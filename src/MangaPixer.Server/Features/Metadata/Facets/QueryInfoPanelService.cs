namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// The Info panel's tags (1.45.0, lane Q; D1 2.5 / 2.6, owner Q9 / Q10). The node must be accessible (as series-info: otherwise null =
/// 404); its library's "Show series information" off = <see cref="InfoPanelMode.None"/>. What the tags describe:
/// <list type="number">
/// <item>the node is a location of a work -> Work mode: the values of that location;</item>
/// <item>else works have locations below the node -> Container mode: their values by how many works below carry them, top
/// <see cref="ContainerChipsPerBucket"/> per bucket + <c>More</c>;</item>
/// <item>else the nearest ancestor that is a location (an archive inside a series folder) -> Work mode for that location.</item>
/// </list>
/// Chip counts are the VIEWER's counts (<see cref="FacetCountService"/> over the viewer's shown libraries - Incognito on a Private
/// library's node leaves Private libraries out of the counts, like search), set only when 2 or more.
/// </summary>
public sealed class InfoPanelService
{
    /// <summary>Chips per bucket on a container (owner Q10).</summary>
    public const int ContainerChipsPerBucket = 12;

    /// <summary>Locations listed on a work.</summary>
    public const int MaxWorkLocations = 10;

    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _auth;
    private readonly FacetViewerScopeResolver _scopes;
    private readonly FacetCountService _counts;
    private readonly ILogger<InfoPanelService> _logger;

    public InfoPanelService(
        MangaPixerDbContext db, LibraryAuthorizationService auth, FacetViewerScopeResolver scopes, FacetCountService counts,
        ILogger<InfoPanelService> logger)
    {
        _db = db;
        _auth = auth;
        _scopes = scopes;
        _counts = counts;
        _logger = logger;
    }

    private sealed record Entry(long WorkId, long ValueId, long LocationNodeId, int Roles, bool Conflict);

