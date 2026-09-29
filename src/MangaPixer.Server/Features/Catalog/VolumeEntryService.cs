namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Globalization;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Persistence;
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
    long? SeriesFolderId)
{
    /// <summary>The Volumes view differs from the folder list: at least one stack, or merged unit subfolders.</summary>
    public bool Available => StackCount > 0 || Consolidated;
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

    /// <summary>The linked series a folder belongs to: <c>Own</c> = the folder itself carries the link.</summary>
    private sealed record SeriesContext(long? RecordId, long? SeriesFolderId, bool Own)
    {
        public string Key => $"{RecordId}:{SeriesFolderId}:{Own}";
    }

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
        var mapKey = await MapKeyAsync(context.RecordId, ct);
        var key = $"volview:{info.Id}:{revision}:{context.Key}:{mapKey}";
        if (_cache is not null && _cache.TryGetValue(key, out FolderVolumeEntries? cached) && cached is not null)
            return cached;

        var built = await BuildAsync(info, context, ct);
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
                : new SeriesContext(null, null, false);
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
                return IsLinked(link.State, link.RecordId) ? new SeriesContext(link.RecordId, parent.Id, false) : new SeriesContext(null, null, false);
            current = new NodeInfo(parent.Id, parent.PublicId, parent.LibraryId, string.Empty, parent.DisplayName, parent.ParentId);
        }
        return new SeriesContext(null, null, false);
    }

    private static bool IsLinked(int state, long? recordId) =>
        recordId is not null && state is (int)SeriesLinkState.Confirmed or (int)SeriesLinkState.Auto;

    private static bool IsUnitFolder(string? name) => AutoMatchText.IsUnitFolderName(name) && !CountEvidence.IsSideFolderName(name);

    // --- The map ------------------------------------------------------------------------------------------------------

    private async Task<string> MapKeyAsync(long? recordId, CancellationToken ct)
    {
        if (recordId is not { } id)
            return "-";
        var maps = await _db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == id)
            .Select(m => new { m.Source, m.State, m.Version }).ToListAsync(ct);
        var record = await _db.MetadataRecords.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new { r.OriginVolumes, r.OriginStatus }).FirstOrDefaultAsync(ct);
        return string.Join(',', maps.OrderBy(m => m.Source).Select(m => $"{m.Source}.{m.State}.{m.Version}")) + $"|{record?.OriginVolumes}|{record?.OriginStatus}";
    }

    /// <summary>
    /// The stored volume knowledge of a linked record as the pure grouping reads it (P2.3): MangaDex's exact list when its map
    /// is Ok, the chapters-per-volume ratio (its own average, else the AniList row's), the highest volume the provider knows
    /// (else the record's volume total) and whether the series still runs. Stored rows only.
    /// </summary>
    private async Task<VolumeMapInput?> LoadMapAsync(long? recordId, CancellationToken ct)
    {
        if (recordId is not { } id)
            return null;
        var maps = await _db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == id).ToListAsync(ct);
        var record = await _db.MetadataRecords.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new { r.OriginVolumes, r.OriginStatus }).FirstOrDefaultAsync(ct);
        var mangadex = maps.FirstOrDefault(m => m.Source == (int)VolumeMapSource.MangaDexAggregate && m.State == (int)VolumeMapState.Ok);
        var aniList = maps.FirstOrDefault(m => m.Source == (int)VolumeMapSource.AniListRatio && m.State == (int)VolumeMapState.Ok);
        var volumes = ParseVolumes(mangadex?.VolumesJson);
        var ratio = mangadex?.ChaptersPerVolume ?? aniList?.ChaptersPerVolume;
        var known = mangadex?.KnownVolumeCount ?? aniList?.KnownVolumeCount ?? record?.OriginVolumes;
        var status = (MetadataOriginStatus?)record?.OriginStatus;
        var ongoing = status is not (MetadataOriginStatus.Complete or MetadataOriginStatus.Cancelled);
        var source = volumes.Count > 0 ? VolumeListSource.MangaDex : ratio is not null ? VolumeListSource.AniList : VolumeListSource.FileNames;
        return new VolumeMapInput(volumes, ratio, known, ongoing, source);
    }

    /// <summary><c>[{"v":"3","c":["17","18","25.5"]}]</c> -> volumes; malformed entries are skipped.</summary>
    private static List<VolumeMapVolume> ParseVolumes(string? json)
    {
        var result = new List<VolumeMapVolume>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("v", out var v) || !TryNumber(v.GetString(), out var volume)
                    || !item.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                var chapters = new List<decimal>();
                foreach (var chapter in c.EnumerateArray())
                {
                    if (chapter.ValueKind == JsonValueKind.String && TryNumber(chapter.GetString(), out var n))
                        chapters.Add(n);
                }
                if (chapters.Count > 0)
                    result.Add(new VolumeMapVolume(volume, chapters.Order().ToList()));
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return result;
    }

    private static bool TryNumber(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value >= 0;

    // --- Building ------------------------------------------------------------------------------------------------------

    private async Task<FolderVolumeEntries> BuildAsync(NodeInfo folder, SeriesContext context, CancellationToken ct)
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

        var map = await LoadMapAsync(context.RecordId, ct);
        var grouping = VolumeGrouping.Group(rows, map);
        return new FolderVolumeEntries(
            folder.Id, folder.PublicId, folder.LibraryId, folder.LibraryPublicId,
            grouping.Entries, byId, grouping.StackCount, Consolidated: merged.Count > 0, context.SeriesFolderId ?? (context.Own ? folder.Id : null));
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
        return rows.ToDictionary(r => r.NodeId, r => (r.Volume, r.Number));
    }
}
