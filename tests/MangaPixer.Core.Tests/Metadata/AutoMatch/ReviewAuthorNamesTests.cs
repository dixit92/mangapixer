namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>Unit tests for <see cref="ReviewAuthorNames"/> (the "Same author" hint, 1.33.0). Synthetic names only.</summary>
public sealed class ReviewAuthorNamesTests
{
    private static string[] Labels(string name) => ReviewAuthorNames.FromWorkName(name).Select(n => n.Label).ToArray();

    [Theory]
    [InlineData("[Some Circle (Some Artist)] A Short Story (Some Parody) [English].zip", new[] { "Some Circle", "Some Artist" })]
    [InlineData("(Event 12) [Some Circle (Some Artist)] A Short Story.cbz", new[] { "Some Circle", "Some Artist" })]
    [InlineData("[Lone Artist] Another Story.cbz", new[] { "Lone Artist" })]
    [InlineData("[Two Names, Other Name] Joint Story.cbz", new[] { "Two Names", "Other Name" })]
    [InlineData("[Pair A & Pair B] Joint Story.cbz", new[] { "Pair A", "Pair B" })]
    public void BalancedLeadingTag_GivesCircleAndArtist(string name, string[] expected) => Assert.Equal(expected, Labels(name));

    [Theory]
    [InlineData("Lone Artist] Another Story.cbz", new[] { "Lone Artist" })]
    [InlineData("Some Circle (Some Artist)] A Short Story.cbz", new[] { "Some Circle", "Some Artist" })]
    [InlineData("Some Circle (Some Artist] A Short Story.cbz", new[] { "Some Circle", "Some Artist" })]
    [InlineData("(Event 12) Lone Artist] Another Story.cbz", new[] { "Lone Artist" })]
    public void UnbalancedLeadingTag_TheOpeningBracketIsMissing(string name, string[] expected) => Assert.Equal(expected, Labels(name));

    [Theory]
    [InlineData("A Plain Series v01.cbz")]
    [InlineData("[English] A Translated Story.cbz")]
    [InlineData("[Digital] Some Story.cbz")]
    [InlineData("Decensored] Some Story.cbz")]
    [InlineData("[2019] Some Story.cbz")]
    [InlineData("Name]")] // no title after the tag
    [InlineData("A Story (Some Parody).cbz")] // a parody group is not an author
    [InlineData("")]
    [InlineData(null)]
    public void NoAuthorName(string? name) => Assert.Empty(ReviewAuthorNames.FromWorkName(name));

    [Fact]
    public void Keys_IgnoreCase_WordOrder_Spaces_AndFullWidthForms()
    {
        // Word order: the order key; spaces: the spaceless key; case and full-width forms: both.
        Assert.Equal(ReviewAuthorNames.OrderKey("Family Given"), ReviewAuthorNames.OrderKey("given FAMILY"));
        Assert.NotEqual(ReviewAuthorNames.Key("Family Given"), ReviewAuthorNames.Key("given FAMILY"));
        Assert.Equal(ReviewAuthorNames.Key("Shin Jinrui"), ReviewAuthorNames.Key("ShinJinrui"));
        Assert.Equal(ReviewAuthorNames.Key("Circle"), ReviewAuthorNames.Key("Ｃｉｒｃｌｅ"));
        Assert.NotEqual(ReviewAuthorNames.OrderKey("Family Given"), ReviewAuthorNames.OrderKey("Family Other"));
        Assert.Equal(string.Empty, ReviewAuthorNames.Key("x"));

        // The same author from a balanced and an unbalanced name, in the other word order.
        var balanced = ReviewAuthorNames.FromWorkName("[Some Circle (Given Family)] Story One.cbz").Select(n => n.OrderKey).ToList();
        var unbalanced = ReviewAuthorNames.FromWorkName("Family Given] Story Two.cbz").Single();
        Assert.Contains(unbalanced.OrderKey, balanced);
    }

    [Fact]
    public void PlainNames_ForArtistFoldersAndComicInfo()
    {
        Assert.Equal("Some Artist", ReviewAuthorNames.FromPlainName("Some Artist").Single().Label);
        Assert.Equal(["Writer One", "Writer Two"], ReviewAuthorNames.FromPlainName("Writer One, Writer Two").Select(n => n.Label));
        Assert.Empty(ReviewAuthorNames.FromPlainName("English"));
        Assert.Empty(ReviewAuthorNames.FromPlainName("  "));
    }
}