    /// <summary>The panel of a node; null when the node does not exist, is tombstoned or the viewer may not open its library.</summary>
    public async Task<InfoPanelDto?> ForNodeAsync(long userId, bool incognito, string nodePublicId, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var (found, source) = await NodeSourceAsync(userId, nodePublicId, ct);
        if (!found)
            return null;
        if (source is null)
            return Empty(nodePublicId);

        var scope = await _scopes.ResolveAsync(userId, incognito, ct);
        var panel = await BuildAsync(source, scope, ct);
        _logger.LogDebug(LogEvents.Metadata.InfoPanelServed,
            "Info panel for node {NodeId}: mode {Mode}, {Buckets} buckets, in {ElapsedMs} ms",
            source.InternalId, panel.Mode, panel.Buckets.Count, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return panel;
    }

    /// <summary>The panel of a library root (Container mode over the whole library); null when unknown or not accessible.</summary>
    public async Task<InfoPanelDto?> ForLibraryAsync(long userId, bool incognito, string libraryPublicId, CancellationToken ct = default)
    {
        var (found, source) = await LibrarySourceAsync(userId, libraryPublicId, ct);
        if (!found)
            return null;
        if (source is null)
            return Empty(libraryPublicId);
        var scope = await _scopes.ResolveAsync(userId, incognito, ct);
        return await BuildAsync(source, scope, ct);
    }

    /// <summary>The most chips one bucket page answers.</summary>
    public const int MaxBucketPage = 200;

    /// <summary>
    /// 1.47.0 (lane P, "+N more"): one bucket of a node's panel from <paramref name="offset"/> on - the same entries, order and per-viewer
    /// counts as <see cref="ForNodeAsync"/> (the panel's chips are this list's first <see cref="ContainerChipsPerBucket"/>). Null under the
    /// same rule as the panel (404); an empty page when series information is hidden or the bucket has nothing here.
    /// </summary>
    public async Task<FacetBucketPageDto?> BucketForNodeAsync(
        long userId, bool incognito, string nodePublicId, FacetBucket bucket, int offset, int limit, CancellationToken ct = default)
    {
        var (found, source) = await NodeSourceAsync(userId, nodePublicId, ct);
        return found ? await BucketPageAsync(userId, incognito, source, bucket, offset, limit, ct) : null;
    }

    /// <summary>The library-root form of <see cref="BucketForNodeAsync"/>.</summary>
    public async Task<FacetBucketPageDto?> BucketForLibraryAsync(
        long userId, bool incognito, string libraryPublicId, FacetBucket bucket, int offset, int limit, CancellationToken ct = default)
    {
        var (found, source) = await LibrarySourceAsync(userId, libraryPublicId, ct);
        return found ? await BucketPageAsync(userId, incognito, source, bucket, offset, limit, ct) : null;
    }

    private async Task<FacetBucketPageDto> BucketPageAsync(
        long userId, bool incognito, Source? source, FacetBucket bucket, int offset, int limit, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxBucketPage);
        var all = new List<FacetChipDto>();
        if (source is not null)
        {
            var scope = await _scopes.ResolveAsync(userId, incognito, ct);
            var (buckets, _) = await OrderedBucketsAsync(source.Entries, source.ForceContainer, scope, ct);
            all = buckets.Where(b => b.Bucket == bucket).Select(b => b.Chips).FirstOrDefault() ?? [];
        }
        var page = all.Skip(offset).Take(limit).ToList();
        var next = offset + page.Count;
        _logger.LogDebug(LogEvents.Metadata.InfoBucketPageServed,
            "Info bucket page: {Chips} of {Total} chips from {Offset}, in {ElapsedMs} ms",
            page.Count, all.Count, offset, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new FacetBucketPageDto
        {
            Bucket = bucket,
            Chips = page,
            Offset = offset,
            Total = all.Count,
            NextOffset = next < all.Count ? next : null,
        };
    }

    /// <summary>What a panel describes: its entries, the node or library it is for, and whether it is a container by place.</summary>
    private sealed record Source(string Id, long InternalId, long LibraryId, List<Entry> Entries, bool ForceContainer);

    /// <summary>
    /// A node's panel source. Found = false: unknown, tombstoned or not accessible (404). Source null: nothing to show (series
    /// information hidden, or no indexed work at, below or above the node).
    /// </summary>
    private async Task<(bool Found, Source? Source)> NodeSourceAsync(long userId, string nodePublicId, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.PublicId == nodePublicId)
            .Select(n => new { n.Id, n.LibraryId, n.Availability })
            .FirstOrDefaultAsync(ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return (false, null);
        if (!await _auth.CanAccessLibraryAsync(userId, node.LibraryId, ct))
            return (false, null);
        if (await _scopes.IsHiddenAsync(node.LibraryId, ct))
            return (true, null);

        var own = await EntriesAtAsync([node.Id], ct);
        if (own.Count > 0)
            return (true, new Source(nodePublicId, node.Id, node.LibraryId, own, false));

        var below = await EntriesBelowAsync(node.Id, ct);
        if (below.Count > 0)
            return (true, new Source(nodePublicId, node.Id, node.LibraryId, below, true));

        var ancestors = await AncestorsAsync(node.Id, ct);
        var atAncestors = await EntriesAtAsync(ancestors, ct);
        var nearest = ancestors.FirstOrDefault(id => atAncestors.Any(e => e.LocationNodeId == id));
        return nearest == 0
            ? (true, null)
            : (true, new Source(nodePublicId, node.Id, node.LibraryId, atAncestors.Where(e => e.LocationNodeId == nearest).ToList(), false));
    }

    /// <summary>A library root's panel source (the same Found / null rule as <see cref="NodeSourceAsync"/>).</summary>
    private async Task<(bool Found, Source? Source)> LibrarySourceAsync(long userId, string libraryPublicId, CancellationToken ct)
    {
        var library = await _db.Libraries.AsNoTracking()
            .Where(l => l.PublicId == libraryPublicId)
            .Select(l => new { l.Id })
            .FirstOrDefaultAsync(ct);
        if (library is null || !await _auth.CanAccessLibraryAsync(userId, library.Id, ct))
            return (false, null);
        if (await _scopes.IsHiddenAsync(library.Id, ct))
            return (true, null);

        var entries = await QuerySql.ReadAsync(_db, $"""
            SELECT e.WorkId, e.ValueId, e.LocationNodeId, e.Roles, e.Conflict
            FROM facet_entries e
            JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
            WHERE e.LibraryId = @library
            """, new Dictionary<string, object?> { ["@library"] = library.Id }, ReadEntry, ct);
        return entries.Count == 0 ? (true, null) : (true, new Source(libraryPublicId, 0, library.Id, entries, true));
    }

    private static InfoPanelDto Empty(string id) => new() { NodeId = id, Mode = InfoPanelMode.None, Buckets = [] };

    private static Entry ReadEntry(System.Data.Common.DbDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), r.GetBoolean(4));

