namespace com.lifepixer.mangapixer.Server.Features.Covers;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>What a decision call did to a node's <c>node_auto_covers</c> row.</summary>
public enum CoverDecisionOutcome
{
    /// <summary>Nothing to decide (not ready, gone) - the row, if any, is left alone.</summary>
    Skipped = 0,

    /// <summary>The inputs did not change since the stored decision (no worker call was made).</summary>
    Unchanged = 1,

    /// <summary>A decision was (re)written.</summary>
    Decided = 2,

    /// <summary>Nothing automatic applies (the file default); a stale row was removed.</summary>
    Cleared = 3,
}

/// <summary>
/// The automatic cover layer's decisions (1.29.0; design 6.3-6.6 with the P2.2 source matrix), written to
/// <c>node_auto_covers</c>. Two phases per node: GATHER reads only the database (names, page 1 size, the linked record, the
/// stored web covers of its MangaDex companion) and hashes those inputs into an <c>InputsKey</c>; only when the key differs
/// from the stored one does EVALUATE spend worker calls (render a crop, hash a thumbnail or a poster - protocol 5 / 4, local
/// files only) and apply the pure <see cref="CoverRules"/>. Nothing here sends a network request. Under Don't match nothing
/// is decided (rows are removed). Logs carry node ids, sources and reasons only.
/// </summary>
public sealed class CoverDecisionService
{
    /// <summary>A subtree decision stops after this many archives (a huge chapter folder is decided over several passes).</summary>
    public const int MaxSubtreeArchives = 5000;

    /// <summary>Bumped when <see cref="CoverRules"/> changes what it decides for the same inputs (see <c>Key</c>).</summary>
    internal const int RulesRevision = 2;

    private const double TallRatio = 2.0;
    private const int MaxMeasuredPages = 400;

    private readonly MangaPixerDbContext _db;
    private readonly CoverCropService _crops;
    private readonly CoverDirectionResolver _direction;
    private readonly ICoverHasher _hasher;
    private readonly ThumbnailStore _thumbnails;
    private readonly MetadataImageStore _posters;
    private readonly CoverFiles _files;
    private readonly ILogger<CoverDecisionService>? _logger;

    public CoverDecisionService(MangaPixerDbContext db, CoverCropService crops, CoverDirectionResolver direction, ICoverHasher hasher,
        ThumbnailStore thumbnails, MetadataImageStore posters, CoverFiles files, ILogger<CoverDecisionService>? logger = null)
    {
        _db = db;
        _crops = crops;
        _direction = direction;
        _hasher = hasher;
        _thumbnails = thumbnails;
        _posters = posters;
        _files = files;
        _logger = logger;
    }

    // ----- context -------------------------------------------------------------------------------------------------

    private sealed record Settings(string PreferredLocale);

    private sealed record StoredCover(long Id, int Kind, int? Volume, string Locale, int StoredVersion, ulong? Hash, string PublicId);

    /// <summary>What a linked series offers the layer: its record, its companion's stored covers.</summary>
    private sealed class SeriesContext
    {
        public required NearestLink Link { get; init; }
        public MetadataRecordEntity? Record { get; init; }
        public long? CompanionId { get; init; }
        public IReadOnlyList<string> OriginLocales { get; init; } = [];
        public IReadOnlyList<StoredCover> Covers { get; init; } = [];

        /// <summary>The same series data seen from another node (its own nearest link: depth, link node).</summary>
        public SeriesContext For(NearestLink link) => new()
        {
            Link = link,
            Record = Record,
            CompanionId = CompanionId,
            OriginLocales = OriginLocales,
            Covers = Covers,
        };

        /// <summary>Changes whenever a stored cover of the companion is added, replaced or removed.</summary>
        public string CoversStamp => string.Create(CultureInfo.InvariantCulture,
            $"{Covers.Count}:{Covers.Sum(c => (long)c.StoredVersion)}:{(Covers.Count == 0 ? 0 : Covers.Max(c => c.Id))}");
    }

