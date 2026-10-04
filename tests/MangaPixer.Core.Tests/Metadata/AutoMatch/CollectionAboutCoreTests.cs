namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// 1.34.0 "Collection about" (a folder of works about one series): what every link state means
/// (<see cref="SeriesLinkStates"/>), the nearest-wins matching rule (<see cref="MatchingCover"/>), the detector's collection rule, the
/// planner's parody from the collection's series and the review suggestion (<see cref="CollectionSignal"/>). Synthetic names only.
/// </summary>
public sealed class CollectionAboutCoreTests
{
    // --- SeriesLinkStates: every value has an answer in every predicate (a new state fails here until it is decided) ---

    [Fact]
    public void EveryLinkState_AnswersEveryPredicate()
    {
        foreach (var state in Enum.GetValues<SeriesLinkState>())
        {
            _ = SeriesLinkStates.IsSeries(state);
            _ = SeriesLinkStates.StopsInheritance(state);
            _ = SeriesLinkStates.ShowsRecordOnSelf(state);
            _ = SeriesLinkStates.IsAdminDecision(state);
            _ = SeriesLinkStates.CoverBelow(state);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => SeriesLinkStates.IsSeries((SeriesLinkState)99));
    }

    [Fact]
    public void CollectionAbout_IsStoredAsFour_AndIsALabelNotASeries()
    {
        Assert.Equal(4, (int)SeriesLinkState.CollectionAbout);
        Assert.Equal(6, (int)SeriesInfoState.CollectionAbout);
        Assert.False(SeriesLinkStates.IsSeries(SeriesLinkState.CollectionAbout));
        Assert.True(SeriesLinkStates.StopsInheritance(SeriesLinkState.CollectionAbout));
        Assert.True(SeriesLinkStates.ShowsRecordOnSelf(SeriesLinkState.CollectionAbout));
        Assert.True(SeriesLinkStates.IsAdminDecision(SeriesLinkState.CollectionAbout));
        Assert.Equal(MatchingCoverKind.Opens, SeriesLinkStates.CoverBelow(SeriesLinkState.CollectionAbout));
    }

    [Theory]
    [InlineData(SeriesLinkState.Confirmed, true, true, true, true, MatchingCoverKind.Covers)]
    [InlineData(SeriesLinkState.Auto, true, true, true, false, MatchingCoverKind.Covers)]
    [InlineData(SeriesLinkState.NeedsReview, false, false, false, false, MatchingCoverKind.Blocks)]
    [InlineData(SeriesLinkState.DontMatch, false, true, false, true, MatchingCoverKind.Blocks)]
    public void ExistingStates_KeepTheirMeaning(SeriesLinkState state, bool series, bool stops, bool shows, bool admin, MatchingCoverKind cover)
    {
        Assert.Equal(series, SeriesLinkStates.IsSeries(state));
        Assert.Equal(stops, SeriesLinkStates.StopsInheritance(state));
        Assert.Equal(shows, SeriesLinkStates.ShowsRecordOnSelf(state));
        Assert.Equal(admin, SeriesLinkStates.IsAdminDecision(state));
        Assert.Equal(cover, SeriesLinkStates.CoverBelow(state));
    }

    // --- MatchingCover: the owner's nearest rule, with Don't match / Needs review above blocking everything ---

    [Fact]
    public void NoRowAbove_IsOpen() => Assert.False(MatchingCover.IsCovered([]));

    [Fact]
    public void LinkedSeriesAbove_Covers() => Assert.True(MatchingCover.IsCovered([SeriesLinkState.Confirmed]));

    [Fact]
    public void CollectionNearest_ReopensBelowALinkedSeries()
    {
        Assert.False(MatchingCover.IsCovered([SeriesLinkState.CollectionAbout]));
        Assert.False(MatchingCover.IsCovered([SeriesLinkState.CollectionAbout, SeriesLinkState.Confirmed]));
        Assert.False(MatchingCover.IsCovered([SeriesLinkState.CollectionAbout, SeriesLinkState.Auto]));
    }

    [Fact]
    public void LinkedSeriesNearest_CoversEvenBelowACollection() =>
        Assert.True(MatchingCover.IsCovered([SeriesLinkState.Auto, SeriesLinkState.CollectionAbout]));

