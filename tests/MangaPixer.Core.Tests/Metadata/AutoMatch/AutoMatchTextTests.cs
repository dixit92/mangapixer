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
}
