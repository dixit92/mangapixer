namespace com.lifepixer.mangapixer.Server.Features.Metadata.Review;

using System.Globalization;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Flags;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The admin review dashboard (metadata stage 2). Tabs are keyset-paged lists
/// (cursor = the last row id) with an optional library filter:
/// - Needs review / Auto-linked / Confirmed / Don't match: own link rows of that
///   state on live nodes (Auto-linked lists work anchors only, not archive-group
///   members, newest first). Needs review lists the rows an admin set aside
///   ("Later", 1.33.0) after all the others, oldest set-aside first, with a
///   compound cursor (see <see cref="ReviewCursor"/>);
/// - Unmatched: decided works with no confident match (and works that failed);
/// - Flags: anchors with open flags, automatic links first;
/// - Missing folders: removed folders still carrying admin rows.
/// Reading never contacts a provider. Accept links a STORED candidate (one gated GET
/// when its record is not stored yet); bulk actions reuse the stage-1 link service.
/// </summary>
public sealed class MetadataReviewService
{
    public const int MaxBulk = 200;
    private const int TrailDepth = 3;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataLinkService _links;
    private readonly MetadataIdentifyService _identify;
    private readonly MetadataAutoMatchService _autoMatch;
    private readonly MetadataCarryOverService _carryOver;
    private readonly AuditService _audit;
    private readonly ILogger<MetadataReviewService> _logger;
    private readonly ICoverResolver _covers;
    private readonly TimeProvider _time;
    private readonly Collections.CollectionAboutService? _collections;
    private readonly Artists.ArtistFolderService? _artists;

    public MetadataReviewService(
        MangaPixerDbContext db,
        MetadataLinkService links,
        MetadataIdentifyService identify,
        MetadataAutoMatchService autoMatch,
        MetadataCarryOverService carryOver,
        AuditService audit,
        ILogger<MetadataReviewService> logger,
        TimeProvider time,
        Collections.CollectionAboutService? collections = null,
        Artists.ArtistFolderService? artists = null)
    {
        _time = time;
        _collections = collections;
        _artists = artists;
        _db = db;
        _links = links;
        _identify = identify;
        _autoMatch = autoMatch;
        _carryOver = carryOver;
        _audit = audit;
        _logger = logger;
        // The review shows the admin's OWN cover next to the provider's (1.31.1, owner): always the file cover (an archive's page 1,
        // a folder's first live archive's page 1), never the cover layer - a linked series' resolved cover can be the very web
        // cover it is compared with, which made both sides look the same.
        _covers = new FileCoverResolver(db, versioned: true);
    }

    // --- Summary ---

    public async Task<MetadataReviewSummaryDto?> SummaryAsync(string? libraryPublicId, CancellationToken ct = default)
    {
        var (ok, libraryId) = await LibraryFilterAsync(libraryPublicId, ct);
        if (!ok)
            return null;
        return new MetadataReviewSummaryDto
        {
            NeedsReview = await LinksOf(SeriesLinkState.NeedsReview, libraryId).CountAsync(ct),
            Later = await LinksOf(SeriesLinkState.NeedsReview, libraryId).CountAsync(l => l.LaterAt != null, ct),
            AutoLinked = await AutoAnchors(libraryId).CountAsync(ct),
            Unmatched = await UnmatchedRows(libraryId).CountAsync(ct),
            OpenFlags = await OpenFlags(libraryId).CountAsync(ct),
            DontMatch = await LinksOf(SeriesLinkState.DontMatch, libraryId).CountAsync(ct),
            Confirmed = await LinksOf(SeriesLinkState.Confirmed, libraryId).CountAsync(ct),
            Collections = await LinksOf(SeriesLinkState.CollectionAbout, libraryId).CountAsync(ct),
            ArtistFolders = await LinksOf(SeriesLinkState.ArtistFolder, libraryId).CountAsync(ct),
            MissingFolders = await _carryOver.StrandedFolderIds(libraryId).CountAsync(ct),
            Pending = await _db.MetadataMatchQueue
                .Where(q => (q.State == QueueState.Pending || q.State == QueueState.Leased) && (libraryId == null || q.LibraryId == libraryId))
                .CountAsync(ct),
            RecheckPending = await _autoMatch.RecheckPendingAsync(libraryId, ct),
        };
    }

    // --- Tab queries (ids + keyset key) ---

