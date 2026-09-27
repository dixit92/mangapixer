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
}
