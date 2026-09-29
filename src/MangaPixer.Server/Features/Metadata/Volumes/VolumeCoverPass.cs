namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>Process-wide status of the volume-cover pass (singleton): why it waits, for the status endpoint.</summary>
public sealed class VolumeCoverPassState
{
    public string? WaitingCode { get; private set; }
    public DateTimeOffset? WaitingUntil { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }

    public void Set(string? waitingCode, DateTimeOffset? until, DateTimeOffset at)
    {
        WaitingCode = waitingCode;
        WaitingUntil = until;
        LastRunAt = at;
    }
}

/// <summary>One linked series the pass works on: its MangaUpdates record, the library its calls are made for, and its linked nodes.</summary>
public sealed record VolumeSeries(long RecordId, long LibraryId, IReadOnlyList<long> NodeIds, DateTimeOffset? LastRead, DateTimeOffset LinkedAt);

/// <summary>What one tick did (counts only).</summary>
public sealed record VolumeCoverPassResult(int Requests, int SeriesChecked, int CoversStored, string? WaitingCode);

/// <summary>
/// The background volume-cover / volume-list pass (1.29.0, design 4.1-4.6 without a separate daily cap - owner Q8), run
/// by <see cref="Hosting.MetadataAutoMatchHostedService"/> after the matching pass. Automatic, so it needs everything
/// automatic matching needs (both consents current, the kill switch off), plus "Volume covers from the web" on
/// (<c>Metadata:AutoMatch:VolumeCovers=false</c> switches it off regardless) and MangaDex on the allowlist; it waits
/// (sends nothing, counts nothing) while any of that is missing, while MangaDex asks to slow down, or when the one daily
/// budget is spent. Stored covers and maps stay and keep being served either way.
///
/// A series = a MangaUpdates record linked Confirmed or Auto from a node in a library whose "Fetch from the web" is on,
/// with no "Don't match" folder above it. Order: the most recently read series first, then the newest link. Each tick
/// sends at most <see cref="SliceRequests"/> requests (paced at one per second by the gateway), so newly added folders
/// are still matched promptly:
/// <list type="number">
/// <item>per due series: the MangaDex companion (cross-link), its cover list + volume list, and - when MangaDex gives no
/// volume list - the AniList totals;</item>
/// <item>breadth first across series: every series' volume 1 cover (preferred language, else the original language);</item>
/// <item>then per series the covers of the volumes it holds (a volume in an archive's name or ComicInfo, or a chapter the
/// exact list places). Short-circuit: when the local volume 1 cover already IS the web volume 1 cover, the release has
/// real covers on page 1 - a held volume archive is then only fetched when its page 1 is spread-shaped or shaped
/// unlike volume 1's (chapter-only volumes still are: their stack has no cover of its own).</item>
/// </list>
/// Sends only what the MangaDex / AniList providers send (see AGENTS.md Privacy). Logs carry ids and counts only.
/// </summary>
public sealed class VolumeCoverPass
{
    /// <summary>At most this many requests per tick (about a minute at one request per second).</summary>
    public const int SliceRequests = 60;

    /// <summary>A page 1 at least this wide for its height is a spread (jacket scan).</summary>
    public const double SpreadAspect = 1.2;

    /// <summary>Unit subfolders are followed this many levels below a linked folder.</summary>
    public const int MaxDepth = 4;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataAutoMatchService _autoMatch;
    private readonly MetadataSettingsService _settings;
    private readonly CompanionLinkService _companions;
    private readonly VolumeMapService _maps;
    private readonly VolumeCoverFetcher _fetcher;
    private readonly ICoverHasher _hasher;
    private readonly ThumbnailStore _thumbnails;
    private readonly CoverHashCache _hashCache;
    private readonly VolumeCoverPassState _state;
    private readonly TimeProvider _time;
    private readonly ILogger<VolumeCoverPass> _logger;

