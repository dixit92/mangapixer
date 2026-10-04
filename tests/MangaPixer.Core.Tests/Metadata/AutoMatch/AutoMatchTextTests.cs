namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>Creator hints and provider disambiguators (1.26.1). All names are synthetic.</summary>
public sealed class AutoMatchTextTests
{
    [Theory]
    [InlineData("Until We Meet [Family Given] .cbz", new[] { "Family Given" })]
    [InlineData("Hop Step! [Onlyname].cbz", new[] { "Onlyname" })]
    [InlineData("Some Words [Joined Hands] (Family Given)", new[] { "Joined Hands", "Family Given" })]
    [InlineData("Some Words [Joined Hands] (Family Given) (2019)", new[] { "Joined Hands", "Family Given" })]
    [InlineData("[Family Given] Some Words", new[] { "Family Given" })]
    [InlineData("(C99) [Circle Name (Family Given)] Some Words (Parody)", new[] { "Circle Name", "Family Given", "Parody" })]
    [InlineData("Family Given] Some Words.cbz", new[] { "Family Given" })]
    [InlineData("Some Words [Family Given.cbz", new[] { "Family Given" })]
    [InlineData("Some Words (2019)", new string[0])]
    [InlineData("Some Words (Digital)", new string[0])]
    [InlineData("Some Words (Vol. 3)", new string[0])]
    [InlineData("[Only A Tag]", new string[0])]
    [InlineData("", new string[0])]
    public void CreatorHints_AreTheBracketedAndUnmatchedNames(string name, string[] expected) =>
        Assert.Equal(expected, AutoMatchText.CreatorHints(name));

    [Theory]
    [InlineData("Sprout (FAMILY Given)", "FAMILY Given")]
    [InlineData("Sprout", null)]
    [InlineData("Sprout (2019)", null)]
    public void DisambiguatorTag_IsTheProviderAuthorSuffix(string title, string? tag) =>
        Assert.Equal(tag, AutoMatchText.DisambiguatorTag(title));

    [Theory]
    [InlineData("Some Title v03 (Digital).cbz", 3)]
    [InlineData("Some Title Vol. 01-05.cbz", 5)]
    [InlineData("Some Title v02.5.cbz", 2)]
    [InlineData("Some Title - Chapter 012.cbz", null)] // a chapter, not a volume
    [InlineData("Some Title Extra.cbz", null)]
    public void VolumeNumberOf_IsTheHighestStatedVolume(string name, int? expected) =>
        Assert.Equal(expected, AutoMatchText.VolumeNumberOf(name));

    [Theory]
    [InlineData("Some Title - Chapter 012.cbz", 12)]
    [InlineData("Some Title c045.5.cbz", 45)]
    [InlineData("Some Title Ch. 001-010.cbz", 10)]
    [InlineData("001 [Chapter Title].cbz", 1)]
    [InlineData("0150 [Chapter Title].cbz", 150)]
    [InlineData("Some Title v01.cbz", null)]
    [InlineData("Some Title 001.cbz", null)] // no chapter token: not chapter-like
    public void ChapterNumberOf_IsTheHighestStatedChapter(string name, int? expected) =>
        Assert.Equal(expected, AutoMatchText.ChapterNumberOf(name));

