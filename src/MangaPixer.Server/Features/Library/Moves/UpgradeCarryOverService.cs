namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Read state that follows a chapter-to-volume upgrade (1.40.0, owner finding of 2026-10-10): a volume archive that replaced its chapter
/// archives in the same folder takes the users' read state of those chapters - READ when every chapter the stored volume list gives for it
/// was read, IN PROGRESS at page 1 when only some were (<see cref="UpgradeCarryOver"/>). Runs inside the move-pairing pass, after the
/// identical-content pairing (a real move wins), so after every scan, after analyses and at startup. Never destructive: a user with a read
/// mark on the volume, or progress on it past page 1, keeps it; the tombstones' own rows stay. Idempotent without a ledger (the lane note,
/// rule R7). Local only; logs ids and counts.
/// </summary>
public sealed class UpgradeCarryOverService
{
    /// <summary>The <see cref="ReadMarkEntity.Source"/> of a read mark this pass writes.</summary>
    public const string MarkSource = "upgrade";

    /// <summary>Unit subfolders are followed this many levels up to the linked folder (the Volumes view's depth).</summary>
    private const int MaxUnitDepth = 3;

    /// <summary>The ancestor walk for the exclusions stops after this many levels (the cover layer's bound).</summary>
    private const int MaxAncestorDepth = 64;

    private readonly MangaPixerDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<UpgradeCarryOverService> _logger;

    public UpgradeCarryOverService(MangaPixerDbContext db, TimeProvider time, ILogger<UpgradeCarryOverService> logger)
    {
        _db = db;
        _time = time;
        _logger = logger;
    }

    /// <summary>What one pass did: volumes marked read / set in progress (per user), distinct users, volumes waiting for analysis.</summary>
    public sealed record PassResult(int MarkedRead, int SetInProgress, int Users, int Waiting)
    {
        public static PassResult None { get; } = new(0, 0, 0, 0);
    }

    private sealed record Tomb(long Id, long LibraryId, long ParentId, string Name, long LastSeenScanRevision, DateTimeOffset? TombstonedAt);

    private sealed record Live(long Id, long LibraryId, long ParentId, string Name, DateTimeOffset CreatedAt, int AnalysisState, long ContentVersion, int? PageCount);

    private sealed record Folder(long Id, long? ParentId, string Name);

    public async Task<PassResult> RunAsync(CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var now = _time.GetUtcNow();
        var windowStart = await MoveEvidence.WindowStartAsync(_db, now, ct);
        var tombstoned = MoveEvidence.Tombstoned;

        // Tombstoned archives inside the window that no move took (one indexed query when there is nothing to do).
        var tombs = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.Kind == 1 && n.Availability == tombstoned && n.ParentId != null && n.TombstonedAt != null && n.TombstonedAt >= windowStart
                && !_db.NodeMoves.Any(m => m.FromNodeId == n.Id))
            .Select(n => new Tomb(n.Id, n.LibraryId, n.ParentId!.Value, n.DisplayName, n.LastSeenScanRevision, n.TombstonedAt))
            .ToListAsync(ct);
        if (tombs.Count == 0)
            return PassResult.None;

        // Live archives of the same folders that state one whole volume and no chapter.
        var parentIds = tombs.Select(t => t.ParentId).Distinct().ToList();
        var live = new List<Live>();
        foreach (var chunk in parentIds.Chunk(500))
        {
            live.AddRange(await _db.CatalogNodes.AsNoTracking()
                .Where(n => n.Kind == 1 && n.Availability != tombstoned && n.ParentId != null && chunk.Contains(n.ParentId.Value))
                .Join(_db.ArchiveItems, n => n.Id, a => a.NodeId,
                    (n, a) => new Live(n.Id, n.LibraryId, n.ParentId!.Value, n.DisplayName, n.CreatedAt, a.AnalysisState, a.ContentVersion, a.PageCount))
                .ToListAsync(ct));
        }
        if (live.Count == 0)
            return PassResult.None;

        var folderIds = live.Select(l => l.ParentId).Distinct().ToList();
        var folders = await _db.CatalogNodes.AsNoTracking().Where(n => folderIds.Contains(n.Id))
            .Select(n => new Folder(n.Id, n.ParentId, n.DisplayName)).ToDictionaryAsync(f => f.Id, ct);
        var comicInfo = await ComicInfoAsync(folderIds, ct);
        UnitNumbers Units(long id, string name, long parent) =>
            UpgradeCarryOver.UnitsOfArchive(name, folders.GetValueOrDefault(parent)?.Name,
                comicInfo.GetValueOrDefault(id).Volume, comicInfo.GetValueOrDefault(id).Number);

