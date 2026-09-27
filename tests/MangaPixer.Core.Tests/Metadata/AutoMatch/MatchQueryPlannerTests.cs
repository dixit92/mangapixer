namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>Unit tests for <see cref="MatchQueryPlanner"/> (stage 2, design section 2). Synthetic names only.</summary>
public sealed class MatchQueryPlannerTests
{
    private readonly WorkDetector _detector = new();
    private readonly MatchQueryPlanner _planner = new();

    private static FolderShape Folder(string name, string[] archives, (string Name, int Count)[]? subs = null,
        string? parent = null, string? category = null) =>
        new(name, 2, archives, (subs ?? []).Select(s => new ChildFolderShape(s.Name, s.Count)).ToList(), parent, category);

    private MatchQuery PlanFolder(FolderShape f, string? comicInfo = null) => _planner.PlanFolder(f, _detector.Classify(f), comicInfo);

    [Fact]
    public void Variants_AreOrderedByKind_AndDeduplicated()
    {
        var q = PlanFolder(
            Folder("Some Long Series 2 - The Return [English Name Here]",
                ["English Name Here v01.cbz", "English Name Here v02.cbz", "Archive Only Name v03.cbz", "Archive Only Name v04.cbz", "Archive Only Name v05.cbz"]),
            comicInfo: "Some Long Series 2 - The Return");

        Assert.Equal(
        [
            ("Some Long Series 2 - The Return", QueryVariantKind.ComicInfoSeries),
            ("English Name Here", QueryVariantKind.EnglishTitle),
            ("Some Long Series 2", QueryVariantKind.SubtitleSplit),
            ("Some Long Series", QueryVariantKind.SequelNumberSplit),
            ("Archive Only Name", QueryVariantKind.ArchiveDerivedTitle),
        ], q.Variants.Select(v => (v.Text, v.Kind)));
    }

    [Fact]
    public void ArchiveTitle_ThatExtendsTheFolderName_IsTheSecondSearch()
    {
        // The folder is the leading words of a long title; the archives carry the whole title.
        var q = PlanFolder(Folder("Alpha to Beta Gamma [Some English Name]",
            ["Alpha to Beta Gamma Delta Epsilon Zeta v01.cbz", "Alpha to Beta Gamma Delta Epsilon Zeta v02.cbz"]));

        Assert.Equal(
        [
            ("Alpha to Beta Gamma", QueryVariantKind.Primary),
            ("Alpha to Beta Gamma Delta Epsilon Zeta", QueryVariantKind.ArchiveDerivedTitle),
            ("Some English Name", QueryVariantKind.EnglishTitle),
        ], q.Variants.Select(v => (v.Text, v.Kind)));
    }

    [Fact]
    public void Context_LocalUnits_AreTheHighestNumbers_AndUnitSubfolderCounts()
    {
        var names = Enumerable.Range(1, 6).Select(i => $"Some Series v0{i}.cbz")
            .Concat(Enumerable.Range(1, 6).Select(i => $"Some Series v0{i}.5.cbz")).ToArray();
        var q = PlanFolder(Folder("Some Series", names));
        Assert.Equal(12, q.Context.VolumeLikeCount);
        Assert.Equal(6, q.Context.LocalVolumes);
        Assert.Null(q.Context.LocalChapters);

        var units = PlanFolder(Folder("Some Series", [], subs: [("Volumes", 11), ("Chapters", 80)]));
        Assert.Equal(11, units.Context.LocalVolumes);
        Assert.Equal(80, units.Context.LocalChapters);
    }

    [Fact]
    public void Variants_DedupeIgnoresCaseAndPunctuation()
    {
        var q = PlanFolder(Folder("Re:Zero Story", ["Re Zero Story v01.cbz", "re zero story v02.cbz"]));

        Assert.Single(q.Variants);
        Assert.Equal(QueryVariantKind.Primary, q.Variants[0].Kind);
    }

    [Fact]
    public void Context_CountsVolumesAndChaptersSeparately_IncludingUnitSubfolders()
    {
        var q = PlanFolder(Folder("Some Series",
            ["Some Series v01 (2011).cbz", "Some Series v02 (2012).cbz", "Some Series Ch 015.cbz"],
            [("Chapters", 30), ("Volumes 3-5", 3), ("Extras", 2)], category: "Manga"));

        Assert.Equal(WorkClass.SeriesWithUnits, q.Context.Class);
        Assert.Equal(3 + 30 + 3 + 2, q.Context.ArchiveCount);
        Assert.Equal(2 + 3, q.Context.VolumeLikeCount);
        Assert.Equal(1 + 30, q.Context.ChapterLikeCount);
        Assert.Equal(2011, q.Context.EarliestYear);
        Assert.Equal("Manga", q.Context.CategoryHint);
    }

