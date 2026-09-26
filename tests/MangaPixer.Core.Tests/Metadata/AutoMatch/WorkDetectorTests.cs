namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// Unit tests for <see cref="WorkDetector"/> (stage 2, design section 1 + decision 6): every class,
/// the unit-subfolder rules, the E6 discriminator, artist folders, mini-series grouping and the
/// Content suggestion. Synthetic names only.
/// </summary>
public sealed class WorkDetectorTests
{
    private readonly WorkDetector _detector = new();

    private static FolderShape Folder(string name, string[] archives, (string Name, int Count)[]? subs = null,
        int depth = 2, string? parent = null, string[]? knownAuthors = null) =>
        new(name, depth, archives, (subs ?? []).Select(s => new ChildFolderShape(s.Name, s.Count)).ToList(),
            parent, null, knownAuthors);

    private static string[] Numbered(string format, int count) =>
        Enumerable.Range(1, count).Select(i => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, i)).ToArray();

    [Fact]
    public void LibraryRoot_IsExcluded()
    {
        var c = _detector.Classify(Folder("Root", Numbered("Some Series v{0:00}.cbz", 3), depth: 0));

        Assert.Equal(WorkClass.Excluded, c.Class);
        Assert.Equal(MatchLevel.None, c.Level);
    }

    [Fact]
    public void EmptyFolder_IsExcluded()
    {
        Assert.Equal(WorkClass.Excluded, _detector.Classify(Folder("Nothing", [])).Class);
    }

    [Fact]
    public void TitledLeaf_IsSeries()
    {
        var c = _detector.Classify(Folder("Some Series", Numbered("Some Series v{0:00} (2019) (Digital).cbz", 6)));

        Assert.Equal(WorkClass.Series, c.Class);
        Assert.Equal(MatchLevel.Folder, c.Level);
        Assert.Empty(c.ArchiveGroups);
    }

    [Fact]
    public void UnitNamedLeaf_IsSeries()
    {
        var c = _detector.Classify(Folder("Some Series", Numbered("{0:000} [A Chapter Title {0}].cbz", 12)));

        Assert.Equal(WorkClass.Series, c.Class);
    }

    [Fact]
    public void SameBaseLeaf_UnderADifferentFolderName_IsSeries()
    {
        var c = _detector.Classify(Folder("Romaji Name", Numbered("English Release Name v{0:00}.cbz", 5)));

        Assert.Equal(WorkClass.Series, c.Class);
    }

    [Fact]
    public void OneArchive_IsOneShot()
    {
        var c = _detector.Classify(Folder("A Short Story", ["A Short Story.cbz"]));

        Assert.Equal(WorkClass.OneShot, c.Class);
        Assert.Equal(MatchLevel.Folder, c.Level);
    }

    [Fact]
    public void UnitSubfolders_MakeSeriesWithUnits()
    {
        var c = _detector.Classify(Folder("Some Series", Numbered("Some Series v{0:00}.cbz", 3),
            [("Chapters", 40), ("Extras", 2)]));

        Assert.Equal(WorkClass.SeriesWithUnits, c.Class);
        Assert.Equal(MatchLevel.Folder, c.Level);
    }

    [Theory]
    [InlineData("Volumes")]
    [InlineData("Chapters 1-50")]
    [InlineData("Season 2")]
    [InlineData("Part 3")]
    [InlineData("12")]
    [InlineData("Side Stories")]
    public void UnitNamedFolder_BelowANonRootParent_IsUnitSub(string name)
    {
        var c = _detector.Classify(Folder(name, Numbered("{0:000}.cbz", 3), depth: 3));

        Assert.Equal(WorkClass.UnitSub, c.Class);
        Assert.Equal(MatchLevel.None, c.Level);
    }

    [Fact]
    public void PartWithSubtitle_IsNotAUnit_ItIsASeparateWork()
    {
        var c = _detector.Classify(Folder("Part 3 - A Subtitle", Numbered("Part 3 - A Subtitle v{0:00}.cbz", 4), depth: 3));

        Assert.Equal(WorkClass.Series, c.Class);
    }

    [Fact]
    public void RelatedSubfolders_MakeAFranchiseContainer()
    {
        var c = _detector.Classify(Folder("Some Franchise", [],
            [("Some Franchise Part 1 - First Arc", 10), ("Some Franchise Part 2 - Second Arc", 12), ("Some Franchise Gaiden", 3)]));

        Assert.Equal(WorkClass.FranchiseContainer, c.Class);
        Assert.Equal(MatchLevel.None, c.Level);
    }

    [Fact]
    public void UnrelatedSubfolders_MakeACollectionContainer()
    {
        var c = _detector.Classify(Folder("Manga", [], [("Alpha Story", 10), ("Beta Tale", 12), ("Gamma Saga", 3)], depth: 1));

        Assert.Equal(WorkClass.CollectionContainer, c.Class);
        Assert.Equal(MatchLevel.None, c.Level);
    }

    [Fact]
    public void OneSubfolderNoArchives_IsWrapper_AndWithArchivesIsMixed()
    {
        Assert.Equal(WorkClass.Wrapper, _detector.Classify(Folder("Outer", [], [("Inner Series", 5)])).Class);

        var mixed = _detector.Classify(Folder("Outer", ["Loose One.cbz"], [("Inner Series", 5)]));
        Assert.Equal(WorkClass.Mixed, mixed.Class);
        Assert.Equal(MatchLevel.ReviewOnly, mixed.Level);
    }

    [Fact]
    public void EmptySubfolders_AreIgnored()
    {
        var c = _detector.Classify(Folder("Some Series", Numbered("Some Series v{0:00}.cbz", 3), [("Empty", 0), ("Also Empty", 0)]));

        Assert.Equal(WorkClass.Series, c.Class);
    }

    [Fact]
    public void DistinctTitles_AreACollectionLeaf_MatchedPerArchive()
    {
        var c = _detector.Classify(Folder("Anthology Shelf",
            ["Alpha Story.cbz", "Beta Tale.cbz", "Gamma Saga.cbz", "Delta Night.cbz", "Epsilon Dawn.cbz"]));

        Assert.Equal(WorkClass.CollectionLeaf, c.Class);
        Assert.Equal(MatchLevel.Archive, c.Level);
        Assert.Equal(5, c.ArchiveGroups.Count);
        Assert.All(c.ArchiveGroups, g => Assert.Single(g.ArchiveIndexes));
    }

    [Fact]
    public void NumberedMiniSeries_AreGrouped_NotMatchedArchiveByArchive()
    {
        var c = _detector.Classify(Folder("Anthology Shelf",
            ["Alpha Story 1.cbz", "Alpha Story 2.cbz", "Alpha Story 3.cbz", "Beta Tale.cbz", "Gamma Saga.cbz", "Delta Night.cbz", "Epsilon Dawn 2.cbz"]));

        Assert.Equal(WorkClass.CollectionLeaf, c.Class);
        var alpha = Assert.Single(c.ArchiveGroups, g => g.QueryTitle == "Alpha Story");
        Assert.Equal([0, 1, 2], alpha.ArchiveIndexes);
        // A lone numbered archive keeps its number in the query (it is part 2 of something).
        Assert.Contains(c.ArchiveGroups, g => g.QueryTitle == "Epsilon Dawn 2" && g.ArchiveIndexes.SequenceEqual([6]));
        Assert.Equal(5, c.ArchiveGroups.Count);
    }

    [Fact]
    public void NeitherSeriesNorCollection_IsAmbiguous_ReviewOnly()
    {
        // Half one base, half distinct titles.
        var c = _detector.Classify(Folder("Some Shelf",
            ["Alpha Story 1.cbz", "Alpha Story 2.cbz", "Alpha Story 3.cbz", "Beta Tale.cbz", "Gamma Saga.cbz", "Delta Night.cbz"]));

        Assert.Equal(WorkClass.Ambiguous, c.Class);
        Assert.Equal(MatchLevel.ReviewOnly, c.Level);
    }

    [Fact]
    public void SubtitledNamesSharingOneHead_AreAmbiguous()
    {
        var c = _detector.Classify(Folder("Some Shelf",
            ["Some Head - First Tale.cbz", "Some Head - Second Tale.cbz", "Some Head - Third Tale.cbz", "Some Head - Fourth Tale.cbz"]));

        Assert.Equal(WorkClass.Ambiguous, c.Class);
    }

    [Fact]
    public void FolderNamedLikeTheDominantArtistTag_IsAnArtistCollection()
    {
        // E6: artist folders whose archives repeat the artist read as "titled series" when bracket
        // text counts as coherence. Here every archive also carries a trailing two-word
        // [Some Artist] group, which the stage-1 normalizer would take as an English variant.
        var c = _detector.Classify(Folder("Some Artist",
        [
            "[Some Artist] Alpha Story.cbz",
            "[Some Artist] Beta Tale [Some Artist].cbz",
            "(Event 3) [Some Circle (Some Artist)] Gamma Saga (Some Parody).cbz",
            "[Some Artist] Alpha Story 2.cbz",
        ]));

        Assert.Equal(WorkClass.ArtistCollection, c.Class);
        Assert.Equal(MatchLevel.Archive, c.Level);
        Assert.Contains(c.ArchiveGroups, g => g.QueryTitle == "Alpha Story" && g.ArchiveIndexes.SequenceEqual([0, 3]));
    }

    [Fact]
    public void FolderNamedLikeAKnownProviderAuthor_IsAnArtistCollection()
    {
        var c = _detector.Classify(Folder("Given Family", ["Alpha Story.cbz", "Beta Tale.cbz"], knownAuthors: ["FAMILY Given"]));

        Assert.Equal(WorkClass.ArtistCollection, c.Class);
    }

    [Fact]
    public void CategoryWordFolder_IsNeverAnArtistFolder()
    {
        var c = _detector.Classify(Folder("Manga", ["[Manga] Alpha Story.cbz", "[Manga] Beta Tale.cbz"], knownAuthors: ["Manga"]));

        Assert.NotEqual(WorkClass.ArtistCollection, c.Class);
    }

    [Fact]
    public void SeriesNamedLikeATag_OnFewArchives_StaysSeries()
    {
        var c = _detector.Classify(Folder("Some Series",
            ["[Some Series] Extra.cbz", "Some Series v01.cbz", "Some Series v02.cbz", "Some Series v03.cbz", "Some Series v04.cbz"]));

        Assert.Equal(WorkClass.Series, c.Class);
    }

    [Fact]
    public void DoujinShapedArchives_SuggestTheContentSetting()
    {
        var c = _detector.Classify(Folder("Some Shelf",
        [
            "(Event 1) [Circle One (Artist One)] Alpha Story (Parody A).cbz",
            "(Event 2) [Circle Two (Artist Two)] Beta Tale (Parody B).cbz",
            "[Circle Three (Artist Three)] Gamma Saga (Parody C).cbz",
        ]));

        Assert.Equal(ContentSuggestion.DoujinshiAndAdultOneShots, c.ContentSuggestion);
        Assert.Equal(WorkClass.CollectionLeaf, c.Class);
    }

    [Fact]
    public void ScanlationNames_DoNotSuggestDoujinContent()
    {
        var c = _detector.Classify(Folder("Some Series", Numbered("[Scan Team] Some Series v{0:00} (Digital) (Group).cbz", 4)));

        Assert.Equal(ContentSuggestion.None, c.ContentSuggestion);
    }

    [Fact]
    public void Reasons_CarryNoNames()
    {
        var c = _detector.Classify(Folder("Secret Folder Name", ["Secret Archive One.cbz", "Hidden Archive Two.cbz", "Private Three.cbz"]));

        Assert.All(c.Reasons, r =>
        {
            Assert.DoesNotContain("Secret", r, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Hidden", r, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Private", r, StringComparison.OrdinalIgnoreCase);
        });
    }
}