    private sealed record ArchiveRow(long Id, string PublicId, long LibraryId, long? ParentId, string DisplayName, string SortKey,
        long ContentVersion, bool Ready, int? Width, int? Height, int? ComicVolume)
    {
        public UnitNumbers Units => AutoMatchText.UnitsOf(DisplayName);

        /// <summary>The volume the archive IS (a volume archive, not a chapter): the name's volume, else ComicInfo's.</summary>
        public int? VolumeNumber
        {
            get
            {
                var u = Units;
                if (u.Chapter is not null)
                    return null;
                if (u.Volume is { } v)
                    return (int)decimal.Truncate(v);
                return ComicVolume;
            }
        }

        public bool NamesNoUnit => Units.IsEmpty && ComicVolume is null;
        public bool Spread => CoverRules.IsSpread(Width, Height);
    }

    // ----- public entry points -------------------------------------------------------------------------------------

    /// <summary>Decides one node (an archive or a folder).</summary>
    public async Task<CoverDecisionOutcome> DecideAsync(long nodeId, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return CoverDecisionOutcome.Skipped;
        var settings = await SettingsAsync(ct);
        var links = await CoverLinks.NearestAsync(_db, [nodeId], ct);
        links.TryGetValue(nodeId, out var link);
        var series = await SeriesAsync(link, settings, ct);

        if (node.Kind == (int)CatalogNodeKind.Archive)
        {
            var rows = await ArchiveRowsAsync([nodeId], ct);
            return rows.Count == 0 ? CoverDecisionOutcome.Skipped : await DecideArchiveAsync(rows[0], series, settings, ct);
        }
        return await DecideFolderAsync(node, series, settings, ct);
    }

