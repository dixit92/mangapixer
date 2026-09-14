namespace com.lifepixer.mangaplex.Server.Features.Home;

using System.Data;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Home "New chapters" service (1.12.0 rewrite). Returns recently-added archives STACKED by
/// their TOP-LEVEL unit — the direct library child they descend from — for each library the
/// caller can see, grouped by library, newest activity first, capped per library.
///
/// <para>Design:</para>
/// <list type="bullet">
/// <item><description>
/// Visibility uses <see cref="LibraryAuthorizationService.GetVisibleLibraryIdsAsync"/>
/// (accessible minus the caller's Private set while Incognito is active) — the same server-side
/// chokepoint browse/home/search use. Libraries the caller has hidden from home
/// (<c>home_excluded_libraries</c>) are then subtracted, AFTER visibility resolution and before
/// grouping.
/// </description></item>
/// <item><description>
/// "Recently-added" is bounded by a recency WINDOW (<see cref="RecentWindow"/>): the candidate
/// set per library is the non-tombstoned archives whose CreatedAt is within the window. Each
/// candidate is attributed to its TOP-LEVEL ancestor (the node with <c>ParentId == null</c>) via
/// one bounded recursive-CTE upward walk. A candidate that is itself a direct library child is
/// its own standalone stack (<see cref="RecentChapterStack.IsFolder"/> = false).
/// </description></item>
/// <item><description>
/// Within a library, stacks are ordered by <see cref="RecentChapterStack.LatestAddedAt"/>
/// descending; <c>perLibrary</c> (default 12, max 50) caps STACKS. <c>NewCount</c> is the number
/// of candidate archives attributed to the stack (always ≥ 1). Convention-agnostic: a stack is
/// never assumed to be a "series" — the top level is whatever unit the user's layout chose.
/// </description></item>
/// </list>
/// </summary>
public sealed class RecentChaptersService
{
    /// <summary>Default per-library stack cap (matches the home continue-reading limit).</summary>
    public const int DefaultPerLibrary = 12;

    /// <summary>Maximum per-library stack cap accepted from a caller.</summary>
    public const int MaxPerLibrary = 50;

    /// <summary>
    /// Recency window that defines "recently added" for the home surface (1.12.0). An archive
    /// counts toward a stack — and a stack appears at all — only if it was added within this
    /// window, so <c>NewCount</c> reflects genuinely new chapters rather than a whole back
    /// catalogue. Chosen product parameter (owner-tunable); flagged for the integrator.
    /// </summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(30);

    private readonly MangaPlexDbContext _db;
    private readonly LibraryAuthorizationService _auth;