    private async Task<List<Entry>> EntriesAtAsync(IReadOnlyCollection<long> nodeIds, CancellationToken ct)
    {
        if (nodeIds.Count == 0)
            return [];
        return await QuerySql.ReadAsync(_db, $"""
            SELECT e.WorkId, e.ValueId, e.LocationNodeId, e.Roles, e.Conflict
            FROM facet_entries e
            JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
            WHERE e.LocationNodeId IN ({QuerySql.List(nodeIds)})
            """, null, ReadEntry, ct);
    }

    // 1.47.0 lane S2: CROSS JOIN keeps the walk as the outer loop (an index lookup of each walked node's entries); with a plain JOIN
    // SQLite 3.53 scanned ALL of facet_entries behind a bloom filter (live copy, the largest category folder: 9.4 -> 4.9 ms). Same rows;
    // the panel groups and fully sorts them (a container by place, so no row is taken by position).
    private Task<List<Entry>> EntriesBelowAsync(long nodeId, CancellationToken ct) =>
        QuerySql.ReadAsync(_db, EntriesBelowSql, new Dictionary<string, object?> { ["@node"] = nodeId }, ReadEntry, ct);

    /// <summary>The statement of <see cref="EntriesBelowAsync"/> (parameter <c>@node</c>; 1.47.0 lane S2: the equivalence test runs it).</summary>
    internal static readonly string EntriesBelowSql = $"""
            WITH RECURSIVE below(Id) AS (
                SELECT Id FROM catalog_nodes WHERE ParentId = @node
                UNION ALL
                SELECT c.Id FROM catalog_nodes c JOIN below ON c.ParentId = below.Id
            )
            SELECT e.WorkId, e.ValueId, e.LocationNodeId, e.Roles, e.Conflict
            FROM below
            CROSS JOIN facet_entries e ON e.LocationNodeId = below.Id
            JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
        """;

    /// <summary>The node's ancestors, nearest first (64 levels at most).</summary>
    private async Task<List<long>> AncestorsAsync(long nodeId, CancellationToken ct)
    {
        var rows = await QuerySql.ReadAsync(_db, """
            WITH RECURSIVE up(Id, Depth) AS (
                SELECT ParentId, 1 FROM catalog_nodes WHERE Id = @node AND ParentId IS NOT NULL
                UNION ALL
                SELECT c.ParentId, up.Depth + 1 FROM catalog_nodes c JOIN up ON c.Id = up.Id
                WHERE c.ParentId IS NOT NULL AND up.Depth < 64
            )
            SELECT Id FROM up ORDER BY Depth
            """, new Dictionary<string, object?> { ["@node"] = nodeId }, r => r.GetInt64(0), ct);
        return rows;
    }

    private async Task<InfoPanelDto> BuildAsync(Source source, FacetViewerScope scope, CancellationToken ct)
    {
        var (ordered, container) = await OrderedBucketsAsync(source.Entries, source.ForceContainer, scope, ct);
        var buckets = ordered
            .Select(b =>
            {
                var shown = container ? b.Chips.Take(ContainerChipsPerBucket).ToList() : b.Chips;
                return new FacetBucketDto { Bucket = b.Bucket, Chips = shown, More = b.Chips.Count - shown.Count };
            })
            .ToList();

        var works = source.Entries.Select(e => e.WorkId).Distinct().ToList();
        if (container)
            return new InfoPanelDto { NodeId = source.Id, Mode = InfoPanelMode.Container, Buckets = buckets, WorksBelow = works.Count };

        // Work mode: the work's title and the locations the viewer may see - plus this node's own library, which the viewer is
        // looking at directly (Incognito filters discovery, not direct access).
        var workId = works[0];
        var title = await _db.FacetWorks.AsNoTracking().Where(w => w.Id == workId).Select(w => w.Title).FirstOrDefaultAsync(ct) ?? "";
        var locationScope = FacetViewerScope.Of(scope.Shown.Append(source.LibraryId));
        var locations = (await QueryLocations.OfWorksAsync(_db, [workId], locationScope, ct)).GetValueOrDefault(workId) ?? [];
        var listed = locations.Take(MaxWorkLocations).ToList();
        var dtos = await QueryLocations.ToDtosAsync(_db, listed, ct);
        return new InfoPanelDto
        {
            NodeId = source.Id,
            Mode = InfoPanelMode.Work,
            Buckets = buckets,
            Work = new FacetWorkSummaryDto
            {
                Title = title,
                LocationCount = locations.Count,
                Locations = listed.Select(l => dtos[l.NodeId]).ToList(),
            },
        };
    }