    /// <summary>
    /// Decides a folder and everything below it (archives first, so a series folder sees its volume 1's fresh decision; then
    /// the subfolders and the folder). Returns how many rows were (re)written or cleared.
    /// </summary>
    public async Task<int> DecideSubtreeAsync(long folderId, CancellationToken ct)
    {
        var folder = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == folderId, ct);
        if (folder is null || folder.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return 0;
        if (folder.Kind == (int)CatalogNodeKind.Archive)
            return await DecideAsync(folderId, ct) is CoverDecisionOutcome.Decided or CoverDecisionOutcome.Cleared ? 1 : 0;

        var settings = await SettingsAsync(ct);
        var (archiveIds, folderIds) = await DescendantsAsync(folderId, ct);
        var nodeIds = archiveIds.Concat(folderIds).Append(folderId).ToList();
        var links = await CoverLinks.NearestAsync(_db, nodeIds, ct);
        var seriesByRecord = new Dictionary<string, SeriesContext>(StringComparer.Ordinal);

        async Task<SeriesContext> SeriesFor(long id)
        {
            links.TryGetValue(id, out var l);
            var key = string.Create(CultureInfo.InvariantCulture, $"{l.LinkNodeId}:{(int)l.State}:{l.RecordId}");
            if (!seriesByRecord.TryGetValue(key, out var s))
                seriesByRecord[key] = s = await SeriesAsync(l, settings, ct);
            // Cached per series; the link (its depth) is each node's own.
            return s.For(l);
        }

        var changed = 0;
        foreach (var chunk in archiveIds.Take(MaxSubtreeArchives).Chunk(500))
        {
            foreach (var row in await ArchiveRowsAsync(chunk, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (await DecideArchiveAsync(row, await SeriesFor(row.Id), settings, ct) is CoverDecisionOutcome.Decided or CoverDecisionOutcome.Cleared)
                    changed++;
            }
        }
        // Deepest folders first; the folder itself last.
        foreach (var id in folderIds.AsEnumerable().Reverse().Append(folderId))
        {
            var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
            if (node is null)
                continue;
            if (await DecideFolderAsync(node, await SeriesFor(id), settings, ct) is CoverDecisionOutcome.Decided or CoverDecisionOutcome.Cleared)
                changed++;
        }
        return changed;
    }

    // ----- archives ------------------------------------------------------------------------------------------------

    private async Task<CoverDecisionOutcome> DecideArchiveAsync(ArchiveRow row, SeriesContext series, Settings settings, CancellationToken ct)
    {
        if (!row.Ready)
            return CoverDecisionOutcome.Skipped;
        var link = series.Link;
        if (link.IsDontMatch)
            return await ClearAsync(row.Id, ct);

        if (!link.IsLinked)
        {
            // Unlinked: only a spread-shaped volume-like archive gets its front half (no request, no hash needed).
            if (row.VolumeNumber is null || !row.Spread)
                return await ClearAsync(row.Id, ct);
            var direction = await _direction.ResolveAsync(row.Id, null, ct);
            var key = Key("unlinked", row.ContentVersion, direction);
            return await ApplyAsync(row.Id, key, ct, () => Task.FromResult<(CoverDecision?, ulong?)>(
                (CoverDecision.Crop(CoverRules.FrontSide(direction), AutoCoverReason.Spread), null)), series);
        }

        if (row.NamesNoUnit && await IsOneShotAsync(row, link, ct))
        {
            var w1 = WebVolume(series, settings, 1);
            var main = WebMain(series, settings);
            var poster = Poster(series);
            var key = Key("oneshot", row.ContentVersion, Describe(series, w1), Describe(series, main), Describe(series, poster));
            return await ApplyAsync(row.Id, key, ct, async () =>
            {
                var chain = new[] { w1, main, poster };
                var first = chain.FirstOrDefault(c => c is not null);
                if (first is null)
                    return (CoverRules.DecideOneShot(null, chain), null);
                first = await WithHashAsync(first, series, ct);
                var fileHash = await FileHashAsync(row, ct);
                return (CoverRules.DecideOneShot(fileHash, [first]), fileHash);
            }, series);
        }

        if (row.VolumeNumber is not { } volume)
            return await ClearAsync(row.Id, ct);

        var web = WebVolume(series, settings, volume);
        if (!row.Spread && web is null)
            return await ClearAsync(row.Id, ct);
        var dir = row.Spread ? await _direction.ResolveAsync(row.Id, (MetadataOrigin?)series.Record?.Origin, ct) : CoverDirection.LeftToRight;
        var volumeKey = Key("volume", row.ContentVersion, row.Spread, dir, volume, Describe(series, web));
        return await ApplyAsync(row.Id, volumeKey, ct, async () =>
        {
            var front = CoverRules.FrontSide(dir);
            if (web is null)
                return (CoverRules.DecideVolume(spread: true, front, null, null, null), null);
            var w = await WithHashAsync(web, series, ct);
            ulong? localHash;
            ulong? otherHash = null;
            var spread = row.Spread;
            if (spread)
            {
                var crop = await _crops.EnsureAsync(row.Id, front, rerender: true, ct);
                if (crop?.Hash is null)
                {
                    // The crop could not be rendered: decide on the file, as for a single-page cover.
                    spread = false;
                    localHash = await FileHashAsync(row, ct);
                }
                else
                {
                    localHash = crop.Hash;
                    otherHash = (await _crops.EnsureAsync(row.Id, CoverRules.Other(front), rerender: true, ct))?.Hash;
                }
            }
            else
            {
                localHash = await FileHashAsync(row, ct);
            }
            return (CoverRules.DecideVolume(spread, front, localHash, otherHash, w), localHash);
        }, series);
    }

    /// <summary>
    /// An archive is its own work (a one-shot) when its own node carries the link, or its folder carries it and holds this
    /// one archive and nothing else.
    /// </summary>
    private async Task<bool> IsOneShotAsync(ArchiveRow row, NearestLink link, CancellationToken ct)
    {
        if (link.Depth == 0)
            return true;
        if (link.Depth != 1 || row.ParentId is not { } parent)
            return false;
        var children = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.ParentId == parent && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .OrderBy(n => n.Id).Select(n => n.Kind).Take(2).ToListAsync(ct);
        return children.Count == 1 && children[0] == (int)CatalogNodeKind.Archive;
    }

    // ----- folders -------------------------------------------------------------------------------------------------

    private async Task<CoverDecisionOutcome> DecideFolderAsync(CatalogNodeEntity folder, SeriesContext series, Settings settings, CancellationToken ct)
    {
        var link = series.Link;
        if (link.IsDontMatch || !link.IsLinked)
            return await ClearAsync(folder.Id, ct);

        var (archiveIds, folderIds) = await DescendantsAsync(folder.Id, ct);
        if (archiveIds.Count == 0)
            return await ClearAsync(folder.Id, ct);

        if (link.Depth == 0)
        {
            // A folder of one archive is a one-shot: its archive decides, the folder shows it.
            if (archiveIds.Count == 1 && folderIds.Count == 0)
                return await ClearAsync(folder.Id, ct);

            var main = WebMain(series, settings);
            var poster = Poster(series);
            if (await IsWebtoonAsync(series.Record, archiveIds, ct))
            {
                var webtoonKey = Key("webtoon", series.CoversStamp, Describe(series, main), Describe(series, poster));
                return await ApplyAsync(folder.Id, webtoonKey, ct,
                    () => Task.FromResult<(CoverDecision?, ulong?)>((CoverRules.DecideWebtoon(main, poster), null)), series);
            }

            var rows = await ArchiveRowsAsync(archiveIds.Take(MaxSubtreeArchives).ToList(), ct);
            var volume1 = rows.Where(r => r.VolumeNumber == 1).OrderBy(r => r.SortKey, StringComparer.Ordinal).ThenBy(r => r.Id).FirstOrDefault();
            var chapterFolder = !rows.Any(r => r.VolumeNumber is not null);
            var w1 = WebVolume(series, settings, 1);
            var volume1Auto = volume1 is null ? null : await _db.NodeAutoCovers.AsNoTracking().FirstOrDefaultAsync(a => a.NodeId == volume1.Id, ct);
            var seriesKey = Key("series", series.CoversStamp, Describe(series, w1), Describe(series, main), Describe(series, poster), chapterFolder,
                volume1?.Id, volume1?.ContentVersion, volume1Auto?.Source, volume1Auto?.CropSide, volume1Auto?.LocalHash);
            return await ApplyAsync(folder.Id, seriesKey, ct, async () =>
            {
                ulong? localHash = null;
                if (volume1 is not null && w1 is not null)
                {
                    // Volume 1's resolved LOCAL cover: its crop when its decision crops, else its file.
                    // (Its decision stored the hash of that local candidate whenever a web cover was compared.)
                    localHash = volume1Auto?.LocalHash is { } stored
                        ? unchecked((ulong)stored)
                        : volume1Auto is { Source: (int)AutoCoverSource.Crop, CropSide: { } side }
                            ? (await _crops.EnsureAsync(volume1.Id, (CoverCropSide)side, rerender: true, ct))?.Hash
                            : await FileHashAsync(volume1, ct);
                }
                var w = w1 is null ? null : await WithHashAsync(w1, series, ct);
                return (CoverRules.DecideSeriesFolder(volume1 is not null, localHash, chapterFolder, w, main, poster), null);
            }, series);
        }

        // A subfolder of a linked series (Season / Part): the volume its first archive belongs to.
        var first = (await FolderCovers.ResolveArchivesAsync(_db, [folder.Id], ct)).TryGetValue(folder.Id, out var cover)
            ? (await ArchiveRowsAsync([cover.Id], ct)).FirstOrDefault() : null;
        if (first is null)
            return await ClearAsync(folder.Id, ct);
        // Local (the name / ComicInfo states the volume, also "v03 c012"), else Exact (the volume list places the chapter).
        var firstVolume = first.VolumeNumber ?? (first.Units.Volume is { } v ? (int?)(int)decimal.Truncate(v) : null)
            ?? await ExactVolumeOfAsync(series, first.Units.Chapter, ct);
        var web = firstVolume is { } fv ? WebVolume(series, settings, fv) : null;
        if (web is null)
            return await ClearAsync(folder.Id, ct);
        var subKey = Key("sub", first.Id, firstVolume, Describe(series, web));
        return await ApplyAsync(folder.Id, subKey, ct,
            () => Task.FromResult<(CoverDecision?, ulong?)>((CoverRules.DecideSubfolder(web), null)), series);
    }

    /// <summary>The record says webtoon, or (unknown) at least half of up to 400 measured pages are tall strips.</summary>
    private async Task<bool> IsWebtoonAsync(MetadataRecordEntity? record, IReadOnlyList<long> archiveIds, CancellationToken ct)
    {
        if (record?.Webtoon is { } flag)
            return flag;
        var ids = archiveIds.Take(200).ToList();
        var dims = await _db.PageEntries.AsNoTracking()
            .Where(p => ids.Contains(p.ItemId) && p.Width != null && p.Height != null && p.Width > 0)
            .OrderBy(p => p.ItemId).ThenBy(p => p.Ordinal)
            .Select(p => new { p.Width, p.Height })
            .Take(MaxMeasuredPages)
            .ToListAsync(ct);
        return dims.Count >= 20 && dims.Count(d => d.Height >= d.Width * TallRatio) * 2 >= dims.Count;
    }

    /// <summary>The volume the exact volume list (MangaDex) puts a chapter in, or null.</summary>
    private async Task<int?> ExactVolumeOfAsync(SeriesContext series, decimal? chapter, CancellationToken ct)
    {
        if (chapter is not { } c || series.Link.RecordId is not { } recordId)
            return null;
        var json = await _db.SeriesVolumeMaps.AsNoTracking()
            .Where(m => m.RecordId == recordId && m.Source == (int)VolumeMapSource.MangaDexAggregate && m.State == (int)VolumeMapState.Ok)
            .Select(m => m.VolumesJson).FirstOrDefaultAsync(ct);
        return VolumeOfChapter(json, c);
    }

    /// <summary>Reads <c>[{"v":"3","c":["17","18"]}]</c>: the integer volume listing <paramref name="chapter"/>, or null.</summary>
    internal static int? VolumeOfChapter(string? volumesJson, decimal chapter)
    {
        if (string.IsNullOrWhiteSpace(volumesJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(volumesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("v", out var v) || !entry.TryGetProperty("c", out var cs) || cs.ValueKind != JsonValueKind.Array)
                    continue;
                if (!decimal.TryParse(v.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var volume))
                    continue;
                foreach (var ch in cs.EnumerateArray())
                    if (decimal.TryParse(ch.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && n == chapter)
                        return (int)decimal.Truncate(volume);
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    // ----- web candidates ------------------------------------------------------------------------------------------

    private static WebCoverCandidate? WebVolume(SeriesContext series, Settings settings, int volume)
    {
        var stored = series.Covers.Where(c => c.Kind == (int)VolumeCoverKind.Volume && c.Volume == volume)
            .Select(c => (c.Locale, new WebCoverCandidate(AutoCoverSource.WebVolume, c.Id, c.Hash))).ToList();
        return CoverRules.PickLanguage(stored, settings.PreferredLocale, series.OriginLocales);
    }

    private static WebCoverCandidate? WebMain(SeriesContext series, Settings settings)
    {
        var mains = series.Covers.Where(c => c.Kind == (int)VolumeCoverKind.Main).ToList();
        if (mains.Count == 0)
            return null;
        var stored = mains.Select(c => (c.Locale, new WebCoverCandidate(AutoCoverSource.WebMain, c.Id, c.Hash))).ToList();
        // The main cover is one image whatever its language: any stored one will do.
        return CoverRules.PickLanguage(stored, settings.PreferredLocale, series.OriginLocales) is { } picked
            ? picked with { OriginFallback = false }
            : new WebCoverCandidate(AutoCoverSource.WebMain, mains[0].Id, mains[0].Hash);
    }

    private static WebCoverCandidate? Poster(SeriesContext series) =>
        series.Record is { ImageState: 1 } ? new WebCoverCandidate(AutoCoverSource.Poster, null, null) : null;

    /// <summary>Fills in a candidate's hash: the stored one, else the worker hashes the stored file.</summary>
    private async Task<WebCoverCandidate> WithHashAsync(WebCoverCandidate candidate, SeriesContext series, CancellationToken ct)
    {
        if (candidate.Hash is not null)
            return candidate;
        string? path = null;
        if (candidate.Source == AutoCoverSource.Poster && series.Record is { } record)
            path = _posters.PathOf(record.Id, record.ImageVersion);
        else if (candidate.VolumeCoverId is { } id && series.Covers.FirstOrDefault(c => c.Id == id) is { } cover)
            path = _files.VolumeCoverPath(cover.PublicId, cover.StoredVersion);
        if (path is null || !File.Exists(path))
            return candidate;
        return candidate with { Hash = await _hasher.HashFileAsync(path, ct) };
    }

    private async Task<ulong?> FileHashAsync(ArchiveRow row, CancellationToken ct)
    {
        var path = _thumbnails.GetThumbnailPath(row.Id, row.ContentVersion);
        return File.Exists(path) ? await _hasher.HashFileAsync(path, ct) : null;
    }

    /// <summary>A candidate as key text: its kind, row, stored version and hash - or the poster's image version.</summary>
    private static string Describe(SeriesContext series, WebCoverCandidate? c)
    {
        if (c is null)
            return "-";
        var version = c.Source == AutoCoverSource.Poster
            ? series.Record?.ImageVersion ?? 0
            : series.Covers.FirstOrDefault(v => v.Id == c.VolumeCoverId)?.StoredVersion ?? 0;
        return string.Create(CultureInfo.InvariantCulture, $"{(int)c.Source}:{c.VolumeCoverId}:{version}:{c.Hash}:{(c.OriginFallback ? 1 : 0)}");
    }

    // ----- persistence ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the decision when the inputs changed. File decisions that involved no web cover and no spread are not stored
    /// for archives (the file default needs no row); folders always keep their row, it carries the series inputs key.
    /// </summary>
    private async Task<CoverDecisionOutcome> ApplyAsync(long nodeId, string inputsKey, CancellationToken ct,
        Func<Task<(CoverDecision? Decision, ulong? LocalHash)>> evaluate, SeriesContext series)
    {
        var existing = await _db.NodeAutoCovers.FirstOrDefaultAsync(a => a.NodeId == nodeId, ct);
        if (existing is not null && string.Equals(existing.InputsKey, inputsKey, StringComparison.Ordinal))
            return CoverDecisionOutcome.Unchanged;

        var (decision, localHash) = await evaluate();
        if (decision is null)
            return await ClearAsync(nodeId, ct);

        var target = decision.Web?.VolumeCoverId;
        var cropSide = decision.CropSide is { } side ? (int?)side : null;
        if (existing is null)
        {
            existing = new NodeAutoCoverEntity { NodeId = nodeId, Version = 1 };
            _db.NodeAutoCovers.Add(existing);
        }
        else if (existing.Source != (int)decision.Source || existing.CropSide != cropSide || existing.VolumeCoverId != target)
        {
            existing.Version++;
        }
        existing.Source = (int)decision.Source;
        existing.CropSide = cropSide;
        existing.VolumeCoverId = target;
        existing.Reason = (int)decision.Reason;
        existing.LocalHash = localHash is { } h ? unchecked((long)h) : existing.LocalHash;
        existing.InputsKey = inputsKey;
        existing.DecidedAt = DateTimeOffset.UtcNow;
        existing.RecheckAt = decision.NeedsRecheck ? DateTimeOffset.UtcNow + RecheckInterval(series.Record) : null;
        await _db.SaveChangesAsync(ct);
        // A crop exists right after its decision (so the first card view does not wait for the worker); an existing
        // file of this content version is kept. A failed render is retried when the cover is first served.
        if (decision is { Source: AutoCoverSource.Crop, CropSide: { } decidedSide })
            await _crops.EnsureAsync(nodeId, decidedSide, rerender: false, ct);
        _logger?.LogDebug(LogEvents.Metadata.CoverDecided, "Cover decided (node {NodeId}): {Source} / {Reason}", nodeId, decision.Source, decision.Reason);
        return CoverDecisionOutcome.Decided;
    }

    private async Task<CoverDecisionOutcome> ClearAsync(long nodeId, CancellationToken ct)
    {
        foreach (var tracked in _db.ChangeTracker.Entries<NodeAutoCoverEntity>().Where(e => e.Entity.NodeId == nodeId).ToList())
            tracked.State = EntityState.Detached;
        var removed = await _db.NodeAutoCovers.Where(a => a.NodeId == nodeId).ExecuteDeleteAsync(ct);
        return removed > 0 ? CoverDecisionOutcome.Cleared : CoverDecisionOutcome.Unchanged;
    }

    /// <summary>The record's refresh cadence: 90 days for a completed series, else 30.</summary>
    private static TimeSpan RecheckInterval(MetadataRecordEntity? record) =>
        record?.OriginStatus is { } status && status == (int)MetadataOriginStatus.Complete ? TimeSpan.FromDays(90) : TimeSpan.FromDays(30);

    private static string Key(params object?[] parts)
    {
        // The rules revision is part of every key: a rules change re-decides every node once (1.30.0: rules 2 - a cover in
        // the original language never replaces a local cover).
        var text = RulesRevision + "|" + string.Join('|', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture) ?? "-"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..40];
    }

    // ----- loading -------------------------------------------------------------------------------------------------

    private async Task<Settings> SettingsAsync(CancellationToken ct)
    {
        var locale = await _db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => s.MetadataCoverLanguage).FirstOrDefaultAsync(ct);
        return new Settings(string.IsNullOrWhiteSpace(locale) ? "en" : locale);
    }

    private async Task<SeriesContext> SeriesAsync(NearestLink link, Settings settings, CancellationToken ct)
    {
        if (!link.IsLinked)
            return new SeriesContext { Link = link };
        var record = await _db.MetadataRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == link.RecordId, ct);
        var companionId = await CoverSeries.CompanionRecordIdAsync(_db, link.RecordId!.Value, ct);
        if (companionId is null)
            return new SeriesContext { Link = link, Record = record };
        var companionOrigin = await _db.MetadataRecords.AsNoTracking().Where(r => r.Id == companionId).Select(r => r.Origin).FirstOrDefaultAsync(ct);
        var covers = await _db.VolumeCovers.AsNoTracking()
            .Where(v => v.ProviderRecordId == companionId && v.State == (int)VolumeCoverState.Stored && v.StoredVersion > 0 && v.Variant == 0)
            .Select(v => new { v.Id, v.Kind, v.Volume, v.Locale, v.StoredVersion, v.Hash, v.PublicId })
            .ToListAsync(ct);
        var origin = (MetadataOrigin?)(companionOrigin ?? record?.Origin);
        return new SeriesContext
        {
            Link = link,
            Record = record,
            CompanionId = companionId,
            OriginLocales = CoverRules.OriginLocales(origin),
            Covers = covers.Select(c => new StoredCover(c.Id, c.Kind, c.Volume, c.Locale, c.StoredVersion,
                c.Hash is { } h ? unchecked((ulong)h) : null, c.PublicId)).OrderBy(c => c.Id).ToList(),
        };
    }

    private async Task<List<ArchiveRow>> ArchiveRowsAsync(IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];
        var nodes = await _db.CatalogNodes.AsNoTracking()
            .Where(n => ids.Contains(n.Id) && n.Kind == (int)CatalogNodeKind.Archive && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .Select(n => new
            {
                n.Id,
                n.PublicId,
                n.LibraryId,
                n.ParentId,
                n.DisplayName,
                n.SortKey,
                Version = n.ArchiveItem != null ? n.ArchiveItem.ContentVersion : 0,
                Ready = n.ArchiveItem != null && n.ArchiveItem.AnalysisState == 0,
            })
            .ToListAsync(ct);
        var nodeIds = nodes.Select(n => n.Id).ToList();
        var firstPages = await _db.PageEntries.AsNoTracking()
            .Where(p => nodeIds.Contains(p.ItemId) && p.Ordinal == 0)
            .Select(p => new { p.ItemId, p.ContentVersion, p.Width, p.Height })
            .ToListAsync(ct);
        var comicVolumes = await _db.EmbeddedMetadata.AsNoTracking()
            .Where(e => nodeIds.Contains(e.NodeId) && e.Volume != null)
            .Select(e => new { e.NodeId, e.Volume })
            .ToDictionaryAsync(e => e.NodeId, e => e.Volume, ct);
        return nodes.Select(n =>
        {
            var page = firstPages.FirstOrDefault(p => p.ItemId == n.Id && p.ContentVersion == n.Version);
            return new ArchiveRow(n.Id, n.PublicId, n.LibraryId, n.ParentId, n.DisplayName, n.SortKey, n.Version, n.Ready,
                page?.Width, page?.Height, comicVolumes.TryGetValue(n.Id, out var v) ? v : null);
        }).ToList();
    }

    /// <summary>The live archives and folders below a folder (any depth), each in SortKey order; folders in walk order (parents first).</summary>
    private async Task<(List<long> Archives, List<long> Folders)> DescendantsAsync(long folderId, CancellationToken ct)
    {
        var archives = new List<long>();
        var folders = new List<long>();
        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE d(NodeId, Kind, SortKey, Availability, Depth) AS (
                    SELECT cn.Id, cn.Kind, cn.SortKey, cn.Availability, 1 FROM catalog_nodes cn WHERE cn.ParentId = {folderId.ToString(CultureInfo.InvariantCulture)}
                    UNION ALL
                    SELECT cn.Id, cn.Kind, cn.SortKey, cn.Availability, d.Depth + 1 FROM d JOIN catalog_nodes cn ON cn.ParentId = d.NodeId
                    WHERE d.Depth < 64
                )
                SELECT NodeId, Kind FROM d WHERE Availability != {(int)CatalogNodeAvailability.Tombstoned} ORDER BY Depth, SortKey, NodeId;
                """;
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                (reader.GetInt32(1) == (int)CatalogNodeKind.Archive ? archives : folders).Add(reader.GetInt64(0));
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return (archives, folders);
    }
}
