namespace com.lifepixer.mangapixer.Server.Features.VolumeScan;

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>Hashes a range of an archive's pages (the worker's <c>page_hash</c>; a fake in tests).</summary>
public interface IVolumePageHasher
{
    Task<PageHashOutcome> HashAsync(PageHashRequest request, CancellationToken ct);
}

/// <summary>The production hasher: a Background slot of the media worker pool (Pause / Slow / Normal while someone reads).</summary>
public sealed class WorkerVolumePageHasher(MediaWorkerPool pool) : IVolumePageHasher
{
    public Task<PageHashOutcome> HashAsync(PageHashRequest request, CancellationToken ct) => pool.HashPagesAsync(request, ct);
}

/// <summary>What one folder's scan did.</summary>
public enum FolderScanOutcome
{
    /// <summary>Not in scope (no unmapped volume file next to a chapter file no list places, "Don't match", doujinshi, chapter mode).</summary>
    OutOfScope = 0,

    /// <summary>Every answer is stored for the current files: nothing to do.</summary>
    UpToDate = 1,

    /// <summary>Hashed what was missing, matched and stored the answers.</summary>
    Scanned = 2,

    /// <summary>Stopped before it finished (switched off, the worker busy or unavailable, shutting down): resumes next time.</summary>
    Stopped = 3,

    /// <summary>
    /// Put off: an archive of the folder is changing or waits for its re-analysis (missing, changed while read, a page count that no
    /// longer matches, a deadline): the folder is tried again on the next sweep; the sweep goes on with the next folder.
    /// </summary>
    Deferred = 4,
}

