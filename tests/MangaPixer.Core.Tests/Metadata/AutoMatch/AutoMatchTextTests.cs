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
}