        var volumes = live
            .Select(l => (Archive: l, Volume: UpgradeCarryOver.VolumeOf(Units(l.Id, l.Name, l.ParentId))))
            .Where(v => v.Volume is not null)
            .Select(v => (v.Archive, Volume: v.Volume!.Value))
            .ToList();
        if (volumes.Count == 0)
            return PassResult.None;

        var tombsByFolder = tombs.Where(t => volumes.Any(v => v.Archive.ParentId == t.ParentId))
            .GroupBy(t => t.ParentId).ToDictionary(g => g.Key, g => g.ToList());
        var candidateTombs = tombsByFolder.Values.SelectMany(t => t).ToList();
        var lastSeen = await MoveEvidence.LastSeenAtAsync(_db,
            candidateTombs.Select(t => new MoveEvidence.Sighting(t.Id, t.LibraryId, t.LastSeenScanRevision, t.TombstonedAt)).ToList(), ct);
        var running = (await MoveEvidence.RunningScanLibraryIds(_db, now).Distinct().ToListAsync(ct)).ToHashSet();

        int read = 0, inProgress = 0, waiting = 0;
        var users = new HashSet<long>();
        foreach (var folderGroup in volumes.GroupBy(v => v.Archive.ParentId))
        {
            if (!tombsByFolder.TryGetValue(folderGroup.Key, out var folderTombs))
                continue;
            if (running.Contains(folderGroup.First().Archive.LibraryId))
            {
                _logger.LogDebug(LogEvents.Scanning.UpgradeCarryOverSkipped, "Upgrade carry-over: folder {FolderId} left for a running scan", folderGroup.Key);
                continue;
            }
            var list = await VolumeListAsync(folderGroup.Key, folders, ct);
            if (list is null)
            {
                _logger.LogDebug(LogEvents.Scanning.UpgradeCarryOverSkipped, "Upgrade carry-over: folder {FolderId} has no linked series with a volume list", folderGroup.Key);
                continue;
            }
            foreach (var byNumber in folderGroup.GroupBy(v => v.Volume))
            {
                if (byNumber.Count() != 1)
                {
                    _logger.LogDebug(LogEvents.Scanning.UpgradeCarryOverSkipped, "Upgrade carry-over: folder {FolderId} holds {Count} files of one volume", folderGroup.Key, byNumber.Count());
                    continue;
                }
                var (archive, number) = byNumber.Single();
                if (!list.TryGetValue(number, out var listed))
                    continue;
                var units = UpgradeCarryOver.UnitsOf(number, listed);
                // Evidence: replaced files last seen present BEFORE the volume appeared (never two files the catalog saw side by side).
                var files = folderTombs
                    .Where(t => lastSeen.TryGetValue(t.Id, out var seen) && seen < archive.CreatedAt)
                    .Select(t => new UpgradeChapterFile(t.Id, Units(t.Id, t.Name, t.ParentId)))
                    .ToList();
                var coverage = UpgradeCarryOver.Cover(units, files);
                if (coverage.Evidence.Count == 0)
                    continue;
                if (archive.AnalysisState == 1)
                {
                    waiting++;
                    continue;
                }
                if (archive.AnalysisState != 0)
                    continue;
                try
                {
                    var done = await CarryAsync(archive, units, coverage, ct);
                    if (done is null)
                    {
                        waiting++;
                        continue;
                    }
                    read += done.Value.Read.Count;
                    inProgress += done.Value.InProgress.Count;
                    users.UnionWith(done.Value.Read);
                    users.UnionWith(done.Value.InProgress);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _db.ChangeTracker.Clear();
                    _logger.LogWarning(LogEvents.Scanning.UpgradeCarryOverFailed, "Upgrade carry-over failed for node {NodeId}: {Error}", archive.Id, ex.GetType().Name);
                }
            }
        }