    /// <summary>
    /// Every chip of every bucket, in the panel's order (container: by how many works here carry it, then the viewer's count, then the
    /// name; a single work: by name), with the viewer's counts. The panel shows the first <see cref="ContainerChipsPerBucket"/> of a
    /// container's bucket; the bucket page (1.47.0) serves the rest from the SAME list. Creator chips carry their live creator's id.
    /// </summary>
    private async Task<(List<(FacetBucket Bucket, List<FacetChipDto> Chips)> Buckets, bool Container)> OrderedBucketsAsync(
        List<Entry> entries, bool forceContainer, FacetViewerScope scope, CancellationToken ct)
    {
        var works = entries.Select(e => e.WorkId).Distinct().ToList();
        var container = forceContainer || works.Count > 1;

        // Merged creators: a chip always names the live creator (the redirect target), never the merged one.
        var values = await QueryValues.ByIdAsync(_db, entries.Select(e => e.ValueId), ct);
        var creatorIds = values.Values.Where(v => v.CreatorId is not null).Select(v => v.CreatorId!.Value).ToList();
        var live = await QueryValues.LiveCreatorsAsync(_db, creatorIds, ct);
        var redirected = live.Where(p => p.Key != p.Value).Select(p => p.Value).ToList();
        var targets = redirected.Count == 0
            ? new Dictionary<long, QueryValues.Value>()
            : await QueryValues.CreatorValuesAsync(_db, redirected, ct);
        Value Resolve(long valueId)
        {
            var v = values[valueId];
            return v.CreatorId is { } c && live.TryGetValue(c, out var to) && to != c && targets.TryGetValue(to, out var target)
                ? new Value(target, v)
                : new Value(v, null);
        }

        var groups = entries
            .Where(e => values.ContainsKey(e.ValueId))
            .GroupBy(e => Resolve(e.ValueId).Shown.Id)
            .Select(g =>
            {
                var resolved = Resolve(g.First().ValueId);
                return new
                {
                    resolved.Shown,
                    Here = g.Select(e => e.WorkId).Distinct().Count(),
                    Roles = (FacetRoles)g.Aggregate(0, (acc, e) => acc | e.Roles),
                    Conflict = g.Any(e => e.Conflict),
                };
            })
            .ToList();

        var counts = await _counts.CountAsync(scope, groups.Select(g => g.Shown.Id).ToList(), ct);

        // The live creator of each creator chip (a value whose merge target has no value of its own keeps its value, named by the
        // creator it was merged into), as public ids.
        long? LiveCreator(QueryValues.Value v) =>
            v.CreatorId is { } c ? (live.TryGetValue(c, out var to) ? to : c) : null;
        var creatorInternal = groups.Where(g => g.Shown.Bucket == FacetBucket.Creators)
            .Select(g => LiveCreator(g.Shown)).OfType<long>().Distinct().ToList();
        var creatorPublic = await QueryValues.CreatorPublicIdsAsync(_db, creatorInternal, ct);

        var buckets = groups
            .GroupBy(g => g.Shown.Bucket)
            .OrderBy(b => (int)b.Key)
            .Select(b =>
            {
                var ordered = container
                    ? b.OrderByDescending(g => g.Here).ThenByDescending(g => counts.GetValueOrDefault(g.Shown.Id))
                        .ThenBy(g => g.Shown.Display, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Shown.Id).ToList()
                    : b.OrderBy(g => g.Shown.Display, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Shown.Id).ToList();
                var chips = ordered.Select(g =>
                {
                    var count = counts.GetValueOrDefault(g.Shown.Id);
                    return new FacetChipDto
                    {
                        Id = g.Shown.PublicId,
                        Display = g.Shown.Display,
                        Count = count >= 2 ? count : null,
                        Here = container ? g.Here : null,
                        Roles = b.Key == FacetBucket.Creators ? g.Roles & (FacetRoles.Writer | FacetRoles.Artist) : FacetRoles.None,
                        Conflict = g.Conflict,
                        Filter = FacetBucketTokens.Token(b.Key) + ":" + g.Shown.PublicId,
                        CreatorId = b.Key == FacetBucket.Creators && LiveCreator(g.Shown) is { } creator
                            ? creatorPublic.GetValueOrDefault(creator)
                            : null,
                    };
                }).ToList();
                return (Bucket: b.Key, Chips: chips);
            })
            .ToList();
        return (buckets, container);
    }

    /// <summary>A value as the chip shows it (<see cref="Shown"/>) and the merged value it stands in for, if any.</summary>
    private sealed record Value(QueryValues.Value Shown, QueryValues.Value? From);
}