    // Unit numbers v2 (1.29.0): volume, volume end, chapter, chapter end ("" = null), extra.
    [Theory]
    [InlineData("Some Title v03 (Digital).cbz", "3", "", "", "", false)]
    [InlineData("Some Title c045.5.cbz", "", "", "45.5", "", true)]
    [InlineData("Some Title v02.5.cbz", "2.5", "", "", "", true)]
    [InlineData("Some Title v03 c012.cbz", "3", "", "12", "", false)]
    [InlineData("Some Title Vol.3 Ch.12.5.cbz", "3", "", "12.5", "", true)]
    [InlineData("Some Title Vol. 01-05.cbz", "1", "5", "", "", false)]
    [InlineData("Some Title v01-v05.cbz", "1", "5", "", "", false)]
    [InlineData("Some Title c010-012.cbz", "", "", "10", "12", false)]
    [InlineData("Some Title Ch. 001-010.cbz", "", "", "1", "10", false)]
    [InlineData("Some Title - Chapter 012.cbz", "", "", "12", "", false)]
    [InlineData("Some Title - Episode 7.cbz", "", "", "7", "", false)]
    // 1.31.1: "Episode N" next to a volume is a part / arc, not a chapter (each arc's volumes restart at 1).
    [InlineData("Saga of Tides - Episode 1 - The First Storm v01 (2-in-1 Edition) (2012) (Digital).cbz", "1", "", "", "", false)]
    [InlineData("Saga of Tides - Episode 3 - The Third Storm v02 (3-in-1 Edition).cbz", "2", "", "", "", false)]
    [InlineData("Some Title v02 Ep. 5.cbz", "2", "", "", "", false)]
    [InlineData("Some Title v02 Chapter 5.cbz", "2", "", "5", "", false)] // a real chapter word still counts
    [InlineData("Some Title #4.cbz", "", "", "4", "", false)]
    [InlineData("001 [Chapter Title].cbz", "", "", "1", "", false)]
    [InlineData("000.cbz", "", "", "0", "", false)]
    [InlineData("012.5 [Side Story].cbz", "", "", "12.5", "", true)]
    [InlineData("001-003 [Chapter Titles].cbz", "", "", "1", "3", false)]
    [InlineData("2019 [Chapter Title].cbz", "", "", "", "", false)] // a leading year is not a chapter
    [InlineData("Some Title v03 - 2019.cbz", "3", "", "", "", false)] // a year never ends a range
    [InlineData("Some Title 001.cbz", "", "", "", "", false)] // a title and no token: not a unit
    [InlineData("Some Title (Vol. 3).cbz", "3", "", "", "", false)] // only a bracket names the unit
    [InlineData("[Group] Some Title - c007 [v2].cbz", "", "", "7", "", false)] // [v2] is a release revision
    [InlineData("[Group] Some Title [v2].cbz", "", "", "", "", false)]
    [InlineData("Some Title Extra.cbz", "", "", "", "", false)]
    [InlineData("", "", "", "", "", false)]
    // 1.34.0: a running index, then the chapter in the first bracket.
    [InlineData("0002 [0000.5].cbz", "", "", "0.5", "", true)]
    [InlineData("0003 [0001 - Some Title].cbz", "", "", "1", "", false)]
    [InlineData("0004 [0002 - Some Title].cbz", "", "", "2", "", false)]
    [InlineData("0168 [0166 - Some Title (Part 2)].cbz", "", "", "166", "", false)] // an arc title: chapter 166 itself
    [InlineData("0001 [0000].cbz", "", "", "0", "", false)]
    [InlineData("12 [10 \u2013 Some Title] [Group].cbz", "", "", "10", "", false)]
    [InlineData("0005 [0003 - Some Title Vol. 2].cbz", "", "", "3", "", false)] // nothing is read from the title
    [InlineData("0150 [Chapter Title].cbz", "", "", "150", "", false)] // a title bracket: the leading number stays the chapter
    [InlineData("001 [2019].cbz", "", "", "1", "", false)] // a bracketed year is not a chapter
    [InlineData("001 [1 Punch].cbz", "", "", "1", "", false)] // a number + a word without " - " is a title
    public void UnitsOf_KeepsDecimalsBothNumbersAndRanges(string name, string volume, string volumeEnd, string chapter, string chapterEnd, bool extra)
    {
        static decimal? D(string s) => s.Length == 0 ? null : decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new UnitNumbers(D(volume), D(volumeEnd), D(chapter), D(chapterEnd), extra), AutoMatchText.UnitsOf(name));
    }

    [Theory]
    [InlineData("Some Title c045.5.cbz")]
    [InlineData("Some Title v03 c012.cbz")]
    [InlineData("Some Title Vol. 01-05.cbz")]
    [InlineData("001 [Chapter Title].cbz")]
    [InlineData("0003 [0001 - Some Title].cbz")]
    public void UnitsOf_LeavesTheMatcherIntegersAlone(string name)
    {
        // The matcher's helpers keep their 1.27.0 answers (the golden set depends on them).
        var units = AutoMatchText.UnitsOf(name);
        Assert.False(units.IsEmpty);
        Assert.Equal(
            (AutoMatchText.VolumeNumberOf(name), AutoMatchText.ChapterNumberOf(name)),
            name switch
            {
                "Some Title c045.5.cbz" => ((int?)null, (int?)45),
                "Some Title v03 c012.cbz" => (null, 12),
                "Some Title Vol. 01-05.cbz" => (5, null),
                // 1.34.0: the matcher keeps the running index (its count rule is unchanged: no revision bump).
                "0003 [0001 - Some Title].cbz" => (null, 3),
                _ => (null, 1),
            });
    }

    [Theory]
    [InlineData("Some Title by Family Given.cbz", new[] { "Family Given" })]
    [InlineData("Some Title - Chapter 012 | Family Given.cbz", new[] { "Family Given", "Some Title" })]
    // Either order: both name-like parts of a dash are hints. That costs nothing - a hint only counts when a
    // record's authors name it.
    [InlineData("Family Given - Some Title", new[] { "Family Given", "Some Title" })]
    [InlineData("Some Title 2 - The Return", new[] { "The Return" })]
    [InlineData("Some Title - Chapter 012", new[] { "Some Title" })]
    [InlineData("Some Title v01 - 2019", new string[0])]
    public void CreatorHints_ReadPlainSeparators_InEitherOrder(string name, string[] expected) =>
        Assert.Equal(expected, AutoMatchText.CreatorHints(name));

    [Theory]
    [InlineData("Some Title by Family Given.cbz", new[] { "Some Title" })]
    [InlineData("Some Title - Chapter 012 | Family Given.cbz", new[] { "Some Title - Chapter 012" })]
    [InlineData("Family Given - Some Title", new[] { "Some Title" })]
    [InlineData("Frieren - Beyond the End", new string[0])] // a one-word head is a title, not an author
    [InlineData("Stand by 2 Me", new string[0])] // not a name after "by"
    [InlineData("Some Title", new string[0])]
    public void CreatorSplitTitles_AreTheTitlePart(string name, string[] expected) =>
        Assert.Equal(expected, AutoMatchText.CreatorSplitTitles(name));

    [Theory]
    [InlineData("HATA Kenjiro", true)]
    [InlineData("JO Yongseok", true)]
    [InlineData("Webtoon", false)]
    [InlineData("Pre-serialization", false)]
    [InlineData("Some Studio", false)]
    [InlineData(null, false)]
    public void IsPersonTag_IsAMangaUpdatesStyleName(string? tag, bool expected) =>
        Assert.Equal(expected, AutoMatchText.IsPersonTag(tag));

    [Fact]
    public void DisambiguatedAliases_CountInFull_OnlyWhenTheTagNamesTheRecordsAuthor()
    {
        var aliases = AutoMatchText.DisambiguatedAliases(["Moon Letter (SATO Hana)", "Other Name (KATO Ken)", "No Tag", null], ["SATO Hana"]);
        Assert.Equal(new[] { ("Moon Letter", 1.0), ("Other Name", AutoMatchText.DisambiguatedAliasFactor) }, aliases);

        // The Identify dialog's display score (1.29.0): the same rule; a search hit knows no authors yet.
        Assert.Equal(1.0, AutoMatchText.BestTitleScore(["Moon Letter"], "Tsuki no Tegami", ["Moon Letter (SATO Hana)"], ["SATO Hana"]), 3);
        Assert.Equal(AutoMatchText.DisambiguatedAliasFactor, AutoMatchText.BestTitleScore(["Moon Letter"], "Tsuki no Tegami", ["Moon Letter (SATO Hana)"]), 3);
        Assert.Equal(1.0, AutoMatchText.BestTitleScore(["Look Up"], "Look Up (SATO Hana)", []), 3); // the main title strips in full
    }
}
