namespace com.lifepixer.mangapixer.Server.Features.Home;

using System.Data;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Home "New chapters" service (1.12.0 rewrite; grouped by the series folder in 1.30.0). Returns recently-added
/// archives STACKED by the series folder they belong to for each library the caller can see, grouped by library,
/// newest activity first, capped per library.
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
/// candidate is attributed to its SERIES FOLDER (see <see cref="ResolveSeriesFoldersAsync"/>) via
/// one bounded recursive-CTE upward walk: the nearest ancestor with its own series link; else the
/// folder that holds the archive, climbing out of generic unit folders ("Vol 3", "Chapters"). In a
/// library whose top level is categories (Manga, Manhwa, ...) that is the series, not the category.
/// A candidate that is itself a direct library child is its own standalone stack
/// (<see cref="RecentChapterStack.IsFolder"/> = false).
/// </description></item>
/// <item><description>
/// Within a library, stacks are ordered by <see cref="RecentChapterStack.LatestAddedAt"/>
/// descending; <c>perLibrary</c> (default 12, max 50) caps STACKS. <c>NewCount</c> is the number
/// of candidate archives attributed to the stack (always ≥ 1).
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
    private readonly SeriesInfoFlagService _seriesInfoFlags;
    private readonly ICoverResolver _covers;

    public RecentChaptersService(MangaPixerDbContext db, LibraryAuthorizationService auth, SeriesInfoFlagService? seriesInfoFlags = null,
        ICoverResolver? covers = null)
    {
        _db = db;
        _auth = auth;
        _seriesInfoFlags = seriesInfoFlags ?? new SeriesInfoFlagService(db);
        _covers = covers ?? new FileCoverResolver(db);
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

        return new RecentChaptersDto { Libraries = await ApplyCardFlagsAsync(userId, groups, ct) };
    }

    /// <summary>
    /// Sets the Home card flags (1.28.0) on every stack of every group at once: the favorites
    /// star on the stack's node (one query) and <see cref="RecentChapterStack.HasSeriesInfo"/>
    /// (a folder stack by the browse rule, a loose archive by the series-info anchor rule -
    /// <see cref="SeriesInfoFlagService.WithAnchoredSeriesInfoAsync"/>). A fixed number of
    /// queries for the whole response, not per library or per card.
    /// </summary>
    private async Task<List<RecentChaptersLibraryGroup>> ApplyCardFlagsAsync(
        long userId, List<RecentChaptersLibraryGroup> groups, CancellationToken ct)
    {
        var ids = groups.SelectMany(g => g.Stacks).Select(s => s.Id).ToList();
        if (ids.Count == 0)
            return groups;

        var starred = await FavoriteFlags.StarredAsync(_db, userId, ids, ct);
        var withInfo = await _seriesInfoFlags.WithAnchoredSeriesInfoAsync(ids, ct);
        return groups
            .Select(g => g with
            {
                Stacks = g.Stacks
                    .Select(s => s with { IsFavorite = starred.Contains(s.Id), HasSeriesInfo = withInfo.Contains(s.Id) })
                    .ToList(),
            })
            .ToList();
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
    /// series folder, aggregate per stack, order by newest activity, cap.
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

        // Map each candidate archive to its series folder, one bounded recursive upward walk.
        // An archive directly in the library root maps to itself.
        var seriesByArchive = await ResolveSeriesFoldersAsync(
            candidates.Select(c => c.Id).ToList(), ct);

        // Group candidates by their series folder.
        var bySeries = new Dictionary<long, List<CandidateArchive>>();
        foreach (var c in candidates)
        {
            if (!seriesByArchive.TryGetValue(c.Id, out var topId))
                continue; // defensive: every node has a root
            if (!bySeries.TryGetValue(topId, out var list))
                bySeries[topId] = list = [];
            list.Add(c);
        }

        if (bySeries.Count == 0)
            return [];

        // Series-folder node details for the stacks.
        var seriesIds = bySeries.Keys.ToList();
        var seriesNodes = await _db.CatalogNodes
            .Where(n => seriesIds.Contains(n.Id))
            .Select(n => new { n.Id, n.PublicId, n.DisplayName, n.Kind })
            .ToDictionaryAsync(x => x.Id, ct);

        var stacks = new List<(RecentChapterStack Stack, long TopId)>(bySeries.Count);
        foreach (var (topId, list) in bySeries)
        {
            if (!seriesNodes.TryGetValue(topId, out var top))
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
        // rollup and never disagree. Rollup is over each stack node's readable
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

        // Covers for the taken stacks from the cover resolver (1.29.0 seam): a folder stack shows the same cover as its
        // browse card (its first descendant archive by SortKey, one recursive CTE), a loose-archive stack its own cover.
        var covers = await _covers.ResolveUrlsAsync(
            ordered.Select(s => new CoverTarget(s.TopId, s.Stack.Id, s.Stack.IsFolder)).ToList(), ct);

        return ordered
            .Select(s => covers.TryGetValue(s.TopId, out var url) ? s.Stack with { CoverUrl = url } : s.Stack)
            .ToList();
    }

    /// <summary>
    /// Maps each given archive node id to the node its stack is built on (1.30.0), via a single bounded recursive
    /// upward walk. The rule, in order:
    /// <list type="number">
    /// <item><description>the nearest ancestor FOLDER with its own series link (confirmed or auto, with a record) -
    /// the series folder the link names, however deep;</description></item>
    /// <item><description>else the folder that holds the archive (the nearest folder that directly holds archives),
    /// climbing out of generic unit folders (<c>Vol 3</c>, <c>Chapters</c> - the names the Volumes view merges) so
    /// <c>Series/Vol 1</c> and <c>Series/Vol 2</c> stay one stack;</description></item>
    /// <item><description>an archive directly in the library root has no folder: it maps to itself (a standalone stack).</description></item>
    /// </list>
    /// In a library whose top level is categories (<c>Manga/Series/ch.cbz</c>) the stack is therefore the series, not
    /// the category; in a flat library (<c>Series/ch.cbz</c>) it is the same folder as before.
    /// </summary>
    private async Task<Dictionary<long, long>> ResolveSeriesFoldersAsync(
        List<long> archiveIds,
        CancellationToken ct)
    {
        var result = new Dictionary<long, long>();
        if (archiveIds.Count == 0)
            return result;

        // ancestors[archive] = the folders above it, nearest first.
        var ancestors = new Dictionary<long, List<(int Depth, long NodeId, string Name, bool Linked)>>();
        var ids = string.Join(",", archiveIds);
        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            // State: 0 Confirmed, 1 Auto (a series link with a record); Kind 0 is a folder.
            command.CommandText = $"""
                WITH RECURSIVE up(ArchiveId, NodeId, ParentId, Depth) AS (
                    SELECT Id, Id, ParentId, 0 FROM catalog_nodes WHERE Id IN ({ids})
                    UNION ALL
                    SELECT u.ArchiveId, p.Id, p.ParentId, u.Depth + 1
                    FROM up u
                    JOIN catalog_nodes p ON p.Id = u.ParentId
                    WHERE u.Depth < {SeriesInfoResolver.MaxWalkDepth}
                )
                SELECT u.ArchiveId, u.Depth, u.NodeId, n.DisplayName,
                       EXISTS (SELECT 1 FROM node_series_links l
                               WHERE l.NodeId = u.NodeId AND l.RecordId IS NOT NULL AND l.State IN (0, 1)) AS Linked
                FROM up u
                JOIN catalog_nodes n ON n.Id = u.NodeId
                WHERE u.Depth > 0 AND n.Kind = {(int)CatalogNodeKind.Folder}
                ORDER BY u.ArchiveId, u.Depth;
                """;

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var archiveId = reader.GetInt64(0);
                if (!ancestors.TryGetValue(archiveId, out var list))
                    ancestors[archiveId] = list = [];
                list.Add((reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4) != 0));
            }
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }

        foreach (var archiveId in archiveIds)
        {
            if (!ancestors.TryGetValue(archiveId, out var chain) || chain.Count == 0)
            {
                result[archiveId] = archiveId; // loose in the library root: its own stack
                continue;
            }

            var linked = chain.FindIndex(a => a.Linked);
            var pick = linked;
            if (pick < 0)
            {
                pick = 0;
                while (pick + 1 < chain.Count && IsUnitFolderName(chain[pick].Name))
                    pick++;
            }
            result[archiveId] = chain[pick].NodeId;
        }
        return result;
    }

    /// <summary>A generic unit folder name ("Vol 3", "Volumes", "Chapters", "Ch 1-50"): part of a series, never one.</summary>
    private static bool IsUnitFolderName(string name) =>
        AutoMatchText.IsVolumeFolderName(name) || AutoMatchText.IsChapterFolderName(name);

    /// <summary>
    /// Resolves the derived read rollup (<see cref="FolderReadRollupRules"/>, same rule the
    /// browse view's folder badge uses) for each given stack node, via a single
    /// recursive CTE. Works uniformly whether the root is a folder (rolled up over every
    /// descendant archive) or a standalone archive (the root itself is the sole readable
    /// descendant, since the base case of the recursive walk includes the root row): the
    /// archive join only requires <c>Kind = 1</c>, not that the root be a folder. Root ids
    /// with no readable (non-tombstoned) descendant archive are omitted (null rollup) — same
    /// as <c>CatalogBrowseService.ResolveFolderReadRollupsAsync</c>, which this mirrors.
    /// </summary>
    private async Task<Dictionary<long, FolderReadRollup>> ResolveReadRollupsAsync(
        List<long> seriesIds,
        long userId,
        CancellationToken ct)
    {
        var result = new Dictionary<long, FolderReadRollup>();
        if (seriesIds.Count == 0)
            return result;

        var ids = string.Join(",", seriesIds);
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
    /// Whether a stack matches the read-state filter by its stack node's read rollup.
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

    /// <summary>Looks up a stack's rollup by its stack node id, or null when absent (same "no readable descendant" case <see cref="MatchesReadState"/> treats as Unread).</summary>
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
/// stack is kept when its stack node's read rollup matches (<see cref="FolderReadRollupRules"/>
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