    public VolumeCoverPass(
        MangaPixerDbContext db, MetadataAutoMatchService autoMatch, MetadataSettingsService settings, CompanionLinkService companions,
        VolumeMapService maps, VolumeCoverFetcher fetcher, ICoverHasher hasher, ThumbnailStore thumbnails, CoverHashCache hashCache,
        VolumeCoverPassState state, TimeProvider time, ILogger<VolumeCoverPass> logger)
    {
        _db = db;
        _autoMatch = autoMatch;
        _settings = settings;
        _companions = companions;
        _maps = maps;
        _fetcher = fetcher;
        _hasher = hasher;
        _thumbnails = thumbnails;
        _hashCache = hashCache;
        _state = state;
        _time = time;
        _logger = logger;
    }

    private sealed record Settings(bool Enabled, string Language);

    private async Task<Settings> ReadSettingsAsync(CancellationToken ct)
    {
        var row = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataVolumeCoversEnabled, s.MetadataCoverLanguage })
            .FirstOrDefaultAsync(ct);
        return new Settings(row?.MetadataVolumeCoversEnabled ?? true, row?.MetadataCoverLanguage ?? "en");
    }

    /// <summary>
    /// Why the pass cannot send anything now (<c>volume_covers_disabled</c> by config, <c>volume_covers_off</c>, or the
    /// automatic gate's code for MangaDex), or null when it may.
    /// </summary>
    public async Task<(string Code, DateTimeOffset? Until)?> WaitingAsync(CancellationToken ct = default)
    {
        if (_settings.VolumeCoversDisabledByConfig)
            return ("volume_covers_disabled", null);
        if (!(await ReadSettingsAsync(ct)).Enabled)
            return ("volume_covers_off", null);
        return await _autoMatch.CheckGlobalGateAsync(MetadataProviderAllowlist.MangaDex, ct) is { } wait ? (wait.Code, wait.Until) : null;
    }

    /// <summary>One tick of the pass. Never throws for a provider problem; a refusal stops the tick and is reported.</summary>
    public async Task<VolumeCoverPassResult> RunTickAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        if (await WaitingAsync(ct) is { } wait)
        {
            if (_state.WaitingCode != wait.Code)
                _logger.LogInformation(LogEvents.Metadata.VolumeCoverWaiting, "Volume covers waiting: {Code}", wait.Code);
            _state.Set(wait.Code, wait.Until, now);
            return new VolumeCoverPassResult(0, 0, 0, wait.Code);
        }

        var settings = await ReadSettingsAsync(ct);
        var series = await EligibleSeriesAsync(ct);
        var call = MetadataCallContext.Automatic();
        var aniListOpen = true;
        int checkedSeries = 0, stored = 0;
        try
        {
            // 1. Companion + lists of due series.
            foreach (var s in series)
            {
                if (call.RequestsSent >= SliceRequests)
                    break;
                try
                {
                    if (await MetadataStepsAsync(s, settings.Language, call, force: false, allowAniList: aniListOpen, ct) is { } aniListState)
                        aniListOpen = aniListState;
                    checkedSeries++;
                }
                catch (MetadataGatewayException ex) when (!MetadataAutoMatchService.IsRefusal(ex))
                {
                    // Recorded on the companion / map (retried later); the next series goes on.
                }
            }

            // 2. Volume 1 of every series, then 3. the volumes each series holds.
            foreach (var s in series)
            {
                if (call.RequestsSent >= SliceRequests)
                    break;
                if (await DownloadVolumeAsync(s, 1, settings.Language, call, ct))
                    stored++;
            }
            foreach (var s in series)
            {
                if (call.RequestsSent >= SliceRequests)
                    break;
                stored += await DownloadHeldVolumesAsync(s, settings.Language, call, ct);
            }
            _state.Set(null, null, _time.GetUtcNow());
        }
        catch (MetadataGatewayException ex) when (MetadataAutoMatchService.IsRefusal(ex))
        {
            _state.Set(ex.Code, ex.RetryAt, _time.GetUtcNow());
            if (call.RequestsSent > 0 || checkedSeries > 0)
                _logger.LogInformation(LogEvents.Metadata.VolumeCoverPass, "Volume cover pass: {Series} series, {Stored} covers, {Requests} requests; stopped {Code}",
                    checkedSeries, stored, call.RequestsSent, ex.Code);
            return new VolumeCoverPassResult(call.RequestsSent, checkedSeries, stored, ex.Code);
        }

        if (call.RequestsSent > 0)
            _logger.LogInformation(LogEvents.Metadata.VolumeCoverPass, "Volume cover pass: {Series} series, {Stored} covers, {Requests} requests",
                checkedSeries, stored, call.RequestsSent);
        return new VolumeCoverPassResult(call.RequestsSent, checkedSeries, stored, null);
    }

    /// <summary>
    /// Steps 1 for one series: the companion, the lists, the AniList fallback - each only when due (or
    /// <paramref name="force"/>d by an admin). Returns the new "AniList may be asked" flag when an AniList refusal closed
    /// it, else null. MangaDex refusals and failures propagate.
    /// </summary>
    public async Task<bool?> MetadataStepsAsync(
        VolumeSeries s, string preferredLanguage, MetadataCallContext? call, bool force, bool allowAniList, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var series = await _db.MetadataRecords.FirstAsync(r => r.Id == s.RecordId, ct);
        var companion = await _companions.FindAsync(series.Id, CompanionLinkService.MangaDex, ct);
        if (force ? companion?.State != (int)CompanionState.None : CompanionLinkService.IsDue(companion, now))
            companion = await _companions.EnsureMangaDexAsync(series, s.LibraryId, call, ct);

        MetadataRecordEntity? mangaDex = companion?.CompanionRecordId is { } mdId
            ? await _db.MetadataRecords.FirstOrDefaultAsync(r => r.Id == mdId, ct)
            : null;
        var map = await _maps.FindAsync(series.Id, VolumeMapSource.MangaDexAggregate, ct);
        if (mangaDex is not null && (force || VolumeMapService.IsDue(map, now)))
            map = await _maps.RefreshListsAsync(series, mangaDex, s.LibraryId, preferredLanguage, call, ct: ct);

        if (!allowAniList || VolumeMapService.HasVolumeList(mangaDex is null ? null : map) || series.OriginVolumes == 1)
            return null;
        var aniList = await _companions.FindAsync(series.Id, MetadataProviderAllowlist.AniList, ct);
        if (!force && aniList is { NextCheckAt: { } next } && next > now)
            return null;
        try
        {
            await _maps.AniListFallbackAsync(series, mangaDex, s.LibraryId, call, ct);
        }
        catch (MetadataGatewayException ex) when (MetadataAutoMatchService.IsRefusal(ex))
        {
            return false; // AniList refused (removed from the allowlist, slowing us down, ...): MangaDex work goes on.
        }
        catch (MetadataGatewayException)
        {
            await _companions.RecordAniListFailedAsync(series, ct);
        }
        return null;
    }

    /// <summary>
    /// Downloads the cover of <paramref name="volume"/> of a series when it is not stored yet: the preferred-language
    /// cover, else the original-language one (integer volume key, no edition alternate). True when one was stored.
    /// </summary>
    private async Task<bool> DownloadVolumeAsync(VolumeSeries s, int volume, string preferred, MetadataCallContext call, CancellationToken ct)
    {
        if (await MangaDexRecordAsync(s.RecordId, ct) is not { } md)
            return false;
        var cover = await PickCoverAsync(md.Id, md.OriginalLanguage, volume, preferred, ct);
        return cover is not null && await _fetcher.DownloadAsync(cover, s.LibraryId, call, ct);
    }

    private sealed record MangaDexRef(long Id, string? OriginalLanguage);

    private async Task<MangaDexRef?> MangaDexRecordAsync(long seriesRecordId, CancellationToken ct)
    {
        var row = await (
            from c in _db.MetadataCompanions.AsNoTracking()
            where c.RecordId == seriesRecordId && c.Provider == CompanionLinkService.MangaDex && c.CompanionRecordId != null
                && (c.State == (int)CompanionState.Auto || c.State == (int)CompanionState.Confirmed)
            join r in _db.MetadataRecords.AsNoTracking() on c.CompanionRecordId equals r.Id
            select new { r.Id, r.ExtraJson }).FirstOrDefaultAsync(ct);
        return row is null ? null : new MangaDexRef(row.Id, CompanionLinkService.MangaDexExtra.Read(row.ExtraJson).OriginalLanguage);
    }

    /// <summary>The cover row to download for a volume, or null when the right one is stored already (or none is listed).</summary>
    public async Task<VolumeCoverEntity?> PickCoverAsync(long mangaDexRecordId, string? originalLanguage, int volume, string preferred, CancellationToken ct)
    {
        var rows = await _db.VolumeCovers
            .Where(c => c.ProviderRecordId == mangaDexRecordId && c.Kind == (int)VolumeCoverKind.Volume && c.Volume == volume && c.Variant == 0)
            .ToListAsync(ct);
        var wanted = rows.FirstOrDefault(r => r.Locale == preferred);
        if (wanted is not null && wanted.State != (int)VolumeCoverState.Gone && wanted.State != (int)VolumeCoverState.Failed)
            return wanted.State == (int)VolumeCoverState.Listed ? wanted : null;
        var origin = originalLanguage is null ? null : rows.FirstOrDefault(r => r.Locale == originalLanguage);
        return origin is { State: (int)VolumeCoverState.Listed } ? origin : null;
    }

    private sealed record HeldArchive(long Id, string Name, long ContentVersion, int? ComicInfoVolume);

    /// <summary>Step 3 for one series: the covers of the volumes it holds, ascending (with the short-circuit).</summary>
    private async Task<int> DownloadHeldVolumesAsync(VolumeSeries s, string preferred, MetadataCallContext call, CancellationToken ct)
    {
        if (await MangaDexRecordAsync(s.RecordId, ct) is not { } md)
            return 0;
        if (!await _db.VolumeCovers.AnyAsync(c => c.ProviderRecordId == md.Id && c.Kind == (int)VolumeCoverKind.Volume
                && c.Variant == 0 && c.State == (int)VolumeCoverState.Listed, ct))
            return 0; // Nothing left to fetch for this series.

        var archives = await ArchivesBelowAsync(s.NodeIds, ct);
        var map = await _maps.FindAsync(s.RecordId, VolumeMapSource.MangaDexAggregate, ct);
        var chapterToVolume = new Dictionary<string, int>(StringComparer.Ordinal);
        if (map is { State: (int)VolumeMapState.Ok })
        {
            foreach (var entry in VolumeMapJson.Read(map.VolumesJson))
            {
                if (VolumeMapJson.Parse(entry.Volume) is not { } v)
                    continue;
                foreach (var c in entry.Chapters)
                    chapterToVolume.TryAdd(c, (int)decimal.Truncate(v));
            }
        }

        // volume -> the archives that ARE that volume (null list entry = held only through chapters)
        var held = new SortedDictionary<int, List<HeldArchive>>();
        foreach (var archive in archives)
        {
            var units = AutoMatchText.UnitsOf(archive.Name);
            int? volume = units.Volume is { } v ? (int)decimal.Truncate(v) : archive.ComicInfoVolume;
            if (volume is { } asVolume && units.Chapter is null)
            {
                Add(held, asVolume).Add(archive);
            }
            else if (volume is { } named)
            {
                Add(held, named); // a chapter archive that names its volume
            }
            else if (units.Chapter is { } chapter && chapterToVolume.TryGetValue(VolumeMapJson.Canonical(decimal.Truncate(chapter)), out var mapped))
            {
                Add(held, mapped);
            }
        }

        var stored = 0;
        var shortCircuit = await Volume1MatchesAsync(md, held, preferred, ct);
        foreach (var (volume, volumeArchives) in held)
        {
            if (volume == 1 || call.RequestsSent >= SliceRequests)
                continue;
            if (shortCircuit is { } reference && volumeArchives.Count > 0
                && !await OddlyShapedAsync(volumeArchives, reference, ct))
                continue; // Real covers on page 1: this volume's own file cover is the cover.
            var cover = await PickCoverAsync(md.Id, md.OriginalLanguage, volume, preferred, ct);
            if (cover is not null && await _fetcher.DownloadAsync(cover, s.LibraryId, call, ct))
                stored++;
        }
        return stored;

        static List<HeldArchive> Add(SortedDictionary<int, List<HeldArchive>> held, int volume)
        {
            if (!held.TryGetValue(volume, out var list))
                held[volume] = list = [];
            return list;
        }
    }

    /// <summary>
    /// The short-circuit test: the local volume 1 archive's file cover is the same image as the stored web volume 1
    /// cover (hash distance within <see cref="CoverHash.SameMaxDistance"/>). Returns volume 1's page-1 aspect then, else null.
    /// Reads only stored thumbnails (never makes one) - no evidence, no short-circuit.
    /// </summary>
    private async Task<double?> Volume1MatchesAsync(MangaDexRef md, SortedDictionary<int, List<HeldArchive>> held, string preferred, CancellationToken ct)
    {
        if (!held.TryGetValue(1, out var ones) || ones.Count == 0)
            return null;
        var web = await _db.VolumeCovers.AsNoTracking()
            .Where(c => c.ProviderRecordId == md.Id && c.Kind == (int)VolumeCoverKind.Volume && c.Volume == 1 && c.Variant == 0
                && c.State == (int)VolumeCoverState.Stored && c.Hash != null)
            .Select(c => new { c.Locale, c.Hash })
            .ToListAsync(ct);
        var webHash = (web.FirstOrDefault(w => w.Locale == preferred) ?? web.FirstOrDefault())?.Hash;
        if (webHash is not { } w)
            return null;
        var local = ones[0];
        if (await LocalHashAsync(local, ct) is not { } hash || CoverHash.Compare(hash, unchecked((ulong)w)) != CoverVerdict.Same)
            return null;
        return await Page1AspectAsync(local, ct) ?? 0.7;
    }

    private async Task<ulong?> LocalHashAsync(HeldArchive archive, CancellationToken ct)
    {
        if (_hashCache.TryGet(archive.Id, archive.ContentVersion, out var cached))
            return cached;
        var path = _thumbnails.GetThumbnailPath(archive.Id, archive.ContentVersion);
        if (!File.Exists(path))
            return null;
        var hash = await _hasher.HashFileAsync(path, ct);
        if (hash is not null)
            _hashCache.Set(archive.Id, archive.ContentVersion, hash);
        return hash;
    }

    private async Task<double?> Page1AspectAsync(HeldArchive archive, CancellationToken ct)
    {
        var page = await _db.PageEntries.AsNoTracking()
            .Where(p => p.ItemId == archive.Id && p.ContentVersion == archive.ContentVersion && p.Ordinal == 0)
            .Select(p => new { p.Width, p.Height })
            .FirstOrDefaultAsync(ct);
        return page is { Width: > 0, Height: > 0 } ? (double)page.Width!.Value / page.Height!.Value : null;
    }

    /// <summary>True when any of the volume's archives has a spread-shaped page 1, or one shaped unlike volume 1's (by more than 0.1).</summary>
    private async Task<bool> OddlyShapedAsync(List<HeldArchive> archives, double volume1Aspect, CancellationToken ct)
    {
        foreach (var archive in archives)
        {
            if (await Page1AspectAsync(archive, ct) is { } aspect && (aspect >= SpreadAspect || Math.Abs(aspect - volume1Aspect) > 0.1))
                return true;
        }
        return false;
    }

    /// <summary>Live archives in and below the linked nodes (unit subfolders, up to <see cref="MaxDepth"/> levels).</summary>
    private async Task<List<HeldArchive>> ArchivesBelowAsync(IReadOnlyList<long> nodeIds, CancellationToken ct)
    {
        var archive = (int)CatalogNodeKind.Archive;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var found = new Dictionary<long, (string Name, long ContentVersion)>();
        var linkedArchives = await _db.CatalogNodes.AsNoTracking()
            .Where(n => nodeIds.Contains(n.Id) && n.Kind == archive && n.Availability != tombstoned)
            .Join(_db.ArchiveItems, n => n.Id, a => a.NodeId, (n, a) => new { n.Id, n.DisplayName, a.ContentVersion })
            .ToListAsync(ct);
        foreach (var a in linkedArchives)
            found[a.Id] = (a.DisplayName, a.ContentVersion);

        var frontier = nodeIds.ToList();
        for (var depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            var parents = frontier;
            var children = await _db.CatalogNodes.AsNoTracking()
                .Where(n => n.ParentId != null && parents.Contains(n.ParentId.Value) && n.Availability != tombstoned)
                .Select(n => new { n.Id, n.Kind, n.DisplayName })
                .ToListAsync(ct);
            var childArchiveIds = children.Where(c => c.Kind == archive).Select(c => c.Id).ToList();
            var versions = await _db.ArchiveItems.AsNoTracking()
                .Where(a => childArchiveIds.Contains(a.NodeId))
                .Select(a => new { a.NodeId, a.ContentVersion })
                .ToDictionaryAsync(a => a.NodeId, a => a.ContentVersion, ct);
            foreach (var c in children.Where(c => c.Kind == archive && versions.ContainsKey(c.Id)))
                found[c.Id] = (c.DisplayName, versions[c.Id]);
            frontier = children.Where(c => c.Kind == (int)CatalogNodeKind.Folder).Select(c => c.Id).ToList();
        }

        var ids = found.Keys.ToList();
        var comicInfo = await _db.EmbeddedMetadata.AsNoTracking()
            .Where(e => ids.Contains(e.NodeId) && e.Volume != null)
            .Select(e => new { e.NodeId, e.Volume })
            .ToDictionaryAsync(e => e.NodeId, e => e.Volume, ct);
        return found.Select(f => new HeldArchive(f.Key, f.Value.Name, f.Value.ContentVersion, comicInfo.GetValueOrDefault(f.Key))).ToList();
    }

    /// <summary>
    /// The linked series in pass order: MangaUpdates records linked Confirmed / Auto from live nodes in libraries whose
    /// "Fetch from the web" is on, without a "Don't match" folder above the node; most recently read first, then the
    /// newest link.
    /// </summary>
    public async Task<List<VolumeSeries>> EligibleSeriesAsync(CancellationToken ct = default)
    {
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var links = await (
            from l in _db.NodeSeriesLinks.AsNoTracking()
            where (l.State == confirmed || l.State == auto) && l.RecordId != null
            join r in _db.MetadataRecords.AsNoTracking() on l.RecordId equals r.Id
            where r.Provider == MetadataProviderAllowlist.MangaUpdates
            join lib in _db.Libraries.AsNoTracking() on l.LibraryId equals lib.Id
            where lib.MetadataEnabled
            join n in _db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
            where n.Availability != tombstoned
            select new { l.NodeId, l.LibraryId, RecordId = r.Id, l.UpdatedAt, n.ParentId })
            .ToListAsync(ct);
        if (links.Count == 0)
            return [];

        var excluded = await UnderDontMatchAsync(links.Select(l => (l.NodeId, l.ParentId)).ToList(), ct);
        var kept = links.Where(l => !excluded.Contains(l.NodeId)).ToList();
        var nodeIds = kept.Select(l => l.NodeId).ToList();
        var reads = await (
            from p in _db.ReadingProgress.AsNoTracking()
            join c in _db.CatalogNodes.AsNoTracking() on p.ItemId equals c.Id
            where nodeIds.Contains(c.Id) || (c.ParentId != null && nodeIds.Contains(c.ParentId.Value))
            select new { c.Id, c.ParentId, p.UpdatedAt })
            .ToListAsync(ct);
        var lastRead = new Dictionary<long, DateTimeOffset>();
        foreach (var r in reads)
        {
            foreach (var key in new[] { r.Id, r.ParentId ?? 0 })
            {
                if (key != 0 && (!lastRead.TryGetValue(key, out var at) || r.UpdatedAt > at))
                    lastRead[key] = r.UpdatedAt;
            }
        }

        return kept
            .GroupBy(l => l.RecordId)
            .Select(g => new VolumeSeries(
                g.Key,
                g.OrderBy(l => l.LibraryId).First().LibraryId,
                g.Select(l => l.NodeId).Distinct().ToList(),
                g.Select(l => lastRead.TryGetValue(l.NodeId, out var at) ? at : (DateTimeOffset?)null).Max(),
                g.Max(l => l.UpdatedAt)))
            .OrderByDescending(s => s.LastRead.HasValue)
            .ThenByDescending(s => s.LastRead)
            .ThenByDescending(s => s.LinkedAt)
            .ThenBy(s => s.RecordId)
            .ToList();
    }

    /// <summary>The nodes (of <paramref name="nodes"/>) that have a "Don't match" folder somewhere above them.</summary>
    private async Task<HashSet<long>> UnderDontMatchAsync(IReadOnlyList<(long NodeId, long? ParentId)> nodes, CancellationToken ct)
    {
        var dontMatch = (await _db.NodeSeriesLinks.AsNoTracking()
            .Where(l => l.State == (int)SeriesLinkState.DontMatch)
            .Select(l => l.NodeId)
            .ToListAsync(ct)).ToHashSet();
        var excluded = new HashSet<long>();
        if (dontMatch.Count == 0)
            return excluded;

        var parentOf = new Dictionary<long, long?>();
        foreach (var (id, parent) in nodes)
            parentOf[id] = parent;
        var frontier = nodes.Select(n => n.ParentId).OfType<long>().Distinct().Where(p => !parentOf.ContainsKey(p)).ToList();
        for (var depth = 0; depth < 64 && frontier.Count > 0; depth++)
        {
            var batch = frontier;
            var rows = await _db.CatalogNodes.AsNoTracking().Where(n => batch.Contains(n.Id)).Select(n => new { n.Id, n.ParentId }).ToListAsync(ct);
            foreach (var row in rows)
                parentOf[row.Id] = row.ParentId;
            frontier = rows.Select(r => r.ParentId).OfType<long>().Distinct().Where(p => !parentOf.ContainsKey(p)).ToList();
        }

        foreach (var (id, _) in nodes)
        {
            var current = parentOf.GetValueOrDefault(id);
            for (var hops = 0; current is { } p && hops < 64; hops++)
            {
                if (dontMatch.Contains(p))
                {
                    excluded.Add(id);
                    break;
                }
                current = parentOf.GetValueOrDefault(p);
            }
        }
        return excluded;
    }

    /// <summary>The status for the Automatic matching card (counts only).</summary>
    public async Task<CoverPassStatusDto> StatusAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var series = await EligibleSeriesAsync(ct);
        var ids = series.Select(s => s.RecordId).ToList();
        var companions = await _db.MetadataCompanions.AsNoTracking()
            .Where(c => ids.Contains(c.RecordId) && c.Provider == CompanionLinkService.MangaDex)
            .ToListAsync(ct);
        var maps = await _db.SeriesVolumeMaps.AsNoTracking()
            .Where(m => ids.Contains(m.RecordId) && m.Source == (int)VolumeMapSource.MangaDexAggregate)
            .ToListAsync(ct);
        var pending = series.Count(s =>
        {
            var c = companions.FirstOrDefault(x => x.RecordId == s.RecordId);
            if (CompanionLinkService.IsDue(c, now))
                return true;
            return c?.CompanionRecordId is not null && VolumeMapService.IsDue(maps.FirstOrDefault(m => m.RecordId == s.RecordId), now);
        });
        var wait = await WaitingAsync(ct);
        return new CoverPassStatusDto
        {
            SeriesPending = pending,
            CoversListed = await _db.VolumeCovers.CountAsync(c => c.State == (int)VolumeCoverState.Listed && c.Kind == (int)VolumeCoverKind.Volume, ct),
            CoversStored = await _db.VolumeCovers.CountAsync(c => c.State == (int)VolumeCoverState.Stored, ct),
            Waiting = wait?.Code ?? _state.WaitingCode,
        };
    }
}
