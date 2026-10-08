namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// 1.37.0 "Artist folder" (a folder an admin marked as one artist's works): what the new link state means
/// (<see cref="SeriesLinkStates"/>), the nearest-wins matching rule with it, the detector's marked-folder rule (whatever the folder's
/// shape; every unmarked folder unchanged) and the planner's author tags (the declared artists, never the folder name). Synthetic names.
/// </summary>
public sealed class ArtistFolderCoreTests
{
    private readonly WorkDetector _detector = new();
    private readonly MatchQueryPlanner _planner = new();

    // --- SeriesLinkStates ---

    [Fact]
    public void ArtistFolder_IsStoredAsFive_AndIsNotASeries_ButOpensMatchingBelow()
    {
        Assert.Equal(5, (int)SeriesLinkState.ArtistFolder);
        Assert.Equal(7, (int)SeriesInfoState.ArtistFolder);
        Assert.False(SeriesLinkStates.IsSeries(SeriesLinkState.ArtistFolder));
        Assert.True(SeriesLinkStates.StopsInheritance(SeriesLinkState.ArtistFolder));
        Assert.False(SeriesLinkStates.ShowsRecordOnSelf(SeriesLinkState.ArtistFolder));
        Assert.True(SeriesLinkStates.IsAdminDecision(SeriesLinkState.ArtistFolder));
        Assert.Equal(MatchingCoverKind.Opens, SeriesLinkStates.CoverBelow(SeriesLinkState.ArtistFolder));
    }

    [Fact]
    public void ArtistFolderNearest_ReopensBelowALinkedSeries_ButNotBelowADontMatchOrAWaitingFolder()
    {
        Assert.False(MatchingCover.IsCovered([SeriesLinkState.ArtistFolder]));
        Assert.False(MatchingCover.IsCovered([SeriesLinkState.ArtistFolder, SeriesLinkState.Confirmed]));
        Assert.True(MatchingCover.IsCovered([SeriesLinkState.Auto, SeriesLinkState.ArtistFolder]));
        Assert.True(MatchingCover.IsCovered([SeriesLinkState.ArtistFolder, SeriesLinkState.DontMatch]));
        Assert.True(MatchingCover.IsCovered([SeriesLinkState.ArtistFolder, SeriesLinkState.NeedsReview]));
    }

    // --- The detector ---

    private static FolderShape Shape(string name, IReadOnlyList<string> archives, IReadOnlyList<string>? artists = null, int depth = 2,
        (string Name, int Count)[]? subs = null) =>
        new(name, depth, archives, (subs ?? []).Select(s => new ChildFolderShape(s.Name, s.Count)).ToList(), "Shelf", null, null,
            ArtistNames: artists);

    private static readonly string[] OneSeriesShape = ["Qzv Harbor Tale v01.cbz", "Qzv Harbor Tale v02.cbz", "Qzv Harbor Tale v03.cbz"];

    private static readonly string[] Works =
    [
        "Qzv Harbor Tale.cbz",
        "Qzv Lantern Road 1.cbz",
        "Qzv Lantern Road 2.cbz",
        "Qzv Quiet Orchard.cbz",
    ];

    [Fact]
    public void MarkedFolder_IsAnArchiveLevelArtistCollection_WhateverItsShape()
    {
        // Unmarked, three volumes of one title are one series (folder level) and two different titles are review-only.
        Assert.Equal((WorkClass.Series, MatchLevel.Folder), Of(_detector.Classify(Shape("Beta Painter", OneSeriesShape))));
        Assert.Equal(MatchLevel.ReviewOnly, _detector.Classify(Shape("Beta Painter", ["Qzv Harbor Tale.cbz", "Qzv Quiet Orchard.cbz"])).Level);

        var marked = _detector.Classify(Shape("Beta Painter", OneSeriesShape, artists: ["Beta Painter"]));
        Assert.Equal((WorkClass.ArtistCollection, MatchLevel.Archive), Of(marked));
        var works = _detector.Classify(Shape("Beta Painter", Works, artists: ["Beta Painter"]));
        Assert.Equal((WorkClass.ArtistCollection, MatchLevel.Archive), Of(works));
        // The numbered pair stays one work; every other archive is its own.
        Assert.Equal(3, works.ArchiveGroups.Count);
        Assert.Contains(works.ArchiveGroups, g => g.ArchiveIndexes.SequenceEqual([1, 2]));
        Assert.All(works.Reasons, r => Assert.DoesNotContain("Painter", r, StringComparison.Ordinal));
    }

    [Fact]
    public void MarkedFolder_WithoutDeclaredArtists_IsStillMarked()
    {
        var c = _detector.Classify(Shape("Beta Painter", Works, artists: []));
        Assert.Equal((WorkClass.ArtistCollection, MatchLevel.Archive), Of(c));
    }

    [Fact]
    public void MarkedFolder_WithOnlySubfolders_QueuesNothingItself_TheSubfoldersAreClassifiedOnTheirOwn()
    {
        var c = _detector.Classify(Shape("Beta Painter", [], artists: ["Beta Painter"], subs: [("Qzv Harbor Tale", 3), ("Qzv Quiet Orchard", 2)]));
        Assert.Equal((WorkClass.ArtistCollection, MatchLevel.None), Of(c));
        Assert.Empty(c.ArchiveGroups);
    }

    [Fact]
    public void MarkedUnitNamedFolder_IsStillAnArtistFolder()
    {
        // An admin decision wins over the name rules: a folder named like a unit subfolder is matched archive by archive when marked.
        Assert.Equal(WorkClass.UnitSub, _detector.Classify(Shape("Volume 2", Works)).Class);
        Assert.Equal((WorkClass.ArtistCollection, MatchLevel.Archive), Of(_detector.Classify(Shape("Volume 2", Works, artists: ["Beta Painter"]))));
    }

    private static (WorkClass, MatchLevel) Of(WorkClassification c) => (c.Class, c.Level);

    // --- The planner: the declared artists are the author tags of a marked folder ---

    [Fact]
    public void ArchiveInAMarkedFolder_UsesTheDeclaredArtistsAsAuthorTags_NotTheFolderName()
    {
        var folder = Shape("Downloads from the con", Works, artists: ["Beta Painter", "Gamma Inker"]);
        var c = _detector.Classify(folder);
        var plan = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups[0]);

        var tags = plan.Context.AuthorTags;
        Assert.Contains("Beta Painter", tags);
        Assert.Contains("Gamma Inker", tags);
        Assert.DoesNotContain(tags, t => t.Contains("Downloads", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(WorkClass.ArtistCollection, plan.Context.Class);
    }

    [Fact]
    public void ArchiveInADetectedArtistFolder_KeepsTheFolderNameAsAuthorTag()
    {
        // The 1.28.0 rule, unchanged: a folder named like the creator tag its archives carry.
        var archives = new[] { "[Beta Painter] Qzv Harbor Tale.cbz", "[Beta Painter] Qzv Quiet Orchard.cbz", "[Beta Painter] Qzv Lantern Road.cbz" };
        var folder = Shape("Beta Painter", archives);
        var c = _detector.Classify(folder);
        Assert.Equal((WorkClass.ArtistCollection, MatchLevel.Archive), Of(c));
        var plan = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups[0]);
        Assert.Contains("Beta Painter", plan.Context.AuthorTags);
    }
}
