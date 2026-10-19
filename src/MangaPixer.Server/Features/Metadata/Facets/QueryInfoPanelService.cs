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
        var node = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.PublicId == nodePublicId)
            .Select(n => new { n.Id, n.LibraryId, n.Availability })
            .FirstOrDefaultAsync(ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return null;
        if (!await _auth.CanAccessLibraryAsync(userId, node.LibraryId, ct))
            return null;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (await _scopes.IsHiddenAsync(node.LibraryId, ct))
            return Empty(nodePublicId);

        var scope = await _scopes.ResolveAsync(userId, incognito, ct);

        InfoPanelDto panel;
        var own = await EntriesAtAsync([node.Id], ct);
        if (own.Count > 0)
        {
            panel = await BuildAsync(nodePublicId, own, scope, node.LibraryId, ct);
        }
        else
        {
            var below = await EntriesBelowAsync(node.Id, ct);
            if (below.Count > 0)
            {
                panel = await BuildAsync(nodePublicId, below, scope, node.LibraryId, ct, forceContainer: true);
            }
            else
            {
                var ancestors = await AncestorsAsync(node.Id, ct);
                var atAncestors = await EntriesAtAsync(ancestors, ct);
                var nearest = ancestors.FirstOrDefault(id => atAncestors.Any(e => e.LocationNodeId == id));
                panel = nearest == 0
                    ? Empty(nodePublicId)
                    : await BuildAsync(nodePublicId, atAncestors.Where(e => e.LocationNodeId == nearest).ToList(), scope, node.LibraryId, ct);
            }
        }

        _logger.LogDebug(LogEvents.Metadata.InfoPanelServed,
            "Info panel for node {NodeId}: mode {Mode}, {Buckets} buckets, in {ElapsedMs} ms",
            node.Id, panel.Mode, panel.Buckets.Count, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return panel;
    }

    /// <summary>The panel of a library root (Container mode over the whole library); null when unknown or not accessible.</summary>
    public async Task<InfoPanelDto?> ForLibraryAsync(long userId, bool incognito, string libraryPublicId, CancellationToken ct = default)
    {
        var library = await _db.Libraries.AsNoTracking()
            .Where(l => l.PublicId == libraryPublicId)
            .Select(l => new { l.Id })
            .FirstOrDefaultAsync(ct);
        if (library is null || !await _auth.CanAccessLibraryAsync(userId, library.Id, ct))
            return null;
        if (await _scopes.IsHiddenAsync(library.Id, ct))
            return Empty(libraryPublicId);

        var scope = await _scopes.ResolveAsync(userId, incognito, ct);
        var entries = await QuerySql.ReadAsync(_db, $"""
            SELECT e.WorkId, e.ValueId, e.LocationNodeId, e.Roles, e.Conflict
            FROM facet_entries e
            JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
            WHERE e.LibraryId = @library
            """, new Dictionary<string, object?> { ["@library"] = library.Id }, ReadEntry, ct);
        return entries.Count == 0
            ? Empty(libraryPublicId)
            : await BuildAsync(libraryPublicId, entries, scope, library.Id, ct, forceContainer: true);
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

    private Task<List<Entry>> EntriesBelowAsync(long nodeId, CancellationToken ct) =>
        QuerySql.ReadAsync(_db, $"""
            WITH RECURSIVE below(Id) AS (
                SELECT Id FROM catalog_nodes WHERE ParentId = @node
                UNION ALL
                SELECT c.Id FROM catalog_nodes c JOIN below ON c.ParentId = below.Id
            )
            SELECT e.WorkId, e.ValueId, e.LocationNodeId, e.Roles, e.Conflict
            FROM below
            JOIN facet_entries e ON e.LocationNodeId = below.Id
            JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
            """, new Dictionary<string, object?> { ["@node"] = nodeId }, ReadEntry, ct);

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

    private async Task<InfoPanelDto> BuildAsync(
        string id, List<Entry> entries, FacetViewerScope scope, long libraryId, CancellationToken ct, bool forceContainer = false)
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

        var buckets = groups
            .GroupBy(g => g.Shown.Bucket)
            .OrderBy(b => (int)b.Key)
            .Select(b =>
            {
                var ordered = container
                    ? b.OrderByDescending(g => g.Here).ThenByDescending(g => counts.GetValueOrDefault(g.Shown.Id))
                        .ThenBy(g => g.Shown.Display, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Shown.Id).ToList()
                    : b.OrderBy(g => g.Shown.Display, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Shown.Id).ToList();
                var shown = container ? ordered.Take(ContainerChipsPerBucket).ToList() : ordered;
                return new FacetBucketDto
                {
                    Bucket = b.Key,
                    Chips = shown.Select(g =>
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
                        };
                    }).ToList(),
                    More = ordered.Count - shown.Count,
                };
            })
            .ToList();

        if (container)
            return new InfoPanelDto { NodeId = id, Mode = InfoPanelMode.Container, Buckets = buckets, WorksBelow = works.Count };

        // Work mode: the work's title and the locations the viewer may see - plus this node's own library, which the viewer is
        // looking at directly (Incognito filters discovery, not direct access).
        var workId = works[0];
        var title = await _db.FacetWorks.AsNoTracking().Where(w => w.Id == workId).Select(w => w.Title).FirstOrDefaultAsync(ct) ?? "";
        var locationScope = FacetViewerScope.Of(scope.Shown.Append(libraryId));
        var locations = (await QueryLocations.OfWorksAsync(_db, [workId], locationScope, ct)).GetValueOrDefault(workId) ?? [];
        var listed = locations.Take(MaxWorkLocations).ToList();
        var dtos = await QueryLocations.ToDtosAsync(_db, listed, ct);
        return new InfoPanelDto
        {
            NodeId = id,
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

    /// <summary>A value as the chip shows it (<see cref="Shown"/>) and the merged value it stands in for, if any.</summary>
    private sealed record Value(QueryValues.Value Shown, QueryValues.Value? From);
}
