namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// Unit tests for <see cref="AutoMatchText.OriginsForCategory"/> (1.32.0 fix: the words are compared in scoring form, so a
/// Webtoon(s) folder - "webton" once long vowels fold - gives its origin; before 1.32.0 it never did).
/// </summary>
public sealed class CategoryOriginsTests
{
    [Theory]
    [InlineData("Webtoon", new[] { MetadataOrigin.Korea, MetadataOrigin.ChinaTaiwan })]
    [InlineData("WEBTOONS", new[] { MetadataOrigin.Korea, MetadataOrigin.ChinaTaiwan })]
    [InlineData("Manga", new[] { MetadataOrigin.Japan })]
    [InlineData("Korean Manhwa", new[] { MetadataOrigin.Korea })]
    [InlineData("Bandes dessinées", new[] { MetadataOrigin.French })]
    [InlineData("Fumetti", new[] { MetadataOrigin.Italian })]
    [InlineData("Stripboeken", new[] { MetadataOrigin.Dutch })]
    [InlineData("US Comics", new[] { MetadataOrigin.EnglishOriginal })]
    public void CategoryWords_GiveTheirOrigins(string hint, MetadataOrigin[] expected)
    {
        Assert.Equal(expected.ToHashSet(), AutoMatchText.OriginsForCategory(hint)!.ToHashSet());
    }

    [Theory]
    [InlineData("Comics")]
    [InlineData("Graphic Novels")]
    [InlineData("Ongoing")]
    [InlineData(null)]
    public void WordsWithoutAnOrigin_GiveNone(string? hint) => Assert.Null(AutoMatchText.OriginsForCategory(hint));
}