    public RecentChaptersService(MangaPlexDbContext db, LibraryAuthorizationService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <summary>
    /// Returns the caller's per-library "New chapters" stacks, newest activity first, capped
    /// per library. Empty <see cref="RecentChaptersDto.Libraries"/> when the caller can see no
    /// (non-hidden) libraries; a library with no recent stacks appears with an empty list.
    /// </summary>
    public async Task<RecentChaptersDto> GetRecentChaptersAsync(
        long userId,
        int? perLibrary = null,
        bool incognito = false,
        CancellationToken ct = default)
    {
        var cap = perLibrary is null or < 1
            ? DefaultPerLibrary
            : Math.Min(perLibrary.Value, MaxPerLibrary);

        var visibleLibs = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
        if (visibleLibs.Count == 0)
            return new RecentChaptersDto { Libraries = [] };

        // Home library-visibility preference (1.12.0): drop libraries the caller hid from home,
        // after visibility resolution and before grouping.
        var excluded = (await _db.HomeExcludedLibraries
            .Where(h => h.UserId == userId)
            .Select(h => h.LibraryId)
            .ToListAsync(ct)).ToHashSet();

        var visibleSet = visibleLibs.Where(id => !excluded.Contains(id)).ToHashSet();
        if (visibleSet.Count == 0)
            return new RecentChaptersDto { Libraries = [] };

        // Visible libraries ordered by display name for a stable home row order.
        var libs = await _db.Libraries
            .Where(l => visibleSet.Contains(l.Id))
            .OrderBy(l => l.DisplayName)
            .Select(l => new { l.Id, l.PublicId, l.DisplayName })
            .ToListAsync(ct);

        var cutoff = DateTimeOffset.UtcNow - RecentWindow;

        var groups = new List<RecentChaptersLibraryGroup>(libs.Count);
        foreach (var lib in libs)
        {
            var stacks = await BuildStacksAsync(lib.Id, cutoff, cap, ct);
            groups.Add(new RecentChaptersLibraryGroup
            {
                LibraryId = lib.PublicId,
                LibraryName = lib.DisplayName,
                Stacks = stacks,
            });
        }

        return new RecentChaptersDto { Libraries = groups };
    }

    /// <summary>
    /// Builds one library's stacks: window-filter the candidate archives, attribute each to its
    /// top-level ancestor, aggregate per stack, order by newest activity, cap.
    /// </summary>
    private async Task<IReadOnlyList<RecentChapterStack>> BuildStacksAsync(
        long libraryId,
        DateTimeOffset cutoff,
        int cap,
        CancellationToken ct)
    {
        // Candidate archives (window-bounded). EF encodes/decodes the CreatedAt binary column.
        var candidates = await _db.CatalogNodes
            .Where(n => n.LibraryId == libraryId
                && n.Kind == (int)CatalogNodeKind.Archive
                && n.Availability != (int)CatalogNodeAvailability.Tombstoned
                && n.CreatedAt >= cutoff)
            .Select(n => new CandidateArchive
            {
                Id = n.Id,
                PublicId = n.PublicId,
                DisplayName = n.DisplayName,
                CreatedAt = n.CreatedAt,
            })
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return [];

        // Map each candidate archive to its top-level ancestor (ParentId == null), one bounded
        // recursive upward walk. A loose archive maps to itself.
        var topLevelByArchive = await ResolveTopLevelAncestorsAsync(
            candidates.Select(c => c.Id).ToList(), ct);

        // Group candidates by their top-level ancestor.
        var byTopLevel = new Dictionary<long, List<CandidateArchive>>();
        foreach (var c in candidates)
        {
            if (!topLevelByArchive.TryGetValue(c.Id, out var topId))
                continue; // defensive: every node has a root
            if (!byTopLevel.TryGetValue(topId, out var list))
                byTopLevel[topId] = list = [];
            list.Add(c);
        }

        if (byTopLevel.Count == 0)
            return [];

        // Top-level node details for the stacks.
        var topLevelIds = byTopLevel.Keys.ToList();
        var topLevelNodes = await _db.CatalogNodes
            .Where(n => topLevelIds.Contains(n.Id))
            .Select(n => new { n.Id, n.PublicId, n.DisplayName, n.Kind })
            .ToDictionaryAsync(x => x.Id, ct);

        var stacks = new List<(RecentChapterStack Stack, long TopId)>(byTopLevel.Count);
        foreach (var (topId, list) in byTopLevel)
        {
            if (!topLevelNodes.TryGetValue(topId, out var top))
                continue;

            // Newest candidate in the stack (CreatedAt desc, Id desc as a stable tiebreak).
            var latest = list
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .First();

            var isFolder = top.Kind == (int)CatalogNodeKind.Folder;
            stacks.Add((new RecentChapterStack
            {
                Id = top.PublicId,
                DisplayName = top.DisplayName,
                IsFolder = isFolder,
                CoverUrl = isFolder ? null : $"/api/v1/items/{top.PublicId}/cover",
                LatestItemId = latest.PublicId,
                LatestItemName = latest.DisplayName,
                LatestAddedAt = latest.CreatedAt,
                NewCount = list.Count,
            }, topId));
        }

        // Order by newest activity, cap to perLibrary STACKS.
        var ordered = stacks
            .OrderByDescending(s => s.Stack.LatestAddedAt)
            .ThenByDescending(s => s.TopId)
            .Take(cap)
            .ToList();

        // Resolve folder covers (first descendant archive by SortKey) for the taken folder
        // stacks in one recursive CTE, matching browse folder-cover resolution.
        var folderTopIds = ordered
            .Where(s => s.Stack.IsFolder)
            .Select(s => s.TopId)
            .ToList();
        var covers = await ResolveFolderCoversAsync(folderTopIds, ct);

        return ordered
            .Select(s => s.Stack.IsFolder && covers.TryGetValue(s.TopId, out var coverPublicId)
                ? s.Stack with { CoverUrl = $"/api/v1/items/{coverPublicId}/cover" }
                : s.Stack)
            .ToList();
    }

    /// <summary>
    /// Maps each given archive node id to its top-level ancestor node id (the ancestor with
    /// <c>ParentId == null</c>), via a single bounded recursive upward walk. An archive that is
    /// itself a direct library child maps to itself.
    /// </summary>
    private async Task<Dictionary<long, long>> ResolveTopLevelAncestorsAsync(
        List<long> archiveIds,
        CancellationToken ct)
    {
        var result = new Dictionary<long, long>();
        if (archiveIds.Count == 0)
            return result;

        var ids = string.Join(",", archiveIds);
        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE up(ArchiveId, NodeId, ParentId) AS (
                    SELECT Id, Id, ParentId FROM catalog_nodes WHERE Id IN ({ids})
                    UNION ALL
                    SELECT u.ArchiveId, p.Id, p.ParentId
                    FROM up u
                    JOIN catalog_nodes p ON p.Id = u.ParentId
                )
                SELECT ArchiveId, NodeId FROM up WHERE ParentId IS NULL;
                """;

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetInt64(0)] = reader.GetInt64(1);
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }

    /// <summary>
    /// Resolves a cover (first non-tombstoned descendant archive by SortKey) for each folder id,
    /// via one recursive CTE. Same resolution as <c>CatalogBrowseService.ResolveFolderCoversAsync</c>
    /// so a home folder stack shows the same thumbnail as its browse card. Folders with no
    /// non-tombstoned descendant archive are omitted.
    /// </summary>
    private async Task<Dictionary<long, string>> ResolveFolderCoversAsync(
        List<long> folderInternalIds,
        CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        if (folderInternalIds.Count == 0)
            return result;

        var ids = string.Join(",", folderInternalIds);
        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE descendants(RootId, NodeId, Kind, SortKey, Availability) AS (
                    SELECT r.Id, cn.Id, cn.Kind, cn.SortKey, cn.Availability
                    FROM catalog_nodes r
                    JOIN catalog_nodes cn ON cn.ParentId = r.Id
                    WHERE r.Id IN ({ids})
                    UNION ALL
                    SELECT d.RootId, cn.Id, cn.Kind, cn.SortKey, cn.Availability
                    FROM descendants d
                    JOIN catalog_nodes cn ON cn.ParentId = d.NodeId
                ),
                ranked AS (
                    SELECT d.RootId, cn.PublicId AS CoverPublicId,
                           ROW_NUMBER() OVER (PARTITION BY d.RootId ORDER BY d.SortKey, d.NodeId) AS rn
                    FROM descendants d
                    JOIN catalog_nodes cn ON d.NodeId = cn.Id
                    WHERE d.Kind = 1 AND d.Availability != 5
                )
                SELECT RootId, CoverPublicId FROM ranked WHERE rn = 1;
                """;

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetInt64(0)] = reader.GetString(1);
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }

    private sealed record CandidateArchive
    {
        public required long Id { get; init; }
        public required string PublicId { get; init; }
        public required string DisplayName { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }
}
