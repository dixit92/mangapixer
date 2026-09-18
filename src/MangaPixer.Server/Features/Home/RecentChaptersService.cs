namespace com.lifepixer.mangapixer.Server.Features.Home;

using System.Data;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
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
/// <item><description>
/// Every stack carries a derived <see cref="RecentChapterStack.ReadState"/> (1.20.0), the same
/// read rollup (<see cref="FolderReadRollupRules"/>) the optional <c>readState</c> filter uses —
/// resolved once per library group so the tag and the filter can never disagree.
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
    /// Default recency window (days) that defines "recently added" for the home surface, used
    /// when the caller has no stored preference (<see cref="ReaderPreferencesEntity.HomeRecentWindowDays"/>
    /// is 0/unset). An archive counts toward a stack — and a stack appears at all — only if it
    /// was added within the window, so <c>NewCount</c> reflects genuinely new chapters rather
    /// than a whole back catalogue.
    /// </summary>
    public const int DefaultWindowDays = 30;

    /// <summary>Minimum per-user window (days) accepted from stored preferences.</summary>
    public const int MinWindowDays = 1;

    /// <summary>Maximum per-user window (days) accepted from stored preferences.</summary>
    public const int MaxWindowDays = 365;

    /// <summary>Default recency window, as a <see cref="TimeSpan"/> (kept for callers/tests that want it pre-1.12.0-refinement style).</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(DefaultWindowDays);

    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _auth;

    public RecentChaptersService(MangaPixerDbContext db, LibraryAuthorizationService auth)
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
        HomeReadStateFilter readState = HomeReadStateFilter.All,
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

        var windowDays = await ResolveWindowDaysAsync(userId, ct);
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(windowDays);

        var groups = new List<RecentChaptersLibraryGroup>(libs.Count);
        foreach (var lib in libs)
        {
            var stacks = await BuildStacksAsync(lib.Id, cutoff, cap, userId, readState, ct);
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
    /// Resolves the caller's "recently added" window in days (1.12.0 refinement): reads
    /// <see cref="ReaderPreferencesEntity.HomeRecentWindowDays"/> for the user and
    /// clamps it to <see cref="MinWindowDays"/>..<see cref="MaxWindowDays"/>; 0/unset (no stored
    /// preferences row, or a row whose value is still 0) falls back to <see cref="DefaultWindowDays"/>.
    /// Queried here rather than passed in by the controller — this service already owns every
    /// other per-user home-surface lookup (visibility, home-excluded libraries), so keeping the
    /// preference read alongside them keeps <see cref="RecentChaptersController"/> a thin
    /// pass-through and avoids a second per-user round trip at the call site.
    /// </summary>
    private async Task<int> ResolveWindowDaysAsync(long userId, CancellationToken ct)
    {
        var stored = await _db.ReaderPreferences
            .Where(p => p.UserId == userId)
            .Select(p => (int?)p.HomeRecentWindowDays)
            .FirstOrDefaultAsync(ct);

        return stored is null or 0
            ? DefaultWindowDays
            : Math.Clamp(stored.Value, MinWindowDays, MaxWindowDays);
    }

    /// <summary>
    /// Builds one library's stacks: window-filter the candidate archives, attribute each to its
    /// top-level ancestor, aggregate per stack, order by newest activity, cap.
    /// </summary>
    private async Task<IReadOnlyList<RecentChapterStack>> BuildStacksAsync(
        long libraryId,
        DateTimeOffset cutoff,
        int cap,
        long userId,
        HomeReadStateFilter readState,
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
                ReadState = ReadStateWireValue(null), // placeholder; set below once rollups resolve
            }, topId));
        }

        // Read-state rollup (1.20.0 tag + 1.17.0 filter, unified): resolved for EVERY stack —
        // one bounded rollup query per library group, same as the folder-cover resolution below
        // — so the per-card tag and the read-state filter are always derived from the same
        // rollup and never disagree. Rollup is over each stack's TOP-LEVEL node's readable
        // descendants (its whole subtree for a folder stack, or just itself for a standalone
        // archive stack) — NOT limited to the recency-window candidates — so a stack's read
        // state matches what the folder rollup badge / archive card would show if the user
        // browsed to it directly.
        var rollups = await ResolveReadRollupsAsync(stacks.Select(s => s.TopId).ToList(), userId, ct);
        stacks = stacks
            .Select(s => (s.Stack with { ReadState = ReadStateWireValue(ResolveRollup(rollups, s.TopId)) }, s.TopId))
            .ToList();

        // Read-state filter (1.17.0), applied BEFORE the newest-activity ordering and the
        // per-library cap so a filtered-out stack never displaces one that matches — mirrors
        // the browse view applying its read-state filter to the base query before pagination.
        if (readState != HomeReadStateFilter.All)
            stacks = stacks.Where(s => MatchesReadState(rollups, s.TopId, readState)).ToList();

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

    /// <summary>
    /// Resolves the derived read rollup (<see cref="FolderReadRollupRules"/>, same rule the
    /// browse view's folder badge uses) for each given TOP-LEVEL stack node, via a single
    /// recursive CTE. Works uniformly whether the root is a folder (rolled up over every
    /// descendant archive) or a standalone archive (the root itself is the sole readable
    /// descendant, since the base case of the recursive walk includes the root row): the
    /// archive join only requires <c>Kind = 1</c>, not that the root be a folder. Root ids
    /// with no readable (non-tombstoned) descendant archive are omitted (null rollup) — same
    /// as <c>CatalogBrowseService.ResolveFolderReadRollupsAsync</c>, which this mirrors.
    /// </summary>
    private async Task<Dictionary<long, FolderReadRollup>> ResolveReadRollupsAsync(
        List<long> topLevelIds,
        long userId,
        CancellationToken ct)
    {
        var result = new Dictionary<long, FolderReadRollup>();
        if (topLevelIds.Count == 0)
            return result;

        var ids = string.Join(",", topLevelIds);
        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            // Kind = 1 is Archive; Availability = 5 is Tombstoned; State = 1 is InProgress
            // (same literals as CatalogBrowseService's sibling CTE).
            command.CommandText = $"""
                WITH RECURSIVE subtree(RootId, NodeId) AS (
                    SELECT r.Id, r.Id FROM catalog_nodes r WHERE r.Id IN ({ids})
                    UNION ALL
                    SELECT s.RootId, cn.Id FROM subtree s
                    JOIN catalog_nodes cn ON cn.ParentId = s.NodeId
                )
                SELECT s.RootId,
                       COUNT(*) AS Total,
                       SUM(CASE WHEN rm.ItemId IS NOT NULL THEN 1 ELSE 0 END) AS ReadCount,
                       SUM(CASE WHEN rm.ItemId IS NULL AND rp.State = 1 THEN 1 ELSE 0 END) AS InProgressCount
                FROM subtree s
                JOIN catalog_nodes a ON a.Id = s.NodeId AND a.Kind = 1 AND a.Availability != 5
                LEFT JOIN read_marks rm ON rm.ItemId = a.Id AND rm.UserId = $user
                LEFT JOIN reading_progress rp ON rp.ItemId = a.Id AND rp.UserId = $user
                GROUP BY s.RootId;
                """;
            var p = command.CreateParameter();
            p.ParameterName = "$user";
            p.Value = userId;
            command.Parameters.Add(p);

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var rollup = FolderReadRollupRules.Classify(
                    total: reader.GetInt32(1),
                    read: reader.GetInt32(2),
                    inProgress: reader.GetInt32(3));
                if (rollup is not null)
                    result[reader.GetInt64(0)] = rollup.Value;
            }
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }

    /// <summary>
    /// Whether a stack matches the read-state filter by its top-level node's read rollup.
    /// A MISSING <paramref name="rollups"/> entry means no readable descendant archive
    /// (cannot occur in practice — every stack has &gt;= 1 in-window candidate archive — but
    /// handled defensively the same way the browse view's folder filter does): Unread also
    /// keeps that case, Read/Reading require the matching rollup.
    /// </summary>
    private static bool MatchesReadState(
        Dictionary<long, FolderReadRollup> rollups,
        long topId,
        HomeReadStateFilter readState)
    {
        var has = rollups.TryGetValue(topId, out var rollup);
        return readState switch
        {
            HomeReadStateFilter.Read => has && rollup == FolderReadRollup.Read,
            HomeReadStateFilter.Reading => has && rollup == FolderReadRollup.Reading,
            HomeReadStateFilter.Unread => !has || rollup == FolderReadRollup.Unread,
            _ => true,
        };
    }

    /// <summary>Looks up a stack's rollup by its top-level node id, or null when absent (same "no readable descendant" case <see cref="MatchesReadState"/> treats as Unread).</summary>
    private static FolderReadRollup? ResolveRollup(Dictionary<long, FolderReadRollup> rollups, long topId) =>
        rollups.TryGetValue(topId, out var rollup) ? rollup : null;

    /// <summary>
    /// Maps a rollup to the stack's <see cref="RecentChapterStack.ReadState"/> wire value
    /// (1.20.0): lower-case <c>"read"</c> / <c>"reading"</c> / <c>"unread"</c>, matching the
    /// <c>readState</c> query param's own casing rather than the PascalCase
    /// <see cref="FolderReadRollup"/> enum names. <c>null</c> (no readable descendant archive)
    /// reports <c>"unread"</c>, the same fallback <see cref="MatchesReadState"/> uses for the
    /// <see cref="HomeReadStateFilter.Unread"/> filter.
    /// </summary>
    private static string ReadStateWireValue(FolderReadRollup? rollup) => rollup switch
    {
        FolderReadRollup.Read => "read",
        FolderReadRollup.Reading => "reading",
        _ => "unread",
    };

    private sealed record CandidateArchive
    {
        public required long Id { get; init; }
        public required string PublicId { get; init; }
        public required string DisplayName { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }
}

/// <summary>
/// Home "New chapters" read-state filter (1.17.0), mirroring
/// <c>CatalogBrowseService.BrowseReadStateFilter</c> but scoped to the New chapters view: a
/// stack is kept when its TOP-LEVEL node's read rollup matches (<see cref="FolderReadRollupRules"/>
/// over the node's readable descendant archives — the whole subtree for a folder stack, just
/// itself for a standalone archive stack). <see cref="All"/> disables the filter (server
/// default — no breaking change for existing callers).
/// </summary>
public enum HomeReadStateFilter
{
    All,
    Reading,
    Read,
    Unread,
}