        if (read > 0 || inProgress > 0)
            _logger.LogInformation(LogEvents.Scanning.UpgradeCarryOverPass,
                "Upgrade carry-over: {Read} volumes marked read, {InProgress} set in progress, for {Users} users; {Waiting} waiting for analysis, in {ElapsedMs} ms",
                read, inProgress, users.Count, waiting, watch.ElapsedMilliseconds);
        else if (waiting > 0)
            _logger.LogDebug(LogEvents.Scanning.UpgradeCarryOverSkipped, "Upgrade carry-over: {Waiting} volumes waiting for analysis", waiting);
        return new PassResult(read, inProgress, users.Count, waiting);
    }

    /// <summary>
    /// Writes one volume's outcomes per user in one transaction (rules R5 / R6 of the lane note). Null when the volume's manifest is not
    /// stored yet (waiting).
    /// </summary>
    private async Task<(List<long> Read, List<long> InProgress)?> CarryAsync(
        Live archive, UpgradeVolumeUnits units, UpgradeCarryOver.Coverage coverage, CancellationToken ct)
    {
        var pages = await _db.PageEntries.AsNoTracking()
            .Where(p => p.ItemId == archive.Id && p.ContentVersion == archive.ContentVersion)
            .OrderBy(p => p.Ordinal)
            .Select(p => new { p.Ordinal, p.EntryKey })
            .ToListAsync(ct);
        if (pages.Count == 0 || pages[0].Ordinal != 0)
            return null;
        var first = pages[0];
        var last = archive.PageCount is { } count && count > 0 && pages.Any(p => p.Ordinal == count - 1)
            ? pages.First(p => p.Ordinal == count - 1)
            : pages[^1];

        var evidence = coverage.Evidence.ToList();
        var marks = await _db.ReadMarks.AsNoTracking().Where(m => evidence.Contains(m.ItemId))
            .Select(m => new { m.UserId, m.ItemId, m.MarkedAt }).ToListAsync(ct);
        var progress = await _db.ReadingProgress.AsNoTracking().Where(p => evidence.Contains(p.ItemId))
            .Select(p => new { p.UserId, p.ItemId, p.State, p.UpdatedAt, p.CompletedAt }).ToListAsync(ct);
        var userIds = marks.Select(m => m.UserId).Concat(progress.Select(p => p.UserId)).Distinct().ToList();
        if (userIds.Count == 0)
            return ([], []);

        var markedNew = (await _db.ReadMarks.Where(m => m.ItemId == archive.Id && userIds.Contains(m.UserId)).Select(m => m.UserId).ToListAsync(ct)).ToHashSet();
        var newProgress = await _db.ReadingProgress.Where(p => p.ItemId == archive.Id && userIds.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, ct);

        var carriedRead = new List<long>();
        var carriedProgress = new List<long>();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        foreach (var userId in userIds.Order())
        {
            var readFiles = marks.Where(m => m.UserId == userId).Select(m => m.ItemId)
                .Concat(progress.Where(p => p.UserId == userId && p.State == (int)ReadingState.Completed).Select(p => p.ItemId))
                .ToHashSet();
            var outcome = UpgradeCarryOver.Decide(units, coverage, readFiles);
            if (outcome == UpgradeOutcome.Nothing || markedNew.Contains(userId))
                continue;
            newProgress.TryGetValue(userId, out var row);
            // A row that says more than "opened at page 1" wins (completed, or past page 1).
            if (row is not null && (row.State == (int)ReadingState.Completed || row.Ordinal != 0))
                continue;
            var at = marks.Where(m => m.UserId == userId && readFiles.Contains(m.ItemId)).Select(m => m.MarkedAt)
                .Concat(progress.Where(p => p.UserId == userId && readFiles.Contains(p.ItemId)).Select(p => p.CompletedAt ?? p.UpdatedAt))
                .DefaultIfEmpty(_time.GetUtcNow()).Max();

            if (outcome == UpgradeOutcome.Read)
            {
                _db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = archive.Id, MarkedAt = at, Source = MarkSource });
                if (row is null)
                {
                    _db.ReadingProgress.Add(NewRow(userId, archive, last.Ordinal, last.EntryKey, (int)ReadingState.Completed, at, completedAt: at));
                }
                else
                {
                    row.ContentVersion = archive.ContentVersion;
                    row.Ordinal = last.Ordinal;
                    row.EntryKey = last.EntryKey;
                    row.State = (int)ReadingState.Completed;
                    row.CompletedAt ??= at;
                    row.HiddenFromContinue = false;
                    row.UpdatedAt = row.UpdatedAt > at ? row.UpdatedAt : at;
                    row.Revision++;
                }
                carriedRead.Add(userId);
            }
            else if (row is null)
            {
                _db.ReadingProgress.Add(NewRow(userId, archive, first.Ordinal, first.EntryKey, (int)ReadingState.InProgress, at, completedAt: null));
                carriedProgress.Add(userId);
            }
            else if (row.State == (int)ReadingState.Unread)
            {
                row.ContentVersion = archive.ContentVersion;
                row.EntryKey = first.EntryKey;
                row.State = (int)ReadingState.InProgress;
                row.UpdatedAt = row.UpdatedAt > at ? row.UpdatedAt : at;
                row.Revision++;
                carriedProgress.Add(userId);
            }
        }
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _db.ChangeTracker.Clear();
        return (carriedRead, carriedProgress);
    }

    private static ReadingProgressEntity NewRow(long userId, Live archive, int ordinal, string entryKey, int state, DateTimeOffset at, DateTimeOffset? completedAt) => new()
    {
        UserId = userId,
        ItemId = archive.Id,
        ContentVersion = archive.ContentVersion,
        Ordinal = ordinal,
        EntryKey = entryKey,
        NormalizedAnchor = 0.0,
        State = state,
        Revision = 1,
        LastMutationId = string.Empty,
        UpdatedAt = at,
        CompletedAt = completedAt,
    };

    /// <summary>
    /// The exact volume list (volume -> listed chapters) of the series a folder belongs to, by the Volumes view's rule: the folder's own
    /// Confirmed / Auto link, else the nearest linked folder through unit subfolders; any other own row stops it. Null when there is none,
    /// when the folder or an ancestor is Don't match / Collection about / an artist's folder, or when the series is in chapter mode.
    /// </summary>
    private async Task<IReadOnlyDictionary<decimal, IReadOnlyList<decimal>>?> VolumeListAsync(long folderId, Dictionary<long, Folder> known, CancellationToken ct)
    {
        // The folder and its ancestors, nearest first, with their link rows.
        var chain = new List<Folder>();
        var current = known.GetValueOrDefault(folderId) ?? await FolderAsync(folderId, ct);
        for (var depth = 0; current is not null && depth < MaxAncestorDepth; depth++)
        {
            chain.Add(current);
            current = current.ParentId is { } parent ? await FolderAsync(parent, ct) : null;
        }
        var ids = chain.Select(f => f.Id).ToList();
        var links = await _db.NodeSeriesLinks.AsNoTracking().Where(l => ids.Contains(l.NodeId))
            .Select(l => new { l.NodeId, l.State, l.RecordId }).ToDictionaryAsync(l => l.NodeId, ct);
        if (links.Values.Any(l => (SeriesLinkState)l.State is SeriesLinkState.DontMatch or SeriesLinkState.CollectionAbout or SeriesLinkState.ArtistFolder))
            return null;

        long? recordId = null;
        for (var i = 0; i < chain.Count && i <= MaxUnitDepth; i++)
        {
            if (links.TryGetValue(chain[i].Id, out var link))
            {
                if (link.RecordId is { } r && SeriesLinkStates.IsSeries((SeriesLinkState)link.State))
                    recordId = r;
                break;
            }
            // Only a unit subfolder (Volumes, Season 2, ...) inherits from the folder above it.
            if (!AutoMatchText.IsUnitFolderName(chain[i].Name) || CountEvidence.IsSideFolderName(chain[i].Name))
                break;
        }
        if (recordId is not { } id)
            return null;

        var maps = await _db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == id).ToListAsync(ct);
        if (maps.Count == 0)
            return null;
        var record = await _db.MetadataRecords.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new SeriesProgressLoader.RecordRow(r.Id, r.Origin, r.OriginStatus, r.OriginVolumes, r.StatusText, r.LatestChapter,
                r.PublishersJson, r.LicensedEn, r.TranslationComplete, r.Webtoon))
            .FirstOrDefaultAsync(ct);
        var (map, _) = SeriesProgressLoader.MapAndFacts(maps, record, ReleasedInLanguage.DefaultLanguage);
        if (map.ChaptersOnly || map.Volumes.Count == 0)
            return null;
        var result = new Dictionary<decimal, IReadOnlyList<decimal>>();
        foreach (var v in map.Volumes)
            result.TryAdd(v.Volume, v.Chapters);
        return result;
    }

    private async Task<Folder?> FolderAsync(long id, CancellationToken ct) =>
        await _db.CatalogNodes.AsNoTracking().Where(n => n.Id == id).Select(n => new Folder(n.Id, n.ParentId, n.DisplayName)).FirstOrDefaultAsync(ct);

    /// <summary>The ComicInfo volume / number of the archives in these folders (the Volumes view reads them too).</summary>
    private async Task<Dictionary<long, (int? Volume, string? Number)>> ComicInfoAsync(List<long> parentIds, CancellationToken ct)
    {
        var rows = await (
            from e in _db.EmbeddedMetadata.AsNoTracking()
            join n in _db.CatalogNodes.AsNoTracking() on e.NodeId equals n.Id
            where n.ParentId != null && parentIds.Contains(n.ParentId.Value) && e.State == 1 && (e.Volume != null || e.Number != null)
            select new { e.NodeId, e.Volume, e.Number }).ToListAsync(ct);
        return rows.GroupBy(r => r.NodeId).ToDictionary(g => g.Key, g => (g.First().Volume, (string?)g.First().Number));
    }
}