/// <summary>
/// The volume contents scan of ONE folder (1.44.0): which unmapped volume files and which candidate chapter files it compares (the
/// scope), the page hashes it still needs (chunks of <see cref="PageHashLimits.ChunkPages"/> through <see cref="IVolumePageHasher"/>,
/// stored as it goes - resumable), the match (<see cref="VolumeContentMatcher"/>) and the stored answers (<c>volume_contents</c>). Scope
/// (owner, 1.44.0): only volume files no list maps, next to chapter files no list places; never "Don't match" folders or anything
/// inside them, never a folder whose Content is "Doujinshi &amp; adult one-shots" or whose series record is a doujinshi, never a
/// webtoon / chapter-mode series. Reads the source archives through the worker only; writes only its own tables. Logs ids and counts.
/// </summary>
public sealed class VolumeContentScanner(
    MangaPixerDbContext db,
    VolumeEntryService entries,
    IVolumePageHasher hasher,
    TimeProvider time,
    ILogger<VolumeContentScanner> logger)
{
    /// <summary>The page hasher's version: a change re-hashes every archive (1 = the reduced-decode CoverHash of protocol v6).</summary>
    public const int HashAlgorithm = 1;

    /// <summary>The matcher's rules (T 16, run 5, edges 8 / 16, gap 8): a change re-matches every folder from the stored hashes.</summary>
    public const int RulesVersion = 1;

    private const int MaxDepth = 64;

    /// <summary>
    /// The folders that may hold a scan target, from the names alone (one query per library): a folder (with its unit subfolders merged)
    /// holding at least one archive read as a volume file and one read as a chapter file. The exact scope is decided per folder.
    /// </summary>
    public async Task<IReadOnlyList<long>> CandidateFoldersAsync(CancellationToken ct)
    {
        var archive = (int)CatalogNodeKind.Archive;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var rows = await db.CatalogNodes.AsNoTracking()
            .Where(n => n.Kind == archive && n.Availability != tombstoned && n.ParentId != null)
            .Join(db.CatalogNodes, n => n.ParentId, p => (long?)p.Id, (n, p) => new { n.DisplayName, ParentId = p.Id, ParentName = p.DisplayName, GrandParentId = p.ParentId })
            .ToListAsync(ct);
        var flags = new Dictionary<long, (bool Volume, bool Chapter)>();
        foreach (var r in rows)
        {
            var unitFolder = AutoMatchText.IsUnitFolderName(r.ParentName) && !CountEvidence.IsSideFolderName(r.ParentName) && r.GrandParentId is not null;
            var units = VolumeGrouping.UnitsOf(new GroupingRow(string.Empty, GroupingRowKind.Archive, r.DisplayName, string.Empty, r.ParentName));
            var isVolume = units.Chapter is null && units.Volume is { } v && decimal.Truncate(v) == v && units.VolumeEnd is null;
            var isChapter = units.Chapter is not null && units.Volume is null;
            if (!isVolume && !isChapter)
                continue;
            // A unit subfolder's archives count for its parent too (the Volumes view merges them there).
            foreach (var folder in unitFolder ? [r.ParentId, r.GrandParentId!.Value] : new[] { r.ParentId })
            {
                var f = flags.GetValueOrDefault(folder);
                flags[folder] = (f.Volume || isVolume, f.Chapter || isChapter);
            }
        }
        return flags.Where(f => f.Value.Volume && f.Value.Chapter).Select(f => f.Key).Order().ToList();
    }

    /// <summary>The volume files and candidate chapter files of a folder in scope, or null when the folder is out of scope.</summary>
    internal sealed record FolderScope(long FolderId, List<Target> Volumes, List<Target> Chapters, List<long> OutOfScopeVolumes);

    internal sealed record Target(long NodeId, decimal Number, long ContentVersion, int PageCount, string ArchivePath, long ModificationTicks,
        long ByteLength);

    /// <summary>Scans one folder: hashes what is missing (one request at a time), matches, stores. <paramref name="enabled"/> is asked
    /// before every request (the admin switch, the kill switch).</summary>
    public async Task<FolderScanOutcome> ScanFolderAsync(long folderId, Func<bool> enabled, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var scope = await ScopeAsync(folderId, ct);
        if (scope is null)
            return FolderScanOutcome.OutOfScope;
        await ForgetAsync(scope.OutOfScopeVolumes, ct);
        if (scope.Volumes.Count == 0 || scope.Chapters.Count == 0)
            return FolderScanOutcome.OutOfScope;

        var key = CandidatesKey(scope);
        var volumeIds = scope.Volumes.Select(v => v.NodeId).ToList();
        var stored = await db.VolumeContents.Where(v => volumeIds.Contains(v.VolumeNodeId)).ToDictionaryAsync(v => v.VolumeNodeId, ct);
        if (scope.Volumes.All(v => stored.TryGetValue(v.NodeId, out var s) && s.CandidatesKey == key && s.VolumeContentVersion == v.ContentVersion
            && s.FolderId == folderId))
            return FolderScanOutcome.UpToDate;

        // Volume files first (needed in any case), then the candidate chapter files.
        var hashes = new Dictionary<long, IReadOnlyList<ulong?>>();
        var errors = new Dictionary<long, string>();
        foreach (var target in scope.Volumes.Concat(scope.Chapters))
        {
            var (pages, error, halt) = await EnsureHashedAsync(target, enabled, ct);
            if (halt is { } outcome)
                return outcome;
            if (pages is not null)
                hashes[target.NodeId] = pages;
            else if (error is not null)
                errors[target.NodeId] = error;
        }

        var volumes = scope.Volumes.Where(v => hashes.ContainsKey(v.NodeId))
            .Select(v => new HashedArchive(v.NodeId.ToString(CultureInfo.InvariantCulture), v.Number, hashes[v.NodeId])).ToList();
        var chapters = scope.Chapters.Where(c => hashes.ContainsKey(c.NodeId))
            .Select(c => new HashedArchive(c.NodeId.ToString(CultureInfo.InvariantCulture), c.Number, hashes[c.NodeId])).ToList();
        var results = VolumeContentMatcher.MatchFolder(volumes, chapters);
        var byId = scope.Chapters.ToDictionary(c => c.NodeId);
        var now = time.GetUtcNow();
        var found = 0;
        foreach (var volume in scope.Volumes)
        {
            var result = results.FirstOrDefault(r => r.VolumeId == volume.NodeId.ToString(CultureInfo.InvariantCulture));
            if (!stored.TryGetValue(volume.NodeId, out var row))
            {
                row = new VolumeContentEntity { VolumeNodeId = volume.NodeId };
                db.VolumeContents.Add(row);
            }
            row.VolumeContentVersion = volume.ContentVersion;
            row.Volume = (double)volume.Number;
            row.FolderId = folderId;
            row.CandidatesKey = key;
            row.ScannedAt = now;
            if (errors.TryGetValue(volume.NodeId, out var error))
            {
                (row.State, row.Error, row.ChaptersJson, row.NearMisses, row.CoveredPages) = ((int)VolumeContentRowState.Failed, error, "[]", 0, 0);
                continue;
            }
            if (result is null)
            {
                // Over the matcher's pair guard: nothing proven, retried when the candidates change.
                (row.State, row.Error, row.ChaptersJson, row.NearMisses, row.CoveredPages) = ((int)VolumeContentRowState.Skipped, "too_many_pairs", "[]", 0, 0);
                continue;
            }
            var chapterRows = result.Chapters.Select(f =>
            {
                var c = byId[long.Parse(f.Id, CultureInfo.InvariantCulture)];
                return new StoredFoundChapter(c.NodeId, c.ContentVersion, c.Number, f.FirstPage, f.LastPage, f.Matched, f.Pages, f.Split);
            }).ToList();
            found += chapterRows.Count;
            row.State = result.State switch
            {
                VolumeContentState.Known => (int)VolumeContentRowState.Known,
                VolumeContentState.Partial => (int)VolumeContentRowState.Partial,
                _ => (int)VolumeContentRowState.NoneFound,
            };
            row.Error = null;
            row.ChaptersJson = JsonSerializer.Serialize(chapterRows, VolumeContentReader.Json);
            row.NearMisses = result.NearMisses;
            row.CoveredPages = result.CoveredPages;
        }
        await db.SaveChangesAsync(ct);
        VolumeContentReader.Bump();
        logger.LogInformation(LogEvents.Worker.VolumeScanFolderScanned,
            "Volume contents: folder {FolderId} - {Volumes} volume files, {Candidates} candidate chapter files, {Found} found inside, {Errors} not hashable",
            folderId, scope.Volumes.Count, scope.Chapters.Count, found, errors.Count);
        return FolderScanOutcome.Scanned;
    }

    /// <summary>The scope of one folder (see the class summary), or null when the folder is out of scope.</summary>
    internal async Task<FolderScope?> ScopeAsync(long folderId, CancellationToken ct)
    {
        if (await UnderDontMatchAsync(folderId, ct))
            return null;
        if ((await MetadataFolderContentService.ResolveAsync(db, folderId, ct)).Content == MetadataFolderContent.DoujinshiAndAdultOneShots)
            return null;
        var view = await entries.GetEntriesAsync(folderId, ct);
        if (view is null || view.ChaptersOnly || view.CollectionStackCount > 0)
            return null;
        if (await IsDoujinshiSeriesAsync(folderId, view.SeriesFolderId, ct))
            return null;

        // Every archive row of the view with how the view placed it.
        var placed = new Dictionary<string, (GroupingRow Row, VolumePlacement? Placement)>(StringComparer.Ordinal);
        foreach (var entry in view.Entries)
        {
            if (entry.Stack is { } stack)
            {
                foreach (var m in stack.Members)
                    placed[m.Row.Id] = (m.Row, m.IsVolumeArchive || m.IsBonusVolume ? null : m.Placement);
            }
            else if (entry.Row is { Kind: GroupingRowKind.Archive } row)
            {
                placed.TryAdd(row.Id, (row, null));
            }
        }

        var volumeRows = new List<(string Id, decimal Number)>();
        var chapterRows = new List<(string Id, decimal Number)>();
        var outOfScope = new List<string>();
        foreach (var (row, placement) in placed.Values)
        {
            var units = VolumeGrouping.UnitsOf(row);
            if (units.Chapter is null && units.Volume is { } v && units.VolumeEnd is null && decimal.Truncate(v) == v)
            {
                if (view.ListedVolumes.Contains(v))
                    outOfScope.Add(row.Id);
                else
                    volumeRows.Add((row.Id, v));
            }
            else if (units.Chapter is { } c && units.Volume is null && placement is not VolumePlacement.Exact)
            {
                chapterRows.Add((row.Id, c));
            }
        }

        var internalOf = view.Rows.ToDictionary(p => p.Key, p => p.Value.InternalId, StringComparer.Ordinal);
        var ids = volumeRows.Concat(chapterRows).Select(r => internalOf[r.Id]).ToList();
        var items = await db.ArchiveItems.AsNoTracking()
            .Where(a => ids.Contains(a.NodeId) && a.AnalysisState == 0 && a.PageCount > 0)
            .Join(db.CatalogNodes, a => a.NodeId, n => n.Id, (a, n) => new { a.NodeId, a.ContentVersion, a.PageCount, a.ModificationTicks, a.ByteLength, n.LibraryId, n.RelativePath })
            .Join(db.Libraries, x => x.LibraryId, l => l.Id, (x, l) => new { x.NodeId, x.ContentVersion, x.PageCount, x.ModificationTicks, x.ByteLength, l.RootPath, x.RelativePath })
            .ToDictionaryAsync(x => x.NodeId, ct);
        Target? TargetOf((string Id, decimal Number) r) =>
            items.TryGetValue(internalOf[r.Id], out var x)
                ? new Target(x.NodeId, r.Number, x.ContentVersion, x.PageCount!.Value, Path.Combine(x.RootPath, x.RelativePath), x.ModificationTicks, x.ByteLength)
                : null;
        return new FolderScope(
            folderId,
            volumeRows.Select(TargetOf).OfType<Target>().OrderBy(t => t.Number).ThenBy(t => t.NodeId).ToList(),
            chapterRows.Select(TargetOf).OfType<Target>().OrderBy(t => t.Number).ThenBy(t => t.NodeId).ToList(),
            outOfScope.Select(id => internalOf[id]).ToList());
    }

    /// <summary>Drops stored answers of volume files a list maps now (the list is the authority).</summary>
    private async Task ForgetAsync(IReadOnlyList<long> volumeIds, CancellationToken ct)
    {
        if (volumeIds.Count == 0)
            return;
        var removed = await db.VolumeContents.Where(v => volumeIds.Contains(v.VolumeNodeId)).ExecuteDeleteAsync(ct);
        if (removed > 0)
            VolumeContentReader.Bump();
    }

    /// <summary>
    /// The page hashes of one archive for its current content version, hashing what is still missing chunk by chunk (stored after
    /// every chunk). Returns the pages, or an error code (the archive cannot be hashed until it changes), or stop (switched off, the
    /// worker unavailable, a file changing under us, shutting down).
    /// </summary>
    private async Task<(IReadOnlyList<ulong?>? Pages, string? Error, FolderScanOutcome? Halt)> EnsureHashedAsync(Target target, Func<bool> enabled, CancellationToken ct)
    {
        var row = await db.ArchivePageHashes.FirstOrDefaultAsync(h => h.NodeId == target.NodeId, ct);
        if (row is null || row.ContentVersion != target.ContentVersion || row.Algorithm != HashAlgorithm || row.PageCount != target.PageCount)
        {
            if (row is null)
            {
                row = new ArchivePageHashEntity { NodeId = target.NodeId };
                db.ArchivePageHashes.Add(row);
            }
            row.ContentVersion = target.ContentVersion;
            row.Algorithm = HashAlgorithm;
            row.PageCount = target.PageCount;
            row.HashedPages = 0;
            row.Hashes = new byte[target.PageCount * 8];
            row.Unhashed = new byte[(target.PageCount + 7) / 8];
            row.Error = null;
        }
        if (row.Error is not null)
            return (null, row.Error, null);

        while (row.HashedPages < row.PageCount)
        {
            if (!enabled() || ct.IsCancellationRequested)
                return (null, null, FolderScanOutcome.Stopped);
            var outcome = await hasher.HashAsync(new PageHashRequest
            {
                JobId = "volume-scan",
                ArchivePath = target.ArchivePath,
                ExpectedLastWriteTicks = target.ModificationTicks,
                ExpectedByteLength = target.ByteLength,
                Deadline = time.GetUtcNow() + MediaWorkerPool.PageHashTimeout,
                FromOrdinal = row.HashedPages,
                Count = PageHashLimits.ChunkPages,
            }, ct);
            if (!outcome.Success || outcome.Result is not { } result)
            {
                var code = outcome.ErrorType ?? PageHashErrors.ReadFailed;
                if (code is PageHashErrors.Encrypted or PageHashErrors.TooLarge or PageHashErrors.TooManyPages or PageHashErrors.ReadFailed
                    or PageHashErrors.InvalidRequest)
                {
                    row.Error = code;
                    row.HashedAt = time.GetUtcNow();
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation(LogEvents.Worker.VolumeScanArchiveFailed, "Volume contents: archive {ItemId} not hashable: {Code}", target.NodeId, code);
                    return (null, code, null);
                }
                await db.SaveChangesAsync(CancellationToken.None);
                // busy / cancelled / unavailable / timeout: the worker pool - stop the sweep; missing / source_changed (a rescan
                // follows) / deadline: this archive - put the folder off and go on.
                var halt = code is "busy" or "cancelled" or "unavailable" or "timeout" ? FolderScanOutcome.Stopped : FolderScanOutcome.Deferred;
                logger.LogDebug(LogEvents.Worker.VolumeScanStopped, "Volume contents: archive {ItemId} paused: {Code}", target.NodeId, code);
                return (null, null, halt);
            }
            if (result.PageCount != row.PageCount || result.Pages.Count == 0)
            {
                // The stored analysis no longer matches the file: wait for its re-analysis (a new content version).
                return (null, null, FolderScanOutcome.Deferred);
            }
            foreach (var page in result.Pages)
            {
                if (page.Ordinal < 0 || page.Ordinal >= row.PageCount)
                    continue;
                BinaryPrimitives.WriteUInt64LittleEndian(row.Hashes.AsSpan(page.Ordinal * 8, 8), page.Hash ?? 0);
                if (page.Hash is null)
                    row.Unhashed[page.Ordinal / 8] |= (byte)(1 << (page.Ordinal % 8));
            }
            row.HashedPages = Math.Max(row.HashedPages, result.Pages.Max(p => p.Ordinal) + 1);
            row.HashedAt = time.GetUtcNow();
            // EF does not see in-place changes of an array: mark the BLOBs modified.
            db.Entry(row).Property(r => r.Hashes).IsModified = true;
            db.Entry(row).Property(r => r.Unhashed).IsModified = true;
            await db.SaveChangesAsync(ct);
            // Yield between chunks: the scan never holds a worker longer than one chunk.
            await Task.Yield();
        }
        return (PagesOf(row), null, null);
    }

    internal static IReadOnlyList<ulong?> PagesOf(ArchivePageHashEntity row)
    {
        var pages = new ulong?[row.PageCount];
        for (var i = 0; i < row.PageCount; i++)
        {
            pages[i] = (row.Unhashed[i / 8] & (1 << (i % 8))) != 0
                ? null
                : BinaryPrimitives.ReadUInt64LittleEndian(row.Hashes.AsSpan(i * 8, 8));
        }
        return pages;
    }

    private static string CandidatesKey(FolderScope scope)
    {
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"r{RulesVersion}a{HashAlgorithm}");
        foreach (var t in scope.Volumes)
            text.Append(CultureInfo.InvariantCulture, $"|v{t.NodeId}:{t.ContentVersion}");
        foreach (var t in scope.Chapters)
            text.Append(CultureInfo.InvariantCulture, $"|c{t.NodeId}:{t.ContentVersion}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    /// <summary>True when the folder itself or a folder above it is marked "Don't match" (owner, 1.44.0: no metadata association).</summary>
    private async Task<bool> UnderDontMatchAsync(long folderId, CancellationToken ct)
    {
        var dontMatch = (int)SeriesLinkState.DontMatch;
        long? current = folderId;
        for (var depth = 0; current is { } id && depth < MaxDepth; depth++)
        {
            if (await db.NodeSeriesLinks.AsNoTracking().AnyAsync(l => l.NodeId == id && l.State == dontMatch, ct))
                return true;
            current = await db.CatalogNodes.AsNoTracking().Where(n => n.Id == id).Select(n => n.ParentId).FirstOrDefaultAsync(ct);
        }
        return false;
    }

    /// <summary>True when the folder's series (its own link, or its series folder's) is a doujinshi record.</summary>
    private async Task<bool> IsDoujinshiSeriesAsync(long folderId, long? seriesFolderId, CancellationToken ct)
    {
        var ids = new List<long> { folderId };
        if (seriesFolderId is { } s && s != folderId)
            ids.Add(s);
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        var doujinshi = (int)MetadataFormat.Doujinshi;
        return await db.NodeSeriesLinks.AsNoTracking()
            .Where(l => ids.Contains(l.NodeId) && l.RecordId != null && (l.State == confirmed || l.State == auto))
            .Join(db.MetadataRecords, l => l.RecordId, r => (long?)r.Id, (l, r) => r.Format)
            .AnyAsync(f => f == doujinshi, ct);
    }
}
