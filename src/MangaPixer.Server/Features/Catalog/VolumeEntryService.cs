namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Core.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// The Volumes-view entry list of one folder (1.29.0), ascending by position, with the real rows the entries point at.
/// Not per user: read state, favourites and the switch are applied after paging.
/// </summary>
public sealed record FolderVolumeEntries(
    long FolderId,
    string FolderPublicId,
    long LibraryId,
    string LibraryPublicId,
    IReadOnlyList<VolumeEntry> Entries,
    IReadOnlyDictionary<string, CatalogBrowseService.BrowseRow> Rows,
    int StackCount,
    bool Consolidated,
    long? SeriesFolderId,
    SeriesStatusInfo? Status = null)
{
    /// <summary>
    /// 1.30.0 (reach): the volume key of a volume FILE of this series that already holds a chapter archive, by the archive's public
    /// id - over the whole series scope (the linked folder and its unit subfolders). Set only on a folder with its own link.
    /// </summary>
    public IReadOnlyDictionary<string, string> AlsoInVolume { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// 1.34.0 (owner): the linked series is a webtoon / manhwa / manhua without a real volume list - the view lists its chapters in chapter
    /// order, with no volume placeholders (<see cref="VolumeMapInput.ChaptersOnly"/>).
    /// </summary>
    public bool ChaptersOnly { get; init; }

    /// <summary>
    /// The Volumes view differs from the folder list: a stack, merged unit subfolders, a missing-volume placeholder, or (1.29.0 RC)
    /// a folder with its own link that holds volumes - its header shows the series status - or (1.34.0) a linked folder in chapter mode
    /// that holds archives (its chapter list, with the series status).
    /// </summary>
    public bool Available => StackCount > 0 || CollectionStackCount > 0 || Consolidated || Status is { MissingVolumes: > 0 }
        || (Status is not null && HasVolumes) || (Status is not null && ChaptersOnly && Entries.Any(e => e.Kind == VolumeEntryKind.Archive));

    /// <summary>
    /// 1.37.0 (tankoubon stacks): the stacks of stories collected in one volume - two or more archives here linked to the same record,
    /// in a folder that is neither a series nor a collection (<see cref="StoryCollectionGrouping"/>).
    /// </summary>
    public int CollectionStackCount { get; init; }

    /// <summary>1.37.0: the internal id of the record each story collection's key (the record's public id) names.</summary>
    public IReadOnlyDictionary<string, long> CollectionRecords { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>At least one volume entry (a volume file, a stack or a placeholder).</summary>
    public bool HasVolumes => Entries.Any(e => e.Rank == 0);
}

/// <summary>
/// The series status line of a folder with its own link (1.29.0 RC): the record's publication status and what is missing
/// against what is released in the preferred language. <see cref="ReleaseKnown"/>: a volume total or a released chapter list
/// exists for that language (else "up to date" cannot be said).
/// </summary>
public sealed record SeriesStatusInfo(MetadataOriginStatus? Status, int MissingVolumes, int MissingChapters, bool ReleaseKnown, string Language)
{
    /// <summary>The record's country / language of origin ("Complete (Japan)"), or null.</summary>
    public MetadataOrigin? Origin { get; init; }

    /// <summary>The record's volume total in the country of origin, or null.</summary>
    public int? OriginVolumes { get; init; }

    /// <summary>Volumes published in the preferred language (English publishers today), or null.</summary>
    public int? ReleasedVolumes { get; init; }

    /// <summary>The highest chapter released in the preferred language (the stored released list, else English publishers), or null.</summary>
    public int? ReleasedChapter { get; init; }

    /// <summary>English only: the series is licensed in English (MangaUpdates), or null when unknown / another language.</summary>
    public bool? Licensed { get; init; }

    /// <summary>English only: the scanlation is complete (MangaUpdates), or null when unknown / another language.</summary>
    public bool? ScanlationComplete { get; init; }

    /// <summary>The linked series record (the "covers downloading" note asks the cover pass about it).</summary>
    public long? RecordId { get; init; }

    /// <summary>
    /// 1.30.0: the series' progress over its whole scope (the linked folder and its unit subfolders) - the trackers, the reach, the
    /// upgrades and the completion, from the same engine as the Missing report.
    /// </summary>
    public SeriesProgressDto? Progress { get; init; }
}

/// <summary>
/// Builds the Volumes-view entry list of a folder from stored data only (catalog rows, ComicInfo volume, the linked series'
/// stored volume map): it never contacts a provider. The list is memoised per (folder, catalog revision, link, map versions)
/// for five minutes. Also answers the toggle chain (user switch > folder > library > global).
/// </summary>
public sealed class VolumeEntryService
{
    /// <summary>Unit subfolders are followed this many levels below the folder (the Missing report's depth).</summary>
    public const int MaxUnitDepth = 3;

    private static readonly TimeSpan s_ttl = TimeSpan.FromMinutes(5);

    private readonly MangaPixerDbContext _db;
    private readonly IMemoryCache? _cache;

    public VolumeEntryService(MangaPixerDbContext db, IMemoryCache? cache = null)
    {
        _db = db;
        _cache = cache;
    }

    private sealed record NodeInfo(long Id, string PublicId, long LibraryId, string LibraryPublicId, string Name, long? ParentId);

    /// <summary>
    /// The linked series a folder belongs to: <c>Own</c> = the folder itself carries the link. <c>Collection</c> (1.37.0): the row that
    /// decided is "Collection about" - not a series, but not a folder of stories either.
    /// </summary>
    private sealed record SeriesContext(long? RecordId, long? SeriesFolderId, bool Own, bool Collection = false)
    {
        public string Key => $"{RecordId}:{SeriesFolderId}:{Own}:{Collection}";

        /// <summary>
        /// 1.37.0 (owner): stories collected in one volume stack only in a folder that is neither a series (no own or unit-inherited
        /// Confirmed / Auto link) nor a collection.
        /// </summary>
        public bool StoriesQualify => RecordId is null && !Collection;
    }

    /// <summary>A direct archive child of a qualifying folder with its own Confirmed / Auto link (1.37.0).</summary>
    private sealed record StoryLink(long NodeId, string NodePublicId, long RecordId, string RecordPublicId, string RecordTitle);

    private sealed record UnitFolder(long Id, string Name, long ParentId, bool Generic, List<CatalogBrowseService.BrowseRow> Archives, List<CatalogBrowseService.BrowseRow> SubFolders);

    /// <summary>
    /// The entry list of a folder, or null when the node is not a live folder. A folder with nothing to group returns an
    /// unavailable result (<see cref="FolderVolumeEntries.Available"/> false): the caller browses it flat as before.
    /// </summary>
    public async Task<FolderVolumeEntries?> GetEntriesAsync(long folderId, CancellationToken ct)
    {
        var folder = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.Id == folderId && n.Kind == (int)CatalogNodeKind.Folder && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .Select(n => new { n.Id, n.PublicId, n.LibraryId, LibraryPublicId = n.Library != null ? n.Library.PublicId : "", n.DisplayName, n.ParentId })
            .FirstOrDefaultAsync(ct);
        if (folder is null)
            return null;
        var info = new NodeInfo(folder.Id, folder.PublicId, folder.LibraryId, folder.LibraryPublicId, folder.DisplayName, folder.ParentId);

        var revision = await _db.Libraries.AsNoTracking().Where(l => l.Id == info.LibraryId).Select(l => l.CatalogRevision).FirstOrDefaultAsync(ct);
        var context = await ContextAsync(info, ct);
        // The preferred language matters only for a linked series (what is released in it); an unlinked folder never reads it.
        var language = context.RecordId is null ? ReleasedInLanguage.DefaultLanguage : await ReleasedInLanguage.PreferredAsync(_db, ct);
        var mapKey = await MapKeyAsync(context.RecordId, ct);
        // 1.37.0: the archive links of a folder of stories are part of the key - a story linked or unlinked regroups at once (a link
        // change does not move the catalog revision).
        var stories = context.StoriesQualify ? await StoryLinksAsync(info.Id, ct) : [];
        var key = $"volview:{info.Id}:{revision}:{context.Key}:{language}:{mapKey}:{StoriesKey(stories)}";
        if (_cache is not null && _cache.TryGetValue(key, out FolderVolumeEntries? cached) && cached is not null)
            return cached;

        var built = await BuildAsync(info, context, language, stories, ct);
        _cache?.Set(key, built, s_ttl);
        return built;
    }

    /// <summary>
    /// Whether the viewer sees the Volumes view of a folder (P2.4 chain): an explicit request (<paramref name="groupOverride"/>
    /// <c>volumes</c> / <c>flat</c>), else the user's switch, else the folder override (the folder itself, then its series
    /// folder), else the library override, else the global default (on).
    /// </summary>
    public async Task<bool> IsActiveAsync(long userId, FolderVolumeEntries entries, string? groupOverride, CancellationToken ct)
    {
        if (string.Equals(groupOverride, "volumes", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(groupOverride, "flat", StringComparison.OrdinalIgnoreCase))
            return false;

        var user = await _db.ReaderPreferences.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.SeriesViewMode).FirstOrDefaultAsync(ct);
        if (user is { } mode)
            return mode == (int)SeriesViewMode.Volumes;
        return await IsDefaultActiveAsync(entries, ct);
    }

    /// <summary>
    /// The folder's default without the viewer's own switch: the folder override (the folder itself, then its series folder),
    /// else the library override, else the global default (on). The web client clears the viewer's switch when they choose
    /// this value again, so an admin's default applies to them from then on (1.29.0 RC).
    /// </summary>
    public async Task<bool> IsDefaultActiveAsync(FolderVolumeEntries entries, CancellationToken ct)
    {
        var folderIds = new List<long> { entries.FolderId };
        if (entries.SeriesFolderId is { } series && series != entries.FolderId)
            folderIds.Add(series);
        var overrides = await _db.FolderViewSettings.AsNoTracking()
            .Where(s => folderIds.Contains(s.NodeId) && s.VirtualVolumes != null)
            .Select(s => new { s.NodeId, s.VirtualVolumes })
            .ToListAsync(ct);
        foreach (var id in folderIds)
        {
            if (overrides.FirstOrDefault(o => o.NodeId == id) is { VirtualVolumes: { } value })
                return value == (int)ViewSwitch.On;
        }

        var library = await _db.Libraries.AsNoTracking().Where(l => l.Id == entries.LibraryId).Select(l => l.VirtualVolumes).FirstOrDefaultAsync(ct);
        if (library is { } libraryValue)
            return libraryValue == (int)ViewSwitch.On;

        var global = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId).Select(s => (bool?)s.VirtualVolumesEnabled).FirstOrDefaultAsync(ct);
        return global ?? true;
    }

    // --- Series context -------------------------------------------------------------------------------------------

    /// <summary>
    /// The linked series of a folder: its own Confirmed / Auto link, else - for a unit subfolder (<c>Volumes</c>,
    /// <c>Season 2</c>, ...) with no link of its own - the nearest linked folder above it through unit subfolders only.
    /// A "Don't match" folder (or one in review) has no series: only its own file names group.
    /// </summary>
    private async Task<SeriesContext> ContextAsync(NodeInfo folder, CancellationToken ct)
    {
        var own = await _db.NodeSeriesLinks.AsNoTracking().Where(l => l.NodeId == folder.Id)
            .Select(l => new { l.State, l.RecordId }).FirstOrDefaultAsync(ct);
        if (own is not null)
        {
            return IsLinked(own.State, own.RecordId)
                ? new SeriesContext(own.RecordId, folder.Id, true)
                : new SeriesContext(null, null, false, IsCollection(own.State));
        }

        var current = folder;
        for (var depth = 0; depth < MaxUnitDepth; depth++)
        {
            if (!IsUnitFolder(current.Name) || current.ParentId is not { } parentId)
                break;
            var parent = await _db.CatalogNodes.AsNoTracking().Where(n => n.Id == parentId)
                .Select(n => new { n.Id, n.PublicId, n.LibraryId, n.DisplayName, n.ParentId }).FirstOrDefaultAsync(ct);
            if (parent is null)
                break;
            var link = await _db.NodeSeriesLinks.AsNoTracking().Where(l => l.NodeId == parent.Id)
                .Select(l => new { l.State, l.RecordId }).FirstOrDefaultAsync(ct);
            if (link is not null)
            {
                return IsLinked(link.State, link.RecordId)
                    ? new SeriesContext(link.RecordId, parent.Id, false)
                    : new SeriesContext(null, null, false, IsCollection(link.State));
            }
            current = new NodeInfo(parent.Id, parent.PublicId, parent.LibraryId, string.Empty, parent.DisplayName, parent.ParentId);
        }
        return new SeriesContext(null, null, false);
    }

    private static bool IsLinked(int state, long? recordId) =>
        recordId is not null && state is (int)SeriesLinkState.Confirmed or (int)SeriesLinkState.Auto;

    private static bool IsCollection(int state) => state == (int)SeriesLinkState.CollectionAbout;

    // --- Stories collected in one volume (1.37.0) ---------------------------------------------------------------------

    /// <summary>
    /// The direct archive children of a folder whose OWN link is Confirmed / Auto (<see cref="SeriesLinkStates.IsSeries"/>), with their
    /// record. Many archives may link one record (the unique index is on the node only). One query.
    /// </summary>
    private async Task<List<StoryLink>> StoryLinksAsync(long folderId, CancellationToken ct)
    {
        var rows = (await (
            from l in _db.NodeSeriesLinks.AsNoTracking()
            join n in _db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
            where n.ParentId == folderId && n.Kind == (int)CatalogNodeKind.Archive && n.Availability != (int)CatalogNodeAvailability.Tombstoned
                && l.RecordId != null
            select new { l.NodeId, n.PublicId, l.State, RecordId = l.RecordId!.Value })
            .ToListAsync(ct))
            .Where(r => SeriesLinkStates.IsSeries((SeriesLinkState)r.State))
            .ToList();
        // Most folders have no linked archive: the records are read only when there is one (a second, small query).
        if (rows.Count == 0)
            return [];
        var recordIds = rows.Select(r => r.RecordId).Distinct().ToList();
        var records = await _db.MetadataRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id))
            .Select(r => new { r.Id, r.PublicId, r.Title }).ToDictionaryAsync(r => r.Id, ct);
        return rows.Where(r => records.ContainsKey(r.RecordId))
            .Select(r => new StoryLink(r.NodeId, r.PublicId, r.RecordId, records[r.RecordId].PublicId, records[r.RecordId].Title))
            .OrderBy(r => r.NodeId)
            .ToList();
    }

    /// <summary>The cache-key term of a folder's story links: changes whenever a link that counts is added, removed or re-pointed.</summary>
    private static string StoriesKey(List<StoryLink> stories)
    {
        if (stories.Count == 0)
            return "-";
        var text = string.Join(',', stories.Select(s => $"{s.NodeId}.{s.RecordId}"));
        return $"{stories.Count}.{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)), 0, 8)}";
    }

    private static bool IsUnitFolder(string? name) => AutoMatchText.IsUnitFolderName(name) && !CountEvidence.IsSideFolderName(name);

    // --- The map ------------------------------------------------------------------------------------------------------

    private async Task<string> MapKeyAsync(long? recordId, CancellationToken ct)
    {
        if (recordId is not { } id)
            return "-";
        var maps = await _db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == id)
            .Select(m => new { m.Source, m.State, m.Version, m.ReleasedLanguage, Released = m.ReleasedChaptersJson == null ? -1 : m.ReleasedChaptersJson.Length })
            .ToListAsync(ct);
        var record = await _db.MetadataRecords.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new { r.OriginVolumes, r.OriginStatus, r.FetchedAt }).FirstOrDefaultAsync(ct);
        return string.Join(',', maps.OrderBy(m => m.Source).Select(m => $"{m.Source}.{m.State}.{m.Version}.{m.ReleasedLanguage}.{m.Released}"))
            + $"|{record?.OriginVolumes}|{record?.OriginStatus}|{record?.FetchedAt.UtcTicks}";
    }

    /// <summary>
    /// The stored volume knowledge of a linked record as the pure grouping reads it (P2.3) and what is released in the preferred
    /// language (<see cref="SeriesProgressLoader.MapAndFacts"/>, shared with the Missing report since 1.30.0). Stored rows only.
    /// </summary>
    private async Task<(VolumeMapInput? Map, ReleaseInfo? Release, MetadataOriginStatus? Status, ProgressFacts? Facts)> LoadMapAsync(
        long? recordId, string language, CancellationToken ct)
    {
        if (recordId is not { } id)
            return (null, null, null, null);
        var maps = await _db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == id).ToListAsync(ct);
        var record = await _db.MetadataRecords.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new SeriesProgressLoader.RecordRow(r.Id, r.Origin, r.OriginStatus, r.OriginVolumes, r.StatusText, r.LatestChapter,
                r.PublishersJson, r.LicensedEn, r.TranslationComplete, r.Webtoon))
            .FirstOrDefaultAsync(ct);
        var (map, facts) = SeriesProgressLoader.MapAndFacts(maps, record, language);
        var releasedMap = maps.FirstOrDefault(m => m.Source == (int)VolumeMapSource.MangaDexAggregate && m.ReleasedLanguage is not null);
        var release = ReleasedInLanguage.For(language, record?.PublishersJson, releasedMap?.ReleasedLanguage, releasedMap?.ReleasedChaptersJson);
        // An official volume means its chapters are out in that language (1.30.0): the stacks' placeholders follow the progress.
        return (SeriesProgress.WithOfficialChapters(map), release, (MetadataOriginStatus?)record?.OriginStatus, record is null ? null : facts);
    }

    // --- Building ------------------------------------------------------------------------------------------------------

    private async Task<FolderVolumeEntries> BuildAsync(NodeInfo folder, SeriesContext context, string language, List<StoryLink> stories,
        CancellationToken ct)
    {
        // A unit subfolder of a linked series never groups when the series' numbering restarts across its folders.
        if (!context.Own && context.SeriesFolderId is { } seriesFolder && await SeriesRestartsAsync(seriesFolder, ct))
            return Empty(folder, context);

        var tree = await LoadTreeAsync(folder.Id, deep: context.Own, ct);
        var merged = context.Own ? MergedFolders(tree) : [];

        var rows = new List<GroupingRow>();
        var byId = new Dictionary<string, CatalogBrowseService.BrowseRow>(StringComparer.Ordinal);
        var mergedIds = merged.Select(m => m.Id).ToHashSet();
        var archiveParents = new List<long> { folder.Id };
        archiveParents.AddRange(merged.Select(m => m.Id));
        var comicInfo = await ComicInfoAsync(archiveParents, ct);

        void Add(CatalogBrowseService.BrowseRow row, string? container)
        {
            byId[row.Id] = row;
            var isFolder = row.Kind == (int)CatalogNodeKind.Folder;
            var ci = comicInfo.GetValueOrDefault(row.InternalId);
            rows.Add(new GroupingRow(row.Id, isFolder ? GroupingRowKind.Folder : GroupingRowKind.Archive, row.DisplayName, row.SortKey, container,
                isFolder ? null : ci.Volume, isFolder ? null : ci.Number));
        }

        foreach (var child in tree.RootChildren)
        {
            // A merged unit subfolder has no card of its own in the Volumes view: its archives join this list.
            if (child.Kind == (int)CatalogNodeKind.Folder && mergedIds.Contains(child.InternalId))
                continue;
            Add(child, null);
        }
        foreach (var unit in merged)
        {
            foreach (var archive in unit.Archives)
                Add(archive, unit.Name);
        }

        // 1.37.0: a folder of stories (neither a series nor a collection) - the archives linked to the same record stack first, the rest
        // groups as before, never with a map or missing-volume placeholders (a collected volume's record is not this folder's series).
        if (context.StoriesQualify && stories.Count > 0)
        {
            var keyByRow = stories.ToDictionary(s => s.NodePublicId, s => s.RecordPublicId, StringComparer.Ordinal);
            // Owner (2026-10-08): a stack sorts by its record's title.
            var titleByKey = stories.GroupBy(s => s.RecordPublicId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().RecordTitle, StringComparer.Ordinal);
            var storyGrouping = StoryCollectionGrouping.Group(rows, keyByRow, titleByKey);
            var records = stories.GroupBy(s => s.RecordPublicId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().RecordId, StringComparer.Ordinal);
            return new FolderVolumeEntries(
                folder.Id, folder.PublicId, folder.LibraryId, folder.LibraryPublicId,
                storyGrouping.Entries, byId, storyGrouping.StackCount, Consolidated: false, SeriesFolderId: null)
            {
                CollectionStackCount = storyGrouping.CollectionCount,
                CollectionRecords = records,
            };
        }

        var (map, release, status, facts) = await LoadMapAsync(context.RecordId, language, ct);
        // The series' progress over its whole scope (1.30.0): the same engine and rows as the Missing report.
        var progress = context.Own && context.RecordId is { } linkedRecord
            ? (await new SeriesProgressLoader(_db).LoadAsync([new SeriesProgressTarget(folder.Id, linkedRecord)], ct)).GetValueOrDefault(folder.Id)
            : null;
        // 1.39.0: what an admin declared on the folder itself. Tracking off: no missing / released placeholders and no "available in"
        // markers (no missing or upgrade answer anywhere); an edition override: no missing-volume cards from the regular list (its volumes
        // are not the edition's). The stacks themselves are unchanged.
        var declared = progress?.Result.Facts;
        if (declared?.TrackingOff == true && map is not null)
            map = map with { ReleasedChapters = null, ReleasedVolumeCount = null };
        // Missing-volume placeholders only at the folder with its own link: a Season / Part subfolder holds part of the run.
        var grouping = VolumeGrouping.Group(rows, map,
            markMissingVolumes: context.Own && declared?.VolumeOverride is null && declared?.TrackingOff != true);
        var statusInfo = context.Own && release is not null
            ? new SeriesStatusInfo(status is MetadataOriginStatus.Unknown ? null : status, grouping.MissingVolumeCount, grouping.MissingChapterCount,
                release.Known, release.Language)
            {
                Origin = facts?.Origin,
                OriginVolumes = facts?.OriginVolumes,
                ReleasedVolumes = release.Volumes,
                ReleasedChapter = release.LastChapter ?? release.EnglishChapters,
                // MangaUpdates' "licensed" and "completely scanlated" are about English.
                Licensed = facts?.Licensed,
                ScanlationComplete = facts?.ScanlationComplete,
                RecordId = context.RecordId,
                Progress = progress?.Dto,
            }
            : null;
        return new FolderVolumeEntries(
            folder.Id, folder.PublicId, folder.LibraryId, folder.LibraryPublicId,
            grouping.Entries, byId, grouping.StackCount, Consolidated: merged.Count > 0, context.SeriesFolderId ?? (context.Own ? folder.Id : null),
            statusInfo)
        {
            AlsoInVolume = progress?.Result.Reach.AlsoInVolume ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ChaptersOnly = map?.ChaptersOnly == true,
        };
    }

    private static FolderVolumeEntries Empty(NodeInfo folder, SeriesContext context) =>
        new(folder.Id, folder.PublicId, folder.LibraryId, folder.LibraryPublicId, [],
            new Dictionary<string, CatalogBrowseService.BrowseRow>(), 0, false, context.SeriesFolderId);

    private sealed record Tree(long RootId, List<CatalogBrowseService.BrowseRow> RootChildren, List<UnitFolder> Units);

    /// <summary>
    /// The folder's children and - when <paramref name="deep"/> - its unit subfolders below (up to <see cref="MaxUnitDepth"/>
    /// levels): those with no link of their own (a linked subfolder is a separate work) that are not side material
    /// (Extras, Specials, Colored ...). One batched query per level.
    /// </summary>
    private async Task<Tree> LoadTreeAsync(long rootId, bool deep, CancellationToken ct)
    {
        var rootChildren = await ChildrenAsync([rootId], ct);
        var root = rootChildren.Select(c => c.Row).ToList();
        var units = new List<UnitFolder>();
        if (!deep)
            return new Tree(rootId, root, units);

        var frontier = root.Where(r => r.Kind == (int)CatalogNodeKind.Folder && IsUnitFolder(r.DisplayName)).ToList();
        var parentIds = frontier.ToDictionary(r => r.InternalId, _ => rootId);
        for (var depth = 0; depth < MaxUnitDepth && frontier.Count > 0; depth++)
        {
            var ids = frontier.Select(f => f.InternalId).ToList();
            var linked = (await _db.NodeSeriesLinks.AsNoTracking().Where(l => ids.Contains(l.NodeId)).Select(l => l.NodeId).ToListAsync(ct)).ToHashSet();
            var live = frontier.Where(f => !linked.Contains(f.InternalId)).ToList();
            if (live.Count == 0)
                break;
            var children = await ChildrenAsync(live.Select(f => f.InternalId).ToList(), ct);
            var next = new List<CatalogBrowseService.BrowseRow>();
            foreach (var f in live)
            {
                var own = children.Where(c => c.ParentId == f.InternalId).Select(c => c.Row).ToList();
                var generic = AutoMatchText.IsVolumeFolderName(f.DisplayName) || AutoMatchText.IsChapterFolderName(f.DisplayName);
                units.Add(new UnitFolder(
                    f.InternalId, f.DisplayName, parentIds[f.InternalId], generic,
                    own.Where(r => r.Kind == (int)CatalogNodeKind.Archive).ToList(),
                    own.Where(r => r.Kind != (int)CatalogNodeKind.Archive).ToList()));
                foreach (var sub in own.Where(r => r.Kind == (int)CatalogNodeKind.Folder && IsUnitFolder(r.DisplayName)))
                {
                    parentIds[sub.InternalId] = f.InternalId;
                    next.Add(sub);
                }
            }
            frontier = next;
        }
        return new Tree(rootId, root, units);
    }

    /// <summary>
    /// The generic unit subfolders (<c>Volumes</c>, <c>Chapters</c>, <c>Vol 1-5</c> - never <c>Season N</c> / <c>Part N</c>) that
    /// merge into the linked folder's list: reached from it through generic folders only, holding no subfolder that is not
    /// itself merged, and not restarting the numbering (a restart merges nothing).
    /// </summary>
    private static List<UnitFolder> MergedFolders(Tree tree)
    {
        var byId = tree.Units.ToDictionary(u => u.Id);

        // Reachable through generic folders only.
        var reachable = new HashSet<long>();
        foreach (var unit in tree.Units)
        {
            if (!unit.Generic)
                continue;
            if (unit.ParentId == tree.RootId || reachable.Contains(unit.ParentId))
                reachable.Add(unit.Id);
        }
        // Bottom-up: a folder merges only when every subfolder it holds is merged too (else its card must stay: a subfolder
        // such as Volumes/Extras would become unreachable).
        var mergedIds = new HashSet<long>(reachable);
        bool changed;
        do
        {
            changed = false;
            foreach (var id in mergedIds.ToList())
            {
                var unit = byId[id];
                if (unit.SubFolders.Any(s => !mergedIds.Contains(s.InternalId)))
                {
                    mergedIds.Remove(id);
                    changed = true;
                }
            }
        }
        while (changed);

        var merged = tree.Units.Where(u => mergedIds.Contains(u.Id)).ToList();
        if (merged.Count == 0)
            return merged;
        var folders = new List<MissingFolder>
        {
            new(null, tree.RootChildren.Where(r => r.Kind == (int)CatalogNodeKind.Archive).Select(r => r.DisplayName).ToList()),
        };
        folders.AddRange(merged.Select(m => new MissingFolder(m.Name, m.Archives.Select(a => a.DisplayName).ToList())));
        return MissingUnits.Evaluate(folders, new PublishedTotals()).Verdict == MissingVerdict.Restarts ? [] : merged;
    }

    /// <summary>The Missing report's restart verdict for a whole series (its own archives plus every unit subfolder).</summary>
    private async Task<bool> SeriesRestartsAsync(long seriesFolderId, CancellationToken ct)
    {
        var tree = await LoadTreeAsync(seriesFolderId, deep: true, ct);
        var folders = new List<MissingFolder>
        {
            new(null, tree.RootChildren.Where(r => r.Kind == (int)CatalogNodeKind.Archive).Select(r => r.DisplayName).ToList()),
        };
        folders.AddRange(tree.Units.Select(u => new MissingFolder(u.Name, u.Archives.Select(a => a.DisplayName).ToList())));
        return MissingUnits.Evaluate(folders, new PublishedTotals()).Verdict == MissingVerdict.Restarts;
    }

    private sealed record ChildRow(long ParentId, CatalogBrowseService.BrowseRow Row);

    private async Task<List<ChildRow>> ChildrenAsync(List<long> parentIds, CancellationToken ct)
    {
        var rows = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.ParentId != null && parentIds.Contains(n.ParentId.Value) && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .OrderBy(n => n.SortKey)
            .Select(n => new
            {
                ParentId = n.ParentId!.Value,
                n.PublicId,
                ParentPublicId = n.Parent != null ? n.Parent.PublicId : "",
                LibraryPublicId = n.Library != null ? n.Library.PublicId : "",
                n.Kind,
                n.DisplayName,
                n.Availability,
                PageCount = n.ArchiveItem != null ? n.ArchiveItem.PageCount : null,
                InternalId = n.Id,
                n.CreatedAt,
                n.SortKey,
            })
            .ToListAsync(ct);
        return rows.Select(n => new ChildRow(n.ParentId, new CatalogBrowseService.BrowseRow
        {
            Id = n.PublicId,
            ParentId = n.ParentPublicId,
            LibraryId = n.LibraryPublicId,
            Kind = n.Kind,
            DisplayName = n.DisplayName,
            Availability = n.Availability,
            PageCount = n.PageCount,
            InternalId = n.InternalId,
            CreatedAt = n.CreatedAt,
            SortKey = n.SortKey,
        })).ToList();
    }

    /// <summary>ComicInfo <c>Volume</c> / <c>Number</c> of the archives directly below the given folders (parsed ComicInfo only).</summary>
    private async Task<Dictionary<long, (int? Volume, string? Number)>> ComicInfoAsync(List<long> parentIds, CancellationToken ct)
    {
        var rows = await (
            from e in _db.EmbeddedMetadata.AsNoTracking()
            join n in _db.CatalogNodes.AsNoTracking() on e.NodeId equals n.Id
            where n.ParentId != null && parentIds.Contains(n.ParentId.Value) && e.State == 1 && (e.Volume != null || e.Number != null)
            select new { e.NodeId, e.Volume, e.Number }).ToListAsync(ct);
        return rows.ToDictionary(r => r.NodeId, r => (r.Volume, (string?)r.Number));
    }
}