    private IQueryable<NodeSeriesLinkEntity> LinksOf(SeriesLinkState state, long? libraryId)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var s = (int)state;
        return _db.NodeSeriesLinks.AsNoTracking()
            .Where(l => l.State == s && (libraryId == null || l.LibraryId == libraryId)
                && _db.CatalogNodes.Any(n => n.Id == l.NodeId && n.Availability != tombstoned));
    }

    /// <summary>1.37.0: the Collections tab - "Collection about" folders and artist folders, on live nodes.</summary>
    private IQueryable<NodeSeriesLinkEntity> CollectionTabRows(long? libraryId)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var collection = (int)SeriesLinkState.CollectionAbout;
        var artist = (int)SeriesLinkState.ArtistFolder;
        return _db.NodeSeriesLinks.AsNoTracking()
            .Where(l => (l.State == collection || l.State == artist) && (libraryId == null || l.LibraryId == libraryId)
                && _db.CatalogNodes.Any(n => n.Id == l.NodeId && n.Availability != tombstoned));
    }

    /// <summary>Automatic links on work anchors (archive-group members have no queue row of their own).</summary>
    private IQueryable<NodeSeriesLinkEntity> AutoAnchors(long? libraryId) =>
        LinksOf(SeriesLinkState.Auto, libraryId).Where(l => _db.MetadataMatchQueue.Any(q => q.NodeId == l.NodeId));

    private IQueryable<MetadataMatchQueueEntity> UnmatchedRows(long? libraryId)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var unmatched = (int)MatchBand.Unmatched;
        return _db.MetadataMatchQueue.AsNoTracking()
            .Where(q => (libraryId == null || q.LibraryId == libraryId)
                && ((q.Outcome == unmatched && (q.State == QueueState.Done || q.State == QueueState.Pending)) || q.State == QueueState.Failed)
                && !_db.NodeSeriesLinks.Any(l => l.NodeId == q.NodeId)
                && _db.CatalogNodes.Any(n => n.Id == q.NodeId && n.Availability != tombstoned));
    }

    private IQueryable<MetadataFlagEntity> OpenFlags(long? libraryId) =>
        _db.MetadataFlags.AsNoTracking().Where(f => f.State == (int)MetadataFlagState.Open && (libraryId == null || f.LibraryId == libraryId));

    // --- List ---

    /// <param name="later">Needs review only: true lists only the rows set aside ("Later"), false only the others, null both.</param>
    /// <param name="author">Needs review only (1.33.0): only the works of this author group (<see cref="MetadataReviewGroupHintDto.Key"/>).</param>
    /// <param name="folder">Needs review only (1.33.0): only the works directly in this folder (its node id). Not with <paramref name="author"/>.</param>
    public async Task<(string? Error, MetadataReviewPageDto? Page)> ListAsync(
        MetadataReviewTab tab, string? libraryPublicId, string? cursor, int limit, CancellationToken ct = default, bool? later = null,
        string? author = null, string? folder = null)
    {
        if (!Enum.IsDefined(tab))
            return ("invalid_tab", null);
        var (ok, libraryId) = await LibraryFilterAsync(libraryPublicId, ct);
        if (!ok)
            return ("library_not_found", null);
        limit = Math.Clamp(limit, 1, 100);
        long? after = long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : null;

        List<(string Key, long NodeId)> rows;
        int total;
        ReviewGroupIndex? groups = null;
        switch (tab)
        {
            case MetadataReviewTab.NeedsReview:
                {
                    if (!string.IsNullOrEmpty(author) && !string.IsNullOrEmpty(folder))
                        return ("invalid_filter", null);
                    groups = await GroupIndexAsync(MetadataReviewTab.NeedsReview, libraryId, ct);
                    var q = LinksOf(SeriesLinkState.NeedsReview, libraryId);
                    if (later is { } onlyLater)
                        q = q.Where(l => (l.LaterAt != null) == onlyLater);
                    if (!string.IsNullOrEmpty(author))
                    {
                        var members = groups.Find(author)?.NodeIds.ToList() ?? [];
                        q = q.Where(l => members.Contains(l.NodeId));
                    }
                    if (!string.IsNullOrEmpty(folder))
                    {
                        var folderId = await _db.CatalogNodes.Where(n => n.PublicId == folder).Select(n => (long?)n.Id).FirstOrDefaultAsync(ct) ?? -1;
                        q = q.Where(l => _db.CatalogNodes.Any(n => n.Id == l.NodeId && n.ParentId == folderId));
                    }
                    total = await q.CountAsync(ct);
                    rows = await NeedsReviewPageAsync(q, ReviewCursor.Parse(cursor), limit + 1, ct);
                    break;
                }
            case MetadataReviewTab.AutoLinked:
            case MetadataReviewTab.Confirmed:
            case MetadataReviewTab.DontMatch:
            case MetadataReviewTab.Collections:
                {
                    var q = tab switch
                    {
                        MetadataReviewTab.AutoLinked => AutoAnchors(libraryId),
                        MetadataReviewTab.Confirmed => LinksOf(SeriesLinkState.Confirmed, libraryId),
                        // 1.37.0: artist folders too - like collections, folders whose items are works of their own.
                        MetadataReviewTab.Collections => CollectionTabRows(libraryId),
                        _ => LinksOf(SeriesLinkState.DontMatch, libraryId),
                    };
                    total = await q.CountAsync(ct);
                    if (after is { } a)
                        q = q.Where(l => l.Id < a);
                    rows = (await q.OrderByDescending(l => l.Id).Take(limit + 1).Select(l => new { l.Id, l.NodeId }).ToListAsync(ct))
                        .Select(x => (Key(x.Id), x.NodeId)).ToList();
                    break;
                }
            case MetadataReviewTab.Unmatched:
                {
                    if (!string.IsNullOrEmpty(author) && !string.IsNullOrEmpty(folder))
                        return ("invalid_filter", null);
                    // 1.34.0: the same author / folder groups as Needs review, built from the Unmatched rows.
                    groups = await GroupIndexAsync(MetadataReviewTab.Unmatched, libraryId, ct);
                    var q = UnmatchedRows(libraryId);
                    if (!string.IsNullOrEmpty(author))
                    {
                        var members = groups.Find(author)?.NodeIds.ToList() ?? [];
                        q = q.Where(r => members.Contains(r.NodeId));
                    }
                    if (!string.IsNullOrEmpty(folder))
                    {
                        var folderId = await _db.CatalogNodes.Where(n => n.PublicId == folder).Select(n => (long?)n.Id).FirstOrDefaultAsync(ct) ?? -1;
                        q = q.Where(r => _db.CatalogNodes.Any(n => n.Id == r.NodeId && n.ParentId == folderId));
                    }
                    total = await q.CountAsync(ct);
                    if (after is { } a)
                        q = q.Where(r => r.Id < a);
                    rows = (await q.OrderByDescending(r => r.Id).Take(limit + 1).Select(r => new { r.Id, r.NodeId }).ToListAsync(ct))
                        .Select(x => (Key(x.Id), x.NodeId)).ToList();
                    break;
                }
            case MetadataReviewTab.Flags:
                {
                    // One row per anchor, keyed by its newest open flag; automatic links first within a page.
                    var q = OpenFlags(libraryId).GroupBy(f => f.NodeId).Select(g => new { NodeId = g.Key, Key = g.Max(f => f.Id) });
                    total = await q.CountAsync(ct);
                    if (after is { } a)
                        q = q.Where(x => x.Key < a);
                    rows = (await q.OrderByDescending(x => x.Key).Take(limit + 1).ToListAsync(ct)).Select(x => (Key(x.Key), x.NodeId)).ToList();
                    break;
                }
            default:
                {
                    var q = _carryOver.StrandedFolderIds(libraryId);
                    total = await q.CountAsync(ct);
                    if (after is { } a)
                        q = q.Where(id => id < a);
                    rows = (await q.OrderByDescending(id => id).Take(limit + 1).ToListAsync(ct)).Select(id => (Key(id), id)).ToList();
                    break;
                }
        }

        var hasMore = rows.Count > limit;
        rows = rows.Take(limit).ToList();
        var items = await BuildItemsAsync(tab, rows.Select(r => r.NodeId).ToList(), ct, groups);
        if (tab == MetadataReviewTab.Flags)
            items = items.OrderByDescending(i => i.Link?.State == SeriesLinkState.Auto).ToList();
        return (null, new MetadataReviewPageDto
        {
            Tab = tab,
            Items = items,
            Total = total,
            HasMore = hasMore,
            NextCursor = hasMore ? rows[^1].Key : null,
        });
    }

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One Needs review page in the order "not Later first (newest row first), then Later (set aside first, oldest first)". The
    /// cursor names the last row shown and its bucket, so a page boundary never repeats or skips a row, also across the buckets.
    /// </summary>
    private static async Task<List<(string Key, long NodeId)>> NeedsReviewPageAsync(
        IQueryable<NodeSeriesLinkEntity> q, ReviewCursor? after, int take, CancellationToken ct)
    {
        var rows = new List<(string Key, long NodeId)>();
        if (after is not { LaterAt: not null })
        {
            var fresh = q.Where(l => l.LaterAt == null);
            if (after is { Id: var id })
                fresh = fresh.Where(l => l.Id < id);
            rows.AddRange((await fresh.OrderByDescending(l => l.Id).Take(take).Select(l => new { l.Id, l.NodeId }).ToListAsync(ct))
                .Select(x => (Key(x.Id), x.NodeId)));
        }
        if (rows.Count < take)
        {
            var aside = q.Where(l => l.LaterAt != null);
            if (after is { LaterAt: { } at, Id: var id })
                aside = aside.Where(l => l.LaterAt > at || (l.LaterAt == at && l.Id > id));
            rows.AddRange((await aside.OrderBy(l => l.LaterAt).ThenBy(l => l.Id).Take(take - rows.Count)
                    .Select(l => new { l.Id, l.NodeId, l.LaterAt }).ToListAsync(ct))
                .Select(x => (new ReviewCursor(x.LaterAt, x.Id).ToString(), x.NodeId)));
        }
        return rows;
    }

    private async Task<(bool Ok, long? LibraryId)> LibraryFilterAsync(string? libraryPublicId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(libraryPublicId))
            return (true, null);
        var id = await _db.Libraries.Where(l => l.PublicId == libraryPublicId).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct);
        return (id is not null, id);
    }

    // --- Same author / same folder (1.33.0) ---

    /// <summary>The Authors list: author groups with at least two waiting works, largest first.</summary>
    public async Task<MetadataReviewAuthorsDto?> AuthorsAsync(string? libraryPublicId, CancellationToken ct = default,
        MetadataReviewTab tab = MetadataReviewTab.NeedsReview)
    {
        var (ok, libraryId) = await LibraryFilterAsync(libraryPublicId, ct);
        if (!ok)
            return null;
        var index = await GroupIndexAsync(tab, libraryId, ct);
        return new MetadataReviewAuthorsDto
        {
            Items = index.Groups.Where(g => g.NodeIds.Count >= 2)
                .OrderByDescending(g => g.NodeIds.Count).ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new MetadataReviewAuthorDto { Key = g.Key, Label = g.Label, Count = g.NodeIds.Count, Later = g.Later })
                .ToList(),
        };
    }

    /// <summary>
    /// The author and folder groups of every work waiting in Needs review (with the library filter). Names only, read in a fixed
    /// number of queries: each work's own name; for an artist-collection work without a tag, the artist folder's name; then, for a
    /// work still without a name, the ComicInfo writers and pencillers of the archive (a folder: of its archives) - never a
    /// translator. Nothing is stored or sent.
    /// </summary>
    private async Task<ReviewGroupIndex> GroupIndexAsync(MetadataReviewTab tab, long? libraryId, CancellationToken ct)
    {
        // The works the tab lists: Needs review's link rows, or (1.34.0) the Unmatched queue rows (never "set aside").
        var waiting = tab == MetadataReviewTab.Unmatched
            ? UnmatchedRows(libraryId).Select(q => new WaitingRef { NodeId = q.NodeId, Later = false })
            : LinksOf(SeriesLinkState.NeedsReview, libraryId).Select(l => new WaitingRef { NodeId = l.NodeId, Later = l.LaterAt != null });
        var rows = await (
            from l in waiting
            join n in _db.CatalogNodes on l.NodeId equals n.Id
            join q in _db.MetadataMatchQueue on n.Id equals q.NodeId into qs
            from q in qs.DefaultIfEmpty()
            join p in _db.CatalogNodes on n.ParentId equals (long?)p.Id into ps
            from p in ps.DefaultIfEmpty()
            select new
            {
                n.Id,
                n.DisplayName,
                n.Kind,
                n.ParentId,
                ParentName = p == null ? null : p.DisplayName,
                WorkClass = q == null ? null : q.WorkClass,
                l.Later,
            }).ToListAsync(ct);

        var folderKind = (int)CatalogNodeKind.Folder;
        var names = new Dictionary<long, IReadOnlyList<ReviewAuthorNames.Name>>();
        foreach (var r in rows)
        {
            var own = ReviewAuthorNames.FromWorkName(r.DisplayName);
            if (own.Count == 0 && r.WorkClass == (int)WorkClass.ArtistCollection)
                own = ReviewAuthorNames.FromPlainName(r.Kind == folderKind ? r.DisplayName : r.ParentName);
            names[r.Id] = own;
        }

        var bare = rows.Where(r => names[r.Id].Count == 0).ToList();
        if (bare.Count > 0)
        {
            var archiveIds = bare.Where(r => r.Kind != folderKind).Select(r => r.Id).ToList();
            var folderIds = bare.Where(r => r.Kind == folderKind).Select(r => r.Id).ToList();
            var infos = await (
                from e in _db.EmbeddedMetadata.AsNoTracking()
                join c in _db.CatalogNodes on e.NodeId equals c.Id
                where e.State == 1 && e.CreatorsJson != null
                    && (archiveIds.Contains(e.NodeId) || (c.ParentId != null && folderIds.Contains(c.ParentId.Value)))
                select new { e.NodeId, c.ParentId, e.CreatorsJson }).ToListAsync(ct);
            foreach (var r in bare)
            {
                var creators = infos.Where(i => r.Kind == folderKind ? i.ParentId == r.Id : i.NodeId == r.Id)
                    .SelectMany(i => MetadataJson.ReadList<MetadataJson.Creator>(i.CreatorsJson))
                    .Where(c => c.Role is "writer" or "penciller")
                    .SelectMany(c => ReviewAuthorNames.FromPlainName(c.Name))
                    .DistinctBy(n => n.Key)
                    .ToList();
                names[r.Id] = creators;
            }
        }

        return new ReviewGroupIndex(rows.Select(r => new ReviewGroupIndex.Work(r.Id, names[r.Id], r.ParentId, r.Later)).ToList());
    }

    /// <summary>A work a group index is built from (its node, and whether an admin set it aside).</summary>
    private sealed class WaitingRef
    {
        public long NodeId { get; init; }
        public bool Later { get; init; }
    }

    private sealed record NodeRow(long Id, string PublicId, long LibraryId, long? ParentId, int Kind, string DisplayName, int Availability);

    /// <summary>Builds the rows of a page with a fixed number of queries (no per-row query).</summary>
    private async Task<List<MetadataReviewItemDto>> BuildItemsAsync(MetadataReviewTab tab, IReadOnlyList<long> nodeIds, CancellationToken ct,
        ReviewGroupIndex? groups = null)
    {
        if (nodeIds.Count == 0)
            return [];
        var nodes = await _db.CatalogNodes.AsNoTracking()
            .Where(n => nodeIds.Contains(n.Id))
            .Select(n => new NodeRow(n.Id, n.PublicId, n.LibraryId, n.ParentId, n.Kind, n.DisplayName, n.Availability))
            .ToDictionaryAsync(n => n.Id, ct);
        var libraries = await _db.Libraries.AsNoTracking()
            .Where(l => nodes.Values.Select(n => n.LibraryId).Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => (l.PublicId, l.DisplayName), ct);
        var links = await _db.NodeSeriesLinks.AsNoTracking().Where(l => nodeIds.Contains(l.NodeId)).ToDictionaryAsync(l => l.NodeId, ct);
        var recordIds = links.Values.Where(l => l.RecordId != null).Select(l => l.RecordId!.Value).ToList();
        var records = await _db.MetadataRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var queue = await _db.MetadataMatchQueue.AsNoTracking().Where(q => nodeIds.Contains(q.NodeId)).ToDictionaryAsync(q => q.NodeId, ct);
        var runIds = queue.Values.Where(q => q.RunId != null).Select(q => q.RunId!.Value).Distinct().ToList();
        var runs = await _db.MetadataMatchRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.PublicId, ct);
        var candidates = (await _db.MetadataMatchCandidates.AsNoTracking().Where(c => nodeIds.Contains(c.NodeId)).OrderBy(c => c.Rank).ToListAsync(ct))
            .GroupBy(c => c.NodeId).ToDictionary(g => g.Key, g => g.ToList());
        var flagCounts = await OpenFlags(null).Where(f => nodeIds.Contains(f.NodeId)).GroupBy(f => f.NodeId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var flags = tab == MetadataReviewTab.Flags
            ? (await MetadataFlagService.ToAdminDtosAsync(_db, await OpenFlags(null).Where(f => nodeIds.Contains(f.NodeId)).OrderByDescending(f => f.Id).ToListAsync(ct), ct))
                .GroupBy(f => f.NodeId).ToDictionary(g => g.Key, g => g.ToList())
            : [];
        var trails = await TrailsAsync(nodes.Values.ToList(), ct);
        var folderIds = nodes.Values.Where(n => n.Kind == (int)CatalogNodeKind.Folder).Select(n => n.Id).ToList();
        var archiveCounts = await ArchiveCountsAsync(folderIds, ct);
        var duplicates = await DuplicatesAsync(folderIds, ct);
        // Folders, and archives that still exist (a tombstoned archive shows no cover).
        var covers = await _covers.ResolveUrlsAsync(nodes.Values
            .Where(n => n.Kind == (int)CatalogNodeKind.Folder || n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .Select(n => new CoverTarget(n.Id, n.PublicId, n.Kind == (int)CatalogNodeKind.Folder)).ToList(), ct);

        var memberIds = queue.Values.SelectMany(q => Members(q)).Distinct().ToList();
        var memberPublic = await _db.CatalogNodes.AsNoTracking().Where(n => memberIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => n.PublicId, ct);
        var parentIds = nodes.Values.Where(n => n.ParentId != null).Select(n => n.ParentId!.Value).Distinct().ToList();
        var parents = await _db.CatalogNodes.AsNoTracking().Where(n => parentIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => (n.PublicId, n.DisplayName), ct);
        // 1.34.0: "Looks like a collection about <Series>" for waiting folders (their direct archive names + stored candidates).
        var waitingFolders = tab == MetadataReviewTab.NeedsReview
            ? folderIds.Where(id => links.TryGetValue(id, out var l) && l.State == (int)SeriesLinkState.NeedsReview).ToList()
            : [];
        var archiveNames = await DirectArchiveNamesAsync(waitingFolders, ct);
        // 1.37.0: the artist each artist folder declares (its first own creator).
        var artistFolders = links.Values.Where(l => l.State == (int)SeriesLinkState.ArtistFolder).Select(l => l.NodeId).ToList();
        var artists = artistFolders.Count == 0 ? [] : (await _db.DeclaredFacts.AsNoTracking()
                .Where(f => f.NodeId != null && artistFolders.Contains(f.NodeId.Value) && f.Key == DeclaredFactKeys.Creator)
                .Select(f => new { NodeId = f.NodeId!.Value, f.Value, f.Role, f.Position, f.Id })
                .ToListAsync(ct))
            .GroupBy(f => f.NodeId)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Position).ThenBy(f => f.Id).Select(f => new DeclaredCreatorDto { Name = f.Value, Role = f.Role }).First());

        var items = new List<MetadataReviewItemDto>();
        foreach (var id in nodeIds)
        {
            if (!nodes.TryGetValue(id, out var node))
                continue;
            links.TryGetValue(id, out var link);
            queue.TryGetValue(id, out var q);
            var members = q is null ? [] : Members(q);
            var library = libraries.GetValueOrDefault(node.LibraryId);
            var coverUrl = covers.GetValueOrDefault(node.Id);
            items.Add(new MetadataReviewItemDto
            {
                NodeId = node.PublicId,
                CoverUrl = coverUrl,
                NodeKind = (CatalogNodeKind)node.Kind,
                DisplayName = node.DisplayName,
                LibraryId = library.PublicId ?? string.Empty,
                LibraryName = library.DisplayName ?? string.Empty,
                Trail = trails.GetValueOrDefault(id) ?? [],
                Missing = node.Availability == (int)CatalogNodeAvailability.Tombstoned,
                ParentNodeId = node.ParentId is { } parent ? parents.GetValueOrDefault(parent).PublicId : null,
                WorkClass = q?.WorkClass is { } wc ? (WorkClass)wc : null,
                MatchLevel = q is null ? null : (MatchLevel)q.Level,
                ItemCount = node.Kind == (int)CatalogNodeKind.Folder ? archiveCounts.GetValueOrDefault(id) : 1 + members.Count,
                MemberNodeIds = members.Select(m => memberPublic.GetValueOrDefault(m)).OfType<string>().ToList(),
                Link = link is null ? null : ToLinkDto(link, node.PublicId, link.RecordId is { } rid ? records.GetValueOrDefault(rid) : null),
                Candidates = candidates.TryGetValue(id, out var list) ? list.Select(c => ToCandidateDto(c, node.LibraryId)).ToList() : [],
                Reasons = q is null ? [] : MatchReasonCodes.Of(q.OutcomeReasons),
                MatchedAt = q?.CompletedAt,
                NextRetryAt = q is { Outcome: (int)MatchBand.Unmatched } ? q.NotBefore : null,
                RunId = q?.RunId is { } run ? runs.GetValueOrDefault(run) : null,
                DuplicateChapters = duplicates.GetValueOrDefault(id).Chapters,
                DuplicateVolumes = duplicates.GetValueOrDefault(id).Volumes,
                CheckingAgain = q is { Reason: QueueReason.Recheck, State: QueueState.Pending or QueueState.Leased },
                LaterAt = link is { State: (int)SeriesLinkState.NeedsReview } ? link.LaterAt : null,
                SameAuthor = groups?.AuthorOf(id) is { NodeIds.Count: > 1 } author
                    ? new MetadataReviewGroupHintDto { Key = author.Key, Label = author.Label, Others = author.NodeIds.Count - 1 }
                    : null,
                SameFolder = groups is not null && node.ParentId is { } folderId && groups.InFolder(folderId) > 1
                        && parents.TryGetValue(folderId, out var folder)
                    ? new MetadataReviewGroupHintDto { Key = folder.PublicId, Label = folder.DisplayName, Others = groups.InFolder(folderId) - 1 }
                    : null,
                Collection = archiveNames.TryGetValue(id, out var names) && candidates.TryGetValue(id, out var stored)
                    ? CollectionHint(names, stored)
                    : null,
                Artist = artists.GetValueOrDefault(id),
                OpenFlagCount = flagCounts.GetValueOrDefault(id),
                Flags = flags.TryGetValue(node.PublicId, out var nodeFlags) ? nodeFlags : [],
            });
        }
        return items;
    }

    /// <summary>The direct live archive names of each folder (the collection signal's input), in catalog order.</summary>
    private async Task<Dictionary<long, List<string>>> DirectArchiveNamesAsync(IReadOnlyCollection<long> folderIds, CancellationToken ct)
    {
        if (folderIds.Count == 0)
            return [];
        var rows = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.ParentId != null && folderIds.Contains(n.ParentId.Value)
                && n.Kind == (int)CatalogNodeKind.Archive && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .OrderBy(n => n.SortKey)
            .Select(n => new { ParentId = n.ParentId!.Value, n.DisplayName })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.ParentId).ToDictionary(g => g.Key, g => g.Select(r => r.DisplayName).ToList());
    }

    private static MetadataReviewCollectionHintDto? CollectionHint(IReadOnlyList<string> archiveNames, IReadOnlyList<MetadataMatchCandidateEntity> stored)
    {
        var pick = CollectionSignal.Suggest(archiveNames,
            stored.Select(c => new CollectionCandidate(c.Rank, (MetadataFormat?)c.Format, c.TitleScore)).ToList());
        if (pick is null || stored.FirstOrDefault(c => c.Rank == pick.Rank) is not { } candidate)
            return null;
        return new MetadataReviewCollectionHintDto
        {
            Rank = candidate.Rank,
            Provider = candidate.Provider,
            ExternalId = candidate.ExternalId,
            Title = candidate.Title,
        };
    }

    private static List<long> Members(MetadataMatchQueueEntity q) =>
        q.MemberNodeIdsJson is { } json ? JsonSerializer.Deserialize<List<long>>(json) ?? [] : [];

    internal static MetadataReviewLinkDto ToLinkDto(NodeSeriesLinkEntity link, string nodePublicId, MetadataRecordEntity? record) => new()
    {
        State = (SeriesLinkState)link.State,
        Provider = record?.Provider,
        ExternalId = record?.ExternalId,
        RecordId = record?.PublicId,
        Title = record?.Title,
        MatchMethod = (MetadataMatchMethod?)link.MatchMethod,
        MatchScore = link.MatchScore,
        ImageUrl = record is { ImageState: 1 } ? SeriesInfoResolver.ImageUrlFor(nodePublicId, record) : null,
        UpdatedAt = link.UpdatedAt,
    };

    private MetadataReviewCandidateDto ToCandidateDto(MetadataMatchCandidateEntity c, long libraryId) => new()
    {
        Rank = c.Rank,
        Provider = c.Provider,
        ExternalId = c.ExternalId,
        Title = c.Title,
        ProviderType = c.ProviderType,
        Format = (MetadataFormat?)c.Format,
        Origin = (MetadataOrigin?)c.Origin,
        Year = c.Year,
        Volumes = c.Volumes,
        TitleScore = c.TitleScore,
        AdjustedScore = c.AdjustedScore,
        Reasons = MatchReasonCodes.Of(c.Reasons),
        ImageToken = c.ImageRemoteUrl is { } url ? _identify.IssueImageToken(c.Provider, libraryId, url) : null,
        FamilyGroup = c.FamilyGroup,
        FamilyRole = c.FamilyRole,
    };

    /// <summary>Up to 3 ancestor names per node, outermost first (3 batched queries).</summary>
    private async Task<Dictionary<long, List<string>>> TrailsAsync(IReadOnlyList<NodeRow> nodes, CancellationToken ct)
    {
        var parentOf = nodes.ToDictionary(n => n.Id, n => n.ParentId);
        var names = new Dictionary<long, (string Name, long? ParentId)>();
        var frontier = nodes.Where(n => n.ParentId != null).Select(n => n.ParentId!.Value).Distinct().ToList();
        for (var level = 0; level < TrailDepth && frontier.Count > 0; level++)
        {
            var batch = await _db.CatalogNodes.AsNoTracking().Where(n => frontier.Contains(n.Id))
                .Select(n => new { n.Id, n.DisplayName, n.ParentId }).ToListAsync(ct);
            foreach (var b in batch)
                names[b.Id] = (b.DisplayName, b.ParentId);
            frontier = batch.Where(b => b.ParentId != null && !names.ContainsKey(b.ParentId.Value)).Select(b => b.ParentId!.Value).Distinct().ToList();
        }
        var result = new Dictionary<long, List<string>>();
        foreach (var n in nodes)
        {
            var trail = new List<string>();
            var current = n.ParentId;
            while (current is { } p && trail.Count < TrailDepth && names.TryGetValue(p, out var entry))
            {
                trail.Insert(0, entry.Name);
                current = entry.ParentId;
            }
            result[n.Id] = trail;
        }
        return result;
    }

    /// <summary>Live archives below each folder (one recursive query for the page).</summary>
    private async Task<Dictionary<long, int>> ArchiveCountsAsync(IReadOnlyList<long> folderIds, CancellationToken ct)
    {
        if (folderIds.Count == 0)
            return [];
        var ids = string.Join(',', folderIds.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        // Ids are longs from our own query (no user input); the IN list is formatted, not interpolated from a request.
#pragma warning disable EF1002
        var rows = await _db.Database.SqlQueryRaw<ArchiveCountRow>($"""
            WITH RECURSIVE sub(root, id, kind) AS (
                SELECT n."Id", n."Id", n."Kind" FROM catalog_nodes n WHERE n."Id" IN ({ids})
                UNION ALL
                SELECT sub.root, c."Id", c."Kind" FROM catalog_nodes c JOIN sub ON c."ParentId" = sub.id WHERE c."Availability" != 5
            )
            SELECT root AS "Root", COUNT(*) AS "Count" FROM sub WHERE kind = 1 GROUP BY root
            """).ToListAsync(ct);
#pragma warning restore EF1002
        return rows.ToDictionary(r => r.Root, r => r.Count);
    }

    private sealed record ArchiveCountRow(long Root, int Count);

    private sealed record ArchiveNameRow(long Root, long ParentId, string ParentName, string Name);

    /// <summary>
    /// Duplicate chapter and volume numbers below each folder (1.31.0): the live archives are compared per directory (a number in two
    /// different folders is not a duplicate - seasons restart), as the Volumes view and the Missing report read the names. Names
    /// only, one recursive query for the page; nothing is stored.
    /// </summary>
    private async Task<Dictionary<long, (int Chapters, int Volumes)>> DuplicatesAsync(IReadOnlyList<long> folderIds, CancellationToken ct)
    {
        if (folderIds.Count == 0)
            return [];
        var ids = string.Join(',', folderIds.Select(i => i.ToString(CultureInfo.InvariantCulture)));
#pragma warning disable EF1002
        var rows = await _db.Database.SqlQueryRaw<ArchiveNameRow>($"""
            WITH RECURSIVE sub(root, id) AS (
                SELECT n."Id", n."Id" FROM catalog_nodes n WHERE n."Id" IN ({ids})
                UNION ALL
                SELECT sub.root, c."Id" FROM catalog_nodes c JOIN sub ON c."ParentId" = sub.id WHERE c."Availability" != 5
            )
            SELECT sub.root AS "Root", c."ParentId" AS "ParentId", p."DisplayName" AS "ParentName", c."DisplayName" AS "Name"
            FROM sub JOIN catalog_nodes c ON c."Id" = sub.id JOIN catalog_nodes p ON p."Id" = c."ParentId"
            WHERE c."Kind" = 1
            """).ToListAsync(ct);
#pragma warning restore EF1002
        var result = new Dictionary<long, (int Chapters, int Volumes)>();
        foreach (var byRoot in rows.GroupBy(r => r.Root))
        {
            var chapters = 0;
            var volumes = 0;
            foreach (var directory in byRoot.GroupBy(r => r.ParentId))
            {
                var found = DuplicateUnits.Find(directory.Select(r =>
                    VolumeGrouping.UnitsOf(new GroupingRow(r.Name, GroupingRowKind.Archive, r.Name, string.Empty, r.ParentName))));
                chapters += found.Count(d => d.Kind == MissingUnitKind.Chapter);
                volumes += found.Count(d => d.Kind == MissingUnitKind.Volume);
            }
            if (chapters + volumes > 0)
                result[byRoot.Key] = (chapters, volumes);
        }
        return result;
    }

    // --- Accept ---

    /// <summary>Links a review row to one of its stored candidates (Confirmed). Group members follow the anchor.</summary>
    public async Task<(string? Error, NodeSeriesLinkChangeDto? Change)> AcceptAsync(string nodePublicId, int rank, string? actor, CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return ("not_found", null);
        var candidate = await _db.MetadataMatchCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.NodeId == node.Id && c.Rank == rank, ct);
        if (candidate is null)
            return ("no_candidate", null);

        var (code, change) = await _identify.LinkAsync(nodePublicId, new LinkSeriesRequest
        {
            Provider = candidate.Provider,
            ExternalId = candidate.ExternalId,
            MatchMethod = MetadataMatchMethod.Auto,
            MatchScore = candidate.TitleScore,
        }, actor, ct);
        if (code != MetadataLinkResultCode.Ok)
            return (code == MetadataLinkResultCode.NodeNotFound ? "not_found" : "record_not_found", null);

        await LinkMembersAsync(node.Id, candidate.Provider, candidate.ExternalId, actor, ct);
        await _audit.RecordAsync(AuditActions.MetadataReviewAccept, rank == 1 ? "top" : "other", actor, ct: ct,
            targetLibraryId: node.LibraryId, targetItemId: node.Id);
        return (null, change);
    }

    /// <summary>
    /// 1.34.0: "Accept as a collection" - marks a Needs-review FOLDER "Collection about" one of its stored candidates (the record is
    /// stored first when needed, one gated GET), sets its Content to "Doujinshi &amp; adult one-shots" and queues its works. Errors:
    /// <c>not_found</c>, <c>not_a_folder</c>, <c>no_candidate</c>, <c>record_not_found</c>, <c>unavailable</c>.
    /// </summary>
    public async Task<(string? Error, CollectionAboutResultDto? Result)> AcceptCollectionAsync(string nodePublicId, int rank, string? actor,
        CancellationToken ct = default)
    {
        if (_collections is null)
            return ("unavailable", null);
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return ("not_found", null);
        if (node.Kind != (int)CatalogNodeKind.Folder)
            return ("not_a_folder", null);
        var candidate = await _db.MetadataMatchCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.NodeId == node.Id && c.Rank == rank, ct);
        if (candidate is null)
            return ("no_candidate", null);
        var (code, result) = await _collections.SetAsync(nodePublicId, new SetCollectionAboutRequest
        {
            Provider = candidate.Provider,
            ExternalId = candidate.ExternalId,
            MatchMethod = MetadataMatchMethod.Auto,
        }, actor, ct, auditResult: "review");
        return code switch
        {
            MetadataLinkResultCode.Ok => (null, result),
            MetadataLinkResultCode.NodeNotFound => ("not_found", null),
            MetadataLinkResultCode.NotAFolder => ("not_a_folder", null),
            _ => ("record_not_found", null),
        };
    }

    /// <summary>
    /// 1.37.0: "Artist folder" from the review dashboard (key <c>r</c>) - marks a live FOLDER (waiting in Needs review or Unmatched) an
    /// artist's folder: its review row and candidates go, the artist is declared (default the folder's name) and its works are queued.
    /// Errors as <see cref="Artists.ArtistFolderService.SetAsync"/>; a removed folder is not found. No network.
    /// </summary>
    public async Task<Artists.ArtistFolderOutcome> AcceptArtistAsync(string nodePublicId, SetArtistFolderRequest? request, string? actor,
        CancellationToken ct = default)
    {
        if (_artists is null)
            return new(MetadataLinkResultCode.InvalidRequest, Error: "unavailable");
        var live = await _db.CatalogNodes.AsNoTracking()
            .AnyAsync(n => n.PublicId == nodePublicId && n.Availability != (int)CatalogNodeAvailability.Tombstoned, ct);
        if (!live)
            return new(MetadataLinkResultCode.NodeNotFound);
        return await _artists.SetAsync(nodePublicId, request, actor, ct, auditResult: "review");
    }

    /// <summary>The collection suggestion of one waiting folder now (the bulk action's input), or null.</summary>
    private async Task<MetadataReviewCollectionHintDto?> CollectionHintOfAsync(long nodeId, CancellationToken ct)
    {
        var stored = await _db.MetadataMatchCandidates.AsNoTracking().Where(c => c.NodeId == nodeId).OrderBy(c => c.Rank).ToListAsync(ct);
        if (stored.Count == 0)
            return null;
        var names = await DirectArchiveNamesAsync([nodeId], ct);
        return names.TryGetValue(nodeId, out var list) ? CollectionHint(list, stored) : null;
    }

    /// <summary>An archive group's other archives get the same confirmed link as the anchor (unless they have their own row).</summary>
    private async Task LinkMembersAsync(long anchorId, string provider, string externalId, string? actor, CancellationToken ct)
    {
        var json = await _db.MetadataMatchQueue.AsNoTracking().Where(q => q.NodeId == anchorId).Select(q => q.MemberNodeIdsJson).FirstOrDefaultAsync(ct);
        if (json is null)
            return;
        var members = JsonSerializer.Deserialize<List<long>>(json) ?? [];
        var linked = await _db.NodeSeriesLinks.AsNoTracking()
            .Where(l => members.Contains(l.NodeId) && l.State != (int)SeriesLinkState.Auto && l.State != (int)SeriesLinkState.NeedsReview)
            .Select(l => l.NodeId).ToListAsync(ct);
        var publicIds = await _db.CatalogNodes.AsNoTracking()
            .Where(n => members.Contains(n.Id) && !linked.Contains(n.Id) && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .Select(n => n.PublicId).ToListAsync(ct);
        foreach (var memberId in publicIds)
            await _links.LinkAsync(memberId, new LinkSeriesRequest { Provider = provider, ExternalId = externalId, MatchMethod = MetadataMatchMethod.Auto }, actor, ct);
    }

    // --- Later (1.33.0) ---

    /// <summary>
    /// Sets a Needs review row aside ("Later": it sorts to the end of Needs review for every admin) or brings it back. Only a row
    /// waiting in Needs review can be set aside; deciding the work or checking it again clears it (see <see cref="NodeSeriesLinkEntity.LaterAt"/>).
    /// Returns <c>ok</c>, <c>not_found</c> or <c>not_in_review</c>. Setting a row that is already set aside keeps its first time.
    /// </summary>
    public async Task<string> SetLaterAsync(string nodePublicId, bool later, string? actor, CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return "not_found";
        var link = await _db.NodeSeriesLinks.FirstOrDefaultAsync(l => l.NodeId == node.Id, ct);
        if (link is null || link.State != (int)SeriesLinkState.NeedsReview)
            return "not_in_review";
        if (later == (link.LaterAt is not null))
            return "ok"; // Nothing changes (idempotent, not audited again).
        link.LaterAt = later ? _time.GetUtcNow() : null;
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(AuditActions.MetadataReviewLater, later ? "set" : "clear", actor, ct: ct,
            targetLibraryId: node.LibraryId, targetItemId: node.Id);
        return "ok";
    }

    // --- Bulk ---

    public async Task<(string? Error, MetadataReviewBulkResultDto? Result)> BulkAsync(MetadataReviewBulkRequest request, string? actor, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(request.Action))
            return ("invalid_action", null);
        var ids = (request.NodeIds ?? []).Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0 || ids.Count > MaxBulk)
            return ("invalid_node_ids", null);

        var results = new List<MetadataReviewBulkItemResultDto>();
        if (request.Action == MetadataReviewBulkAction.RerunMatching)
        {
            var codes = await _autoMatch.RerunAsync(ids, actor, ct);
            results.AddRange(ids.Select(id => new MetadataReviewBulkItemResultDto { NodeId = id, Code = codes.GetValueOrDefault(id, "not_found") }));
        }
        else
        {
            foreach (var id in ids)
            {
                string code;
                try
                {
                    code = await ApplyOneAsync(request.Action, id, actor, ct);
                }
                catch (MetadataGatewayException ex)
                {
                    code = ex.Code;
                }
                results.Add(new MetadataReviewBulkItemResultDto { NodeId = id, Code = code });
            }
        }
        var succeeded = results.Count(r => r.Code == "ok");
        _logger.LogInformation(LogEvents.Metadata.ReviewAction, "Review bulk {Action}: {Succeeded} ok, {Failed} failed",
            request.Action, succeeded, results.Count - succeeded);
        return (null, new MetadataReviewBulkResultDto
        {
            Action = request.Action,
            Succeeded = succeeded,
            Failed = results.Count - succeeded,
            Results = results,
        });
    }

    private async Task<string> ApplyOneAsync(MetadataReviewBulkAction action, string nodePublicId, string? actor, CancellationToken ct)
    {
        switch (action)
        {
            case MetadataReviewBulkAction.AcceptTop:
                return (await AcceptAsync(nodePublicId, 1, actor, ct)).Error ?? "ok";
            case MetadataReviewBulkAction.DontMatch:
                return Code((await _links.SetDontMatchAsync(nodePublicId, actor, ct)).Code);
            case MetadataReviewBulkAction.Unlink:
                return Code((await _links.RemoveAsync(nodePublicId, onlyDontMatch: false, actor, ct)).Code);
            case MetadataReviewBulkAction.Confirm:
                return await ConfirmAsync(nodePublicId, actor, ct);
            case MetadataReviewBulkAction.Later:
                return await SetLaterAsync(nodePublicId, later: true, actor, ct);
            case MetadataReviewBulkAction.ClearLater:
                return await SetLaterAsync(nodePublicId, later: false, actor, ct);
            case MetadataReviewBulkAction.AcceptCollection:
                {
                    var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
                    if (node is null)
                        return "not_found";
                    var inReview = await _db.NodeSeriesLinks.AnyAsync(l => l.NodeId == node.Id && l.State == (int)SeriesLinkState.NeedsReview, ct);
                    if (!inReview || await CollectionHintOfAsync(node.Id, ct) is not { } hint)
                        return "no_suggestion";
                    return (await AcceptCollectionAsync(nodePublicId, hint.Rank, actor, ct)).Error ?? "ok";
                }
            case MetadataReviewBulkAction.MarkArtistFolder:
                {
                    var outcome = await AcceptArtistAsync(nodePublicId, null, actor, ct);
                    return outcome.Code switch
                    {
                        MetadataLinkResultCode.Ok => "ok",
                        MetadataLinkResultCode.NodeNotFound => "not_found",
                        _ => outcome.Error ?? "invalid_request",
                    };
                }
            default:
                return "invalid_action";
        }
    }

    /// <summary>Auto -> Confirmed on the anchor and its group members (no network, same record).</summary>
    private async Task<string> ConfirmAsync(string nodePublicId, string? actor, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null)
            return "not_found";
        var json = await _db.MetadataMatchQueue.AsNoTracking().Where(q => q.NodeId == node.Id).Select(q => q.MemberNodeIdsJson).FirstOrDefaultAsync(ct);
        var ids = (json is null ? [] : JsonSerializer.Deserialize<List<long>>(json) ?? []).Prepend(node.Id).ToList();
        var updated = await _db.NodeSeriesLinks
            .Where(l => ids.Contains(l.NodeId) && l.State == (int)SeriesLinkState.Auto)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.State, (int)SeriesLinkState.Confirmed).SetProperty(l => l.LaterAt, (DateTimeOffset?)null), ct);
        if (updated == 0)
            return "not_auto";
        await _audit.RecordAsync(AuditActions.MetadataReviewConfirm, AuditResults.Success, actor, ct: ct, targetLibraryId: node.LibraryId, targetItemId: node.Id);
        return "ok";
    }

    private static string Code(MetadataLinkResultCode code) => code switch
    {
        MetadataLinkResultCode.Ok => "ok",
        MetadataLinkResultCode.NodeNotFound => "not_found",
        MetadataLinkResultCode.RecordNotFound => "record_not_found",
        _ => "invalid_request",
    };
}

/// <summary>
/// The Needs review cursor (1.33.0): the last row shown and its bucket. A plain row id is a row that is not set aside (also the
/// cursor of earlier releases); <c>L&lt;ticks&gt;_&lt;id&gt;</c> is a row set aside at that time (UTC ticks as stored).
/// </summary>
internal sealed record ReviewCursor(DateTimeOffset? LaterAt, long Id)
{
    public static ReviewCursor? Parse(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return null;
        if (long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return new ReviewCursor(null, id);
        var sep = cursor.IndexOf('_', StringComparison.Ordinal);
        if (cursor[0] == 'L' && sep > 1
            && long.TryParse(cursor.AsSpan(1, sep - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.UtcTicks
            && long.TryParse(cursor.AsSpan(sep + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var laterId))
            return new ReviewCursor(new DateTimeOffset(ticks, TimeSpan.Zero), laterId);
        return null; // Unknown: the first page, as before.
    }

    public override string ToString() => LaterAt is { } at
        ? string.Create(CultureInfo.InvariantCulture, $"L{at.UtcTicks}_{Id}")
        : Id.ToString(CultureInfo.InvariantCulture);
}
