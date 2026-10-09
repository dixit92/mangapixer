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
    private const int ReviewedForStates = 6;

    /// <summary>
    /// Every reader, with what the newest state, <see cref="SeriesLinkState.ArtistFolder"/> (1.37.0: an artist's folder - no record,
    /// stops inheritance, re-opens matching below), means there. (<see cref="SeriesLinkState.CollectionAbout"/>'s answers: git history.)
    /// </summary>
    private static readonly Dictionary<string, string> Readers = new(StringComparer.Ordinal)
    {
        ["src/MangaPixer.Core/Api/FolderMatchDtos.cs"] = "1.38.0: a preview row shows the folder's own state (any value; Confirmed / Auto / Don't match / collection / artist folder = decided, unticked)",
        ["src/MangaPixer.Core/Api/MetadataAutoMatchDtos.cs"] = "review DTOs carry the state; MarkArtistFolder bulk; ArtistFolders count; the row's Artist",
        ["src/MangaPixer.Core/Api/MetadataDtos.cs"] = "link DTOs carry the state (record fields null)",
        ["src/MangaPixer.Core/Api/MetadataMissingDtos.cs"] = "Missing rows are Confirmed / Auto only: never an artist folder",
        ["src/MangaPixer.Core/Api/MoveConflictDtos.cs"] = "a conflict side may be an artist folder (web label)",
        ["src/MangaPixer.Core/Api/OfficialReleasesDtos.cs"] = "Official releases rows are Confirmed / Auto only",
        ["src/MangaPixer.Core/Metadata/MetadataVocabulary.cs"] = "the enum (value 5) and SeriesInfoState.ArtistFolder (7)",
        ["src/MangaPixer.Core/Metadata/SeriesLinkStates.cs"] = "THE decisions: not a series, stops, shows no record, admin decision, Opens",
        ["src/MangaPixer.Server/Features/Catalog/CatalogBrowseService.cs"] = "alt-title search joins on a record: an artist folder has none",
        ["src/MangaPixer.Server/Features/Catalog/VolumeEntryService.cs"] = "Volumes view: Confirmed / Auto only - an artist folder has no series; a unit walk stops at it",
        ["src/MangaPixer.Server/Features/Covers/CoverDecisionHostedService.cs"] = "any non-review row re-decides its subtree's covers (marking one too)",
        ["src/MangaPixer.Server/Features/Covers/CoverDecisionService.cs"] = "no record, not linked: the folder and everything below follow the unlinked rules",
        ["src/MangaPixer.Server/Features/Covers/CoverLinks.cs"] = "nearest-row walk stops at it; IsLinked / IsOwnCollection false (no record)",
        ["src/MangaPixer.Server/Features/Covers/CoverPickerService.cs"] = "not linked: the covers of the series linked below it (1.36.0 rule), else not_linked",
        ["src/MangaPixer.Server/Features/Covers/CoverResolutionService.cs"] = "no poster layer (no record); series covers Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Covers/StackCoverService.cs"] = "volume stack covers: Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Export/ExportItemBuilder.cs"] = "an item with record null; no companions, links, volumes, completion or refresh",
        ["src/MangaPixer.Server/Features/Export/ExportRebuildService.cs"] = "items = own rows of any state",
        ["src/MangaPixer.Server/Features/Export/ExportService.cs"] = "item counts = own rows of any state",
        ["src/MangaPixer.Server/Features/Export/ExportVocabulary.cs"] = "\"ArtistFolder\"",
        ["src/MangaPixer.Server/Features/Home/RecentChaptersService.cs"] = "stacks need a record: new works stack on the folder holding them (the artist folder for loose works)",
        ["src/MangaPixer.Server/Features/Jobs/ScheduledJobsService.cs"] = "refresh cadence: Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Library/Moves/MoveConflictService.cs"] = "moves like an admin decision; retires nothing below",
        ["src/MangaPixer.Server/Features/Library/Moves/MovePairing.cs"] = "IsAdminDecision: kept on a move; another admin row on the other side is a conflict",
        ["src/MangaPixer.Server/Features/Metadata/Artists/ArtistFolderService.cs"] = "set (declares the artist, queues the works) / clear only this state",
        ["src/MangaPixer.Server/Features/Metadata/FolderMatch/FolderMatchService.cs"] = "1.38.0: reads the own row (Needs review is not a decision; the other five are) and Confirmed / Auto records to rank; writes only through the single actions",
        ["src/MangaPixer.Server/Features/Metadata/Authors/AuthorAliasLookupService.cs"] = "Confirmed / Auto / Collection about only (a record's creators): never an artist folder (no record)",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/AutoMatchWorkSelector.cs"] = "CoverBelow Opens: classified (the detector makes it an artist collection) and walked",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/CoveredWorkRetirement.cs"] = "a linked series' retirement stops at an artist folder (OpensMatching)",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/LibraryTreeSnapshot.cs"] = "artist folders with their declared artists for the detector / planner",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/LinkCoverCheck/CoverCheckService.cs"] = "Auto links only",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/MetadataAutoMatchService.cs"] = "MatchingCover (Opens) in re-check / hand-over / re-run; EnqueueBelowAsync re-queues finished works",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/MetadataCarryOverService.cs"] = "carried by the move rules; stranded like any admin row",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/MetadataOutcomeCounters.cs"] = "Auto -> artist folder counts as changed; review -> artist folder is not counted",
        ["src/MangaPixer.Server/Features/Metadata/AutoMatch/MetadataRefreshService.cs"] = "Confirmed / Auto only: there is no record to refresh",
        ["src/MangaPixer.Server/Features/Metadata/Collections/CollectionAboutService.cs"] = "clears only a Collection about row (an artist row is left alone)",
        ["src/MangaPixer.Server/Features/Metadata/Flags/MetadataFlagService.cs"] = "readers cannot flag it (no web data); caps count Confirmed / Auto",
        ["src/MangaPixer.Server/Features/Metadata/MetadataIdentifyService.cs"] = "Identify links over it (the row is replaced); no record to refresh",
        ["src/MangaPixer.Server/Features/Metadata/MetadataLinkService.cs"] = "SetArtistFolderAsync; clear only that state; purge KEEPS it (an admin decision, no record)",
        ["src/MangaPixer.Server/Features/Metadata/MetadataSettingsService.cs"] = "link counts = rows of any state",
        ["src/MangaPixer.Server/Features/Metadata/Missing/MissingConversionService.cs"] = "Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Metadata/Missing/MissingReportService.cs"] = "Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Metadata/Reach/OfficialReleasesService.cs"] = "Confirmed / Auto only",
        ["src/MangaPixer.Server/Features/Metadata/Reach/ReachCheckService.cs"] = "Auto links only",
        ["src/MangaPixer.Server/Features/Metadata/Reach/SeriesProgressLoader.cs"] = "unit folders with any own row stop the walk",
        ["src/MangaPixer.Server/Features/Metadata/Review/MetadataReviewService.cs"] = "Collections tab lists it (+ count), accept-artist, MarkArtistFolder bulk",
        ["src/MangaPixer.Server/Features/Metadata/SeriesInfoFlagService.cs"] = "(i) on the artist folder itself only (no record needed); archives below: none",
        ["src/MangaPixer.Server/Features/Metadata/SeriesInfoResolver.cs"] = "own row: ArtistFolder info (folder name, no ComicInfo merge); below: stops",
        ["src/MangaPixer.Server/Features/Metadata/Volumes/VolumeCoverPass.cs"] = "Confirmed / Auto only: no companions, covers, lists or Wikipedia",
        ["src/MangaPixer.Server/Persistence/Entities.cs"] = "the column (no constraint; value 5, RecordId null)",
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
