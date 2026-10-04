namespace com.lifepixer.mangapixer.Tests.Server.Contracts;

using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Metadata;
using Xunit;

/// <summary>
/// 1.34.0 sweep over the readers of the series link state (<c>node_series_links.State</c>). Adding a state (Collection about was the
/// fifth) is the riskiest kind of change: every reader must decide what it means there. This test lists every source file that reads
/// the state - <see cref="SeriesLinkState"/>, the <c>NodeSeriesLinks</c> set, raw <c>node_series_links</c> SQL or the cover layer's
/// nearest-link walk - with what the newest state means in it. It fails when a reader appears or disappears, and when a state is
/// added (<see cref="ReviewedForStates"/>): re-check every file below, write down its answer, then update the list.
/// </summary>
public sealed class SeriesLinkStateReaderSweepTests
{
    /// <summary>How many <see cref="SeriesLinkState"/> values the list below was reviewed for.</summary>
    private const int ReviewedForStates = 5;

    /// <summary>Every reader, with what <see cref="SeriesLinkState.CollectionAbout"/> means there.</summary>
    private static readonly Dictionary<string, string> Readers = new(StringComparer.Ordinal)
    {
        ["src/MangaPixer.Core/Api/MetadataAutoMatchDtos.cs"] = "review DTOs carry the state; Collections tab, suggestion, bulk accept",
        ["src/MangaPixer.Core/Api/MetadataDtos.cs"] = "link DTOs carry the state",
        ["src/MangaPixer.Core/Api/MetadataMissingDtos.cs"] = "Missing rows are Confirmed / Auto only: never a collection",
        ["src/MangaPixer.Core/Api/MoveConflictDtos.cs"] = "a conflict side may be a collection (web label)",
        ["src/MangaPixer.Core/Api/OfficialReleasesDtos.cs"] = "Official releases rows are Confirmed / Auto only",
        ["src/MangaPixer.Core/Metadata/MetadataVocabulary.cs"] = "the enum",
        ["src/MangaPixer.Core/Metadata/SeriesLinkStates.cs"] = "THE decisions (exhaustive switches)",
        ["src/MangaPixer.Server/Features/Catalog/CatalogBrowseService.cs"] = "alt-title search finds a collection folder (visible libraries only)",
        ["src/MangaPixer.Server/Features/Catalog/VolumeEntryService.cs"] = "Volumes view: Confirmed / Auto only - a collection has no series",
        ["src/MangaPixer.Server/Features/Covers/CoverDecisionHostedService.cs"] = "any non-review row re-decides its subtree's covers",
        ["src/MangaPixer.Server/Features/Covers/CoverDecisionService.cs"] = "own collection row: the series poster; below it: unlinked rules",
        ["src/MangaPixer.Server/Features/Covers/CoverLinks.cs"] = "nearest-row walk stops at a collection (IsOwnCollection)",
        ["src/MangaPixer.Server/Features/Covers/CoverPickerService.cs"] = "no web covers to choose for a collection",
        ["src/MangaPixer.Server/Features/Covers/CoverResolutionService.cs"] = "poster layer of an own collection row; series covers Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Covers/StackCoverService.cs"] = "volume stack covers: Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Export/ExportItemBuilder.cs"] = "record kept; no companions, links, volumes, completion or refresh",
        ["src/MangaPixer.Server/Features/Export/ExportRebuildService.cs"] = "items = own rows of any state",
        ["src/MangaPixer.Server/Features/Export/ExportService.cs"] = "item counts = own rows of any state",
        ["src/MangaPixer.Server/Features/Export/ExportVocabulary.cs"] = "\"CollectionAbout\"",
        ["src/MangaPixer.Server/Features/Home/RecentChaptersService.cs"] = "new works inside a collection stack on it",
        ["src/MangaPixer.Server/Features/Jobs/ScheduledJobsService.cs"] = "refresh cadence: Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Library/Moves/MoveConflictService.cs"] = "moves like an admin decision; retires nothing below",
        ["src/MangaPixer.Server/Features/Library/Moves/MovePairing.cs"] = "IsAdminDecision",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/AutoMatchWorkSelector.cs"] = "CoverBelow: a collection re-opens matching, classified as a collection",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/CoveredWorkRetirement.cs"] = "a linked series' retirement stops at a collection",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/LibraryTreeSnapshot.cs"] = "collection folders and their series titles for the detector / planner",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/LinkCoverCheck/CoverCheckService.cs"] = "Auto links only",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/MetadataAutoMatchService.cs"] = "MatchingCover in re-check / hand-over / re-run; EnqueueBelowAsync",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/MetadataCarryOverService.cs"] = "carried by the move rules; stranded like any admin row",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/MetadataOutcomeCounters.cs"] = "Auto -> collection counts as changed; review -> collection is not counted",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/MetadataRefreshService.cs"] = "Confirmed / Auto only: a collection's record is not refreshed",
        ["src/MangaPixer.Server/Features/Metadata/Collections/CollectionAboutService.cs"] = "set / clear",
        ["src/MangaPixer.Server/Features/Metadata/Flags/MetadataFlagService.cs"] = "readers cannot flag a collection; caps count Confirmed / Auto",
        ["src/MangaPixer.Server/Features/Metadata/MetadataIdentifyService.cs"] = "set with a fetched record; manual Refresh of the shown record",
        ["src/MangaPixer.Server/Features/Metadata/MetadataLinkService.cs"] = "SetCollectionAboutAsync; clear only that state; purge removes it",
        ["src/MangaPixer.Server/Features/Metadata/MetadataSettingsService.cs"] = "link counts = rows of any state",
        ["src/MangaPixer.Server/Features/Metadata/Missing/MissingConversionService.cs"] = "Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Metadata/Missing/MissingReportService.cs"] = "Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Metadata/Reach/OfficialReleasesService.cs"] = "Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Metadata/Reach/ReachCheckService.cs"] = "Auto links only",
        ["src/MangaPixer.Server/Features/Metadata/Reach/SeriesProgressLoader.cs"] = "unit folders with any own row stop the walk",
        ["src/MangaPixer.Server/Features/Metadata/Review/MetadataReviewService.cs"] = "Collections tab + count, suggestion, accept, bulk",
        ["src/MangaPixer.Server/Features/Metadata/SeriesInfoFlagService.cs"] = "(i) on the collection folder itself only",
        ["src/MangaPixer.Server/Features/Metadata/SeriesInfoResolver.cs"] = "own row: CollectionAbout info without numbers; below: stops",
        ["src/MangaPixer.Server/Features/Metadata/Volumes/VolumeCoverPass.cs"] = "Confirmed / Auto only: no companions, covers, lists or Wikipedia",
        ["src/MangaPixer.Server/Persistence/Entities.cs"] = "the column (no constraint; value 4)",
        ["src/MangaPixer.Server/Persistence/MangaPixerDbContext.cs"] = "index (LibraryId, State) - no migration",
        ["src/MangaPixer.Server/Scanning/LibraryScanCoordinator.cs"] = "rows of any state follow a cross-library move",
    };

    private static readonly Regex ReaderPattern = new(@"SeriesLinkState|NodeSeriesLinks|node_series_links|CoverLinks\.Nearest", RegexOptions.CultureInvariant);

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MangaPixer.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (MangaPixer.slnx) not found above the test output directory.");
    }

    [Fact]
    public void TheListWasReviewedForEveryState()
    {
        Assert.True(Enum.GetValues<SeriesLinkState>().Length == ReviewedForStates,
            $"SeriesLinkState has {Enum.GetValues<SeriesLinkState>().Length} values, the reader list was reviewed for {ReviewedForStates}: " +
            "decide what the new state means in SeriesLinkStates and in every file of this list, then update it.");
    }

    [Fact]
    public void EveryReaderOfTheLinkState_IsReviewed()
    {
        var root = FindRepoRoot();
        var found = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/Migrations/", StringComparison.Ordinal) && !f.Contains("/obj/", StringComparison.Ordinal)
                && !f.Contains("/bin/", StringComparison.Ordinal))
            .Where(f => ReaderPattern.IsMatch(File.ReadAllText(Path.Combine(root, f))))
            .Order(StringComparer.Ordinal)
            .ToList();

        var unreviewed = found.Except(Readers.Keys).ToList();
        var gone = Readers.Keys.Except(found).ToList();
        Assert.True(unreviewed.Count == 0, "New readers of the series link state - decide what every state means there and list them: "
            + string.Join(", ", unreviewed));
        Assert.True(gone.Count == 0, "Listed readers that no longer read the state - remove them: " + string.Join(", ", gone));
    }
}