    [Fact]
    public void DontMatchAnywhereAbove_BlocksACollection() =>
        Assert.True(MatchingCover.IsCovered([SeriesLinkState.CollectionAbout, SeriesLinkState.Confirmed, SeriesLinkState.DontMatch]));

    [Fact]
    public void WaitingFolderAnywhereAbove_BlocksACollection() =>
        Assert.True(MatchingCover.IsCovered([SeriesLinkState.CollectionAbout, SeriesLinkState.NeedsReview]));

    // --- The detector's collection rule ---

    private readonly WorkDetector _detector = new();
    private readonly MatchQueryPlanner _planner = new();

    private static readonly string[] Doujins =
    [
        "(Event 1) [Circle One (Artist A)] Moonlit Promise (Starlight Academy) [English].cbz",
        "[Circle Two (Artist B)] Summer Lesson [English].cbz",
        "Circle Three] Rainy Day Story.cbz",
        "[Artist C] After School 1.cbz",
        "[Artist C] After School 2.cbz",
    ];

    private static FolderShape Shape(string name, IReadOnlyList<string> archives, bool collection = false, string? series = null,
        int depth = 2, (string Name, int Count)[]? subs = null) =>
        new(name, depth, archives, (subs ?? []).Select(s => new ChildFolderShape(s.Name, s.Count)).ToList(), "Doujin", null, null,
            collection, series);

    [Fact]
    public void CollectionFolder_IsAnArchiveLevelCollection_WhateverItsShape()
    {
        // Unmarked, a folder whose archives share the series' name is one series (folder level).
        var named = new[] { "[Circle One] Starlight Academy.cbz", "[Circle Two] Starlight Academy Extra.cbz", "[Circle Three] Starlight Academy.cbz" };
        Assert.Equal(MatchLevel.Folder, _detector.Classify(Shape("Starlight Academy", named)).Level);
        var marked = _detector.Classify(Shape("Starlight Academy", named, collection: true, series: "Starlight Academy"));
        Assert.Equal((WorkClass.CollectionLeaf, MatchLevel.Archive), (marked.Class, marked.Level));

        var c = _detector.Classify(Shape("Starlight Academy", Doujins, collection: true, series: "Starlight Academy"));
        Assert.Equal(WorkClass.CollectionLeaf, c.Class);
        Assert.Equal(MatchLevel.Archive, c.Level);
        // The numbered pair stays one work; every other archive is its own.
        Assert.Equal(4, c.ArchiveGroups.Count);
        Assert.Contains(c.ArchiveGroups, g => g.ArchiveIndexes.SequenceEqual([3, 4]));
        Assert.All(c.Reasons, r => Assert.DoesNotContain("Starlight", r, StringComparison.Ordinal));
    }

    [Fact]
    public void CollectionFolder_WithOnlySubfolders_QueuesNothingItself()
    {
        var c = _detector.Classify(Shape("Starlight Academy", [], collection: true, subs: [("Circle One", 3), ("Circle Two", 2)]));
        Assert.Equal(MatchLevel.None, c.Level);
        Assert.Empty(c.ArchiveGroups);
    }

    // --- The planner: the collection's series is the parody, searched second ---

    [Fact]
    public void ArchiveInACollection_SearchesTheSeriesDjForm_Second()
    {
        var folder = Shape("Starlight Academy", Doujins, collection: true, series: "Starlight Academy");
        var c = _detector.Classify(folder);
        var summer = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups.Single(g => g.ArchiveIndexes.SequenceEqual([1])));

