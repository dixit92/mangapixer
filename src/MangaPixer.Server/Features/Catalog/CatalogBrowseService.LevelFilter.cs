namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Operations.Perf;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

// 1.49.0 lane J - the level-filter pass of the browse listing, extracted from BrowseAsync so the jump index (JumpIndexService) lists
// exactly the rows browse lists under the same filters. A pure move: the statements, their order and the answers are those of
// 1.48.0's inline code (browse answers are pinned byte-for-byte by LevelFilterEquivalenceTests and the existing browse tests).

/// <summary>
/// The filters browse applies to one listed level (a folder's children or a library root): favorites only, the read-state filter, hide
/// empty folders, and the Completion answer to keep (1.44.0). <see cref="None"/> keeps the whole level.
/// </summary>
public sealed record LevelFilter(
    BrowseReadStateFilter ReadState = BrowseReadStateFilter.All,
    bool HideEmpty = false,
    bool FavoritesOnly = false,
    SeriesAnswer? Completion = null)
{
    /// <summary>No filter: the level as it is.</summary>
    public static LevelFilter None { get; } = new();

    /// <summary>Whether any filter is on.</summary>
    public bool IsActive => ReadState != BrowseReadStateFilter.All || HideEmpty || FavoritesOnly || Completion is not null;

    /// <summary>
    /// Parses the read-state filter query param (1.10.0), shared by browse and the jump index. Tolerant of the wire values
    /// "reading" / "read" / "unread" (case-insensitive); anything else - including null and "all" - disables the filter, so older
    /// clients and omitted params behave as before.
    /// </summary>
    public static BrowseReadStateFilter ParseReadState(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return BrowseReadStateFilter.All;
        if (value.Equals("reading", StringComparison.OrdinalIgnoreCase))
            return BrowseReadStateFilter.Reading;
        if (value.Equals("read", StringComparison.OrdinalIgnoreCase))
            return BrowseReadStateFilter.Read;
        if (value.Equals("unread", StringComparison.OrdinalIgnoreCase))
            return BrowseReadStateFilter.Unread;
        return BrowseReadStateFilter.All;
    }

    /// <summary>
    /// The Completion filter query param (1.44.0 lane C) - a <see cref="SeriesAnswer"/> name ("HaveItAll", "FinishedMissing", "UpToDate",
    /// "MissingSome", "CantTell"; case-insensitive). Anything else, numbers included, means no filter (tolerant, like the read state).
    /// </summary>
    public static SeriesAnswer? ParseCompletion(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(char.IsAsciiLetter)
            && Enum.TryParse<SeriesAnswer>(value, ignoreCase: true, out var answer) && Enum.IsDefined(answer)
            ? answer
            : null;
}

/// <summary>
/// What <see cref="CatalogBrowseService.ApplyLevelFiltersAsync"/> answers: the filtered level query (not counted, no cursor), the level's
/// linked series (by node id, before any filter - browse reports their count), the Completion counts (null unless the Completion filter
/// applied) and the measurement-only phase clock, carried on for the caller's next lap.
/// </summary>
internal sealed record LevelFilterResult(
    IQueryable<CatalogNodeEntity> Query,
    Dictionary<long, long> LinkedSeries,
    BrowseCompletionCountsDto? CompletionCounts,
    PerfPhase Phase);

public sealed partial class CatalogBrowseService
{
    /// <summary>
    /// Applies <paramref name="filter"/> to <paramref name="level"/> - the live children of one parent (authorization, library, parent
    /// and availability already applied by the caller) - and returns the query that lists the kept rows. Shared by browse (which then
    /// counts, pages and enriches it) and the jump index (which builds the rail over the same kept rows), so both see one level.
    /// <paramref name="continuation"/> is browse's "this is a later page" flag, which only lets the Completion answers reuse the 60 s memo.
    /// </summary>
    internal async Task<LevelFilterResult> ApplyLevelFiltersAsync(
        IQueryable<CatalogNodeEntity> level,
        long userId,
        long libraryId,
        long? parentId,
        LevelFilter filter,
        bool continuation,
        PerfPhase perfPhase,
        CancellationToken ct)
    {
        var baseQuery = level;
        var readState = filter.ReadState;
        var hideEmpty = filter.HideEmpty;
        var completion = filter.Completion;

        // 1.44.0 lane C: the level's linked series, before any filter - the Completion section shows only where there are some.
        // 1.48.0: none while "Show series information" is off (library or global), as the (i) flags and search - so the
        // Completion section, its counts and the filter are gone there for every user.
        var linkedSeries = await IsSeriesInfoHiddenAsync(libraryId, ct)
            ? new Dictionary<long, long>()
            : await LinkedSeriesOfLevelAsync(baseQuery, ct);
        perfPhase = perfPhase.Lap("browse.linked-series");

        // Favorites-only filter (1.21.0): keep only nodes the user has starred. Applied
        // to the base query BEFORE counting/pagination so it composes with the read-state
        // and hide-empty filters and yields an accurate TotalCount across keyset pages.
        if (filter.FavoritesOnly)
        {
            baseQuery = baseQuery.Where(n =>
                _db.Favorites.Any(f => f.UserId == userId && f.CatalogNodeId == n.Id));
        }

        // Read-state filter (1.10.0) + hide-empty filter (1.11.0), applied to the base
        // query BEFORE counting/pagination (alongside the authorization filter) so both
        // compose with keyset paging + infinite scroll and yield an accurate TotalCount.
        //
        // ARCHIVES honor the read state exactly as the cards / folder rollup do: Read =
        // a sticky read-mark; Reading = no mark AND an in-progress ReadingProgress row;
        // Unread = neither.
        //
        // FOLDERS are filtered by their descendant read ROLLUP (1.11.0 fix - previously
        // folders were always kept, so at an all-folders level the filter did nothing,
        // the owner's "filters do not work" report). The rollup is the SAME aggregate the
        // badge uses (ResolveFolderReadRollupsAsync -> FolderReadRollupRules.Classify),
        // computed here in a PRE-PASS over this level's direct child folders so the
        // matching set is folded into the base query BEFORE the page is cut - keeping
        // TotalCount, HasMore, and the cursor correct across page boundaries. Mapping:
        // Read keeps folders that roll up to Read (every readable descendant archive is
        // read); Reading keeps Reading; Unread keeps Unread AND empty folders (no readable
        // descendant archive = no activity = not "read"; use hideEmpty to drop those).
        //
        // hide-empty (1.11.0) drops folders whose entire subtree contains no readable
        // archive. A folder appears in the rollup dictionary IFF it has >=1 readable
        // descendant archive, so "present in the rollups" is exactly the non-empty
        // predicate - it reuses the same pre-pass and composes with the read state.
        if (readState != BrowseReadStateFilter.All || hideEmpty)
        {
            var levelFolderIds = await baseQuery
                .Where(n => n.Kind == (int)CatalogNodeKind.Folder)
                .Select(n => n.Id)
                .ToListAsync(ct);

            var folderRollups = levelFolderIds.Count > 0
                ? await ResolveFolderReadRollupsAsync(levelFolderIds, userId, ct)
                : new Dictionary<long, FolderReadRollup>();

            IEnumerable<long> keptFolders = levelFolderIds;
            if (readState != BrowseReadStateFilter.All)
                keptFolders = keptFolders.Where(id => MatchesFolderReadState(folderRollups, id, readState));
            if (hideEmpty)
                keptFolders = keptFolders.Where(folderRollups.ContainsKey);
            var keptFolderIds = keptFolders.ToList();

            switch (readState)
            {
                case BrowseReadStateFilter.Read:
                    baseQuery = baseQuery.Where(n =>
                        (n.Kind == (int)CatalogNodeKind.Archive
                            && _db.ReadMarks.Any(m => m.UserId == userId && m.ItemId == n.Id))
                        || (n.Kind == (int)CatalogNodeKind.Folder && keptFolderIds.Contains(n.Id)));
                    break;
                case BrowseReadStateFilter.Reading:
                    baseQuery = baseQuery.Where(n =>
                        (n.Kind == (int)CatalogNodeKind.Archive
                            && !_db.ReadMarks.Any(m => m.UserId == userId && m.ItemId == n.Id)
                            && _db.ReadingProgress.Any(p =>
                                p.UserId == userId && p.ItemId == n.Id && p.State == (int)ReadingState.InProgress))
                        || (n.Kind == (int)CatalogNodeKind.Folder && keptFolderIds.Contains(n.Id)));
                    break;
                case BrowseReadStateFilter.Unread:
                    baseQuery = baseQuery.Where(n =>
                        (n.Kind == (int)CatalogNodeKind.Archive
                            && !_db.ReadMarks.Any(m => m.UserId == userId && m.ItemId == n.Id)
                            && !_db.ReadingProgress.Any(p =>
                                p.UserId == userId && p.ItemId == n.Id && p.State == (int)ReadingState.InProgress))
                        || (n.Kind == (int)CatalogNodeKind.Folder && keptFolderIds.Contains(n.Id)));
                    break;
                case BrowseReadStateFilter.All:
                default:
                    // hide-empty only: keep every archive, keep only non-empty folders.
                    baseQuery = baseQuery.Where(n =>
                        n.Kind == (int)CatalogNodeKind.Archive
                        || keptFolderIds.Contains(n.Id));
                    break;
            }
        }

        // 1.44.0 lane C: the Completion filter, last (over what the other filters kept), folded in before counting. It applies only
        // where the level holds linked series - elsewhere it does nothing (never an empty list); other folders and files drop.
        BrowseCompletionCountsDto? completionCounts = null;
        if (completion is { } wanted && linkedSeries.Count > 0)
        {
            perfPhase = perfPhase.Lap("browse.read-filter");
            (baseQuery, completionCounts) = await ApplyCompletionAsync(baseQuery, linkedSeries, libraryId, parentId, wanted,
                continuation, ct);
            perfPhase = perfPhase.Lap("browse.completion");
        }

        return new LevelFilterResult(baseQuery, linkedSeries, completionCounts, perfPhase);
    }
}