    [Fact]
    public void Context_UnitNamedArchives_AreChapterLike()
    {
        var q = PlanFolder(Folder("Some Series", ["001 [First].cbz", "002 [Second].cbz", "003 [Third].cbz"]));

        Assert.Equal(3, q.Context.ChapterLikeCount);
        Assert.Equal(0, q.Context.VolumeLikeCount);
    }

    [Fact]
    public void Context_AuthorLikeParent_IsATieBreakTag_CategoryParentIsNot()
    {
        Assert.Contains("Given Family", PlanFolder(Folder("Some Series", ["Some Series v01.cbz", "Some Series v02.cbz"], parent: "Given Family")).Context.AuthorTags);
        Assert.Empty(PlanFolder(Folder("Some Series", ["Some Series v01.cbz", "Some Series v02.cbz"], parent: "Manga")).Context.AuthorTags);
    }

    [Fact]
    public void ArchiveGroup_OfAnArtistCollection_CarriesTheArtistAndTheParodyForm()
    {
        var folder = Folder("Some Artist",
        [
            "(Event 1) [Some Circle (Some Artist)] Alpha Story (Parody Name).cbz",
            "[Some Artist] Beta Tale.cbz",
            "[Some Artist] Gamma Saga 1.cbz",
            "[Some Artist] Gamma Saga 2.cbz",
        ]);
        var c = _detector.Classify(folder);
        Assert.Equal(WorkClass.ArtistCollection, c.Class);

        var alpha = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups.Single(g => g.QueryTitle == "Alpha Story"));
        Assert.Equal(("Alpha Story", QueryVariantKind.Primary), (alpha.Variants[0].Text, alpha.Variants[0].Kind));
        Assert.Contains(alpha.Variants, v => v.Kind == QueryVariantKind.DoujinParodyForm && v.Text == "Parody Name dj - Alpha Story");
        Assert.Contains("Some Circle", alpha.Context.AuthorTags);
        Assert.Contains("Some Artist", alpha.Context.AuthorTags);
        Assert.Equal(1, alpha.Context.ArchiveCount);

        var gamma = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups.Single(g => g.QueryTitle == "Gamma Saga"));
        Assert.Equal(2, gamma.Context.ArchiveCount);
        Assert.Equal(["Some Artist"], gamma.Context.AuthorTags);
    }

    [Fact]
    public void OneShotFolder_AddsTheArchiveTitle()
    {
        var q = PlanFolder(Folder("Folder Label", ["[Some Artist] Actual Story Title.cbz"]));

        Assert.Equal(WorkClass.OneShot, q.Context.Class);
        Assert.Contains(q.Variants, v => v.Kind == QueryVariantKind.ArchiveDerivedTitle && v.Text == "Actual Story Title");
    }

    [Fact]
    public void FolderWithEnglishTitleAndCreator_SearchesBoth_AndCarriesTheCreatorHint()
    {
        var q = PlanFolder(Folder("Tsunagu Te [Joined Hands] (Family Given)", ["Tsunagu Te v01.cbz", "Tsunagu Te v02.cbz"]));

        Assert.Contains(q.Variants, v => v.Text == "Joined Hands" && v.Kind == QueryVariantKind.EnglishTitle);
        Assert.Contains("Family Given", q.Context.CreatorHints!);
    }

    [Fact]
    public void Plan_IsDeterministic()
    {
        var f = Folder("Some Series [English Name Here]", ["English Name Here v01.cbz", "English Name Here v02.cbz"]);

        Assert.Equal(PlanFolder(f).Variants, PlanFolder(f).Variants);
    }

    [Fact]
    public void LooseArchiveGroup_InAContainer_IsScoredAsACollectionWork()
    {
        var detector = new WorkDetector();
        var folder = new FolderShape("Manga", 1, ["Short Story.cbz"],
            [new ChildFolderShape("Alpha Story", 10), new ChildFolderShape("Beta Tale", 12)]);
        var c = detector.Classify(folder);

        var q = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups.Single());

        Assert.Equal(WorkClass.CollectionContainer, c.Class);
        Assert.Equal(WorkClass.CollectionLeaf, q.Context.Class);
    }
}