        Assert.Equal(QueryVariantKind.Primary, summer.Variants[0].Kind);
        var parody = summer.Variants.Single(v => v.Kind == QueryVariantKind.DoujinParodyForm);
        Assert.Equal("Starlight Academy dj - Summer Lesson", parody.Text);
        // Within the first searches (the lookup sends at most four), before every bracket / split variant.
        Assert.True(summer.Variants.ToList().IndexOf(parody) <= 2);
    }

    [Fact]
    public void ArchiveWithItsOwnParodyTag_KeepsIt()
    {
        var folder = Shape("Starlight Academy", Doujins, collection: true, series: "Starlight Academy");
        var c = _detector.Classify(folder);
        var moon = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups.Single(g => g.ArchiveIndexes.SequenceEqual([0])));

        Assert.Equal("Starlight Academy dj - Moonlit Promise", moon.Variants.Single(v => v.Kind == QueryVariantKind.DoujinParodyForm).Text);
    }

    [Fact]
    public void MiniSeriesInACollection_UsesTheGroupTitle()
    {
        var folder = Shape("Starlight Academy", Doujins, collection: true, series: "Starlight Academy");
        var c = _detector.Classify(folder);
        var group = c.ArchiveGroups.Single(g => g.ArchiveIndexes.SequenceEqual([3, 4]));
        var plan = _planner.PlanArchiveGroup(folder, c, group);

        Assert.Contains(plan.Variants, v => v.Kind == QueryVariantKind.DoujinParodyForm && v.Text == $"Starlight Academy dj - {TitleNormalizer.Normalize(group.QueryTitle).Primary}");
    }

    [Fact]
    public void FolderWorkBelowACollection_SearchesTheSeriesDjForm()
    {
        var sub = Shape("Long Holiday", ["Long Holiday v01.cbz", "Long Holiday v02.cbz"], series: "Starlight Academy", depth: 3);
        var c = _detector.Classify(sub);
        var plan = _planner.PlanFolder(sub, c);

        Assert.Equal("Long Holiday", plan.Variants[0].Text);
        Assert.Contains(plan.Variants.Take(3), v => v.Kind == QueryVariantKind.DoujinParodyForm && v.Text == "Starlight Academy dj - Long Holiday");
    }

    [Fact]
    public void WithoutACollection_NoSeriesIsAdded()
    {
        var folder = Shape("Mixed Doujin", Doujins);
        var c = _detector.Classify(folder with { IsCollection = true });
        var summer = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups.Single(g => g.ArchiveIndexes.SequenceEqual([1])));

        Assert.DoesNotContain(summer.Variants, v => v.Kind == QueryVariantKind.DoujinParodyForm);
    }

    // --- The review suggestion ---

    private static readonly CollectionCandidate Series = new(1, MetadataFormat.Comic, 1.0);

    [Fact]
    public void DoujinFolderNamedAfterASeries_IsSuggested()
    {
        var pick = CollectionSignal.Suggest(Doujins, [Series, new CollectionCandidate(2, MetadataFormat.Comic, 0.7)]);
        Assert.Equal(1, pick?.Rank);
    }

    [Fact]
    public void DoujinshiCandidates_AreNeverTheSeries()
    {
        var dj = new CollectionCandidate(1, MetadataFormat.Doujinshi, 1.0);
        var series = new CollectionCandidate(2, MetadataFormat.Comic, 0.9);
        Assert.Equal(2, CollectionSignal.Suggest(Doujins, [dj, series])?.Rank);
        Assert.Null(CollectionSignal.Suggest(Doujins, [dj]));
    }

    [Fact]
    public void WeakTitle_IsNoSuggestion() =>
        Assert.Null(CollectionSignal.Suggest(Doujins, [new CollectionCandidate(1, MetadataFormat.Comic, 0.8)]));

    [Fact]
    public void FewArchives_IsNoSuggestion() =>
        Assert.Null(CollectionSignal.Suggest(Doujins.Take(2).ToList(), [Series]));

    [Fact]
    public void UntaggedSeriesFolder_IsNoSuggestion() =>
        Assert.Null(CollectionSignal.Suggest(["Starlight Academy v01.cbz", "Starlight Academy v02.cbz", "Starlight Academy v03.cbz"], [Series]));

    [Fact]
    public void ScanlationGroupChapters_AreNoSuggestion() =>
        Assert.Null(CollectionSignal.Suggest(
            ["[Group A] Starlight Academy c001.cbz", "[Group A] Starlight Academy c002.cbz", "[Group B] Starlight Academy c003.cbz"], [Series]));

    [Fact]
    public void OneCirclesReleases_AreNoSuggestion() =>
        Assert.Null(CollectionSignal.Suggest(
            ["[Circle One] First Story.cbz", "[Circle One] Second Story.cbz", "[Circle One] Third Story.cbz"], [Series]));

    [Fact]
    public void FewTagged_IsNoSuggestion() =>
        Assert.Null(CollectionSignal.Suggest(
            ["[Circle One] First Story.cbz", "[Circle Two] Second Story.cbz", "Third Story.cbz", "Fourth Story.cbz"], [Series]));
}
