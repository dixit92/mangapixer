namespace com.lifepixer.mangapixer.Tests.Core.Metadata;

using com.lifepixer.mangapixer.Core.Metadata;
using Xunit;

/// <summary>
/// Unit tests for <see cref="TitleNormalizer"/> (1.24.0): display name to query
/// variants + hints. Synthetic names only.
/// </summary>
public sealed class TitleNormalizerTests
{
    [Theory]
    [InlineData("Berserk", "Berserk")]
    [InlineData("Berserk v01.cbz", "Berserk")]
    [InlineData("Berserk Vol. 3", "Berserk")]
    [InlineData("Berserk Volume 1-5", "Berserk")]
    [InlineData("Berserk Ch 12", "Berserk")]
    [InlineData("Berserk ch.12.5", "Berserk")]
    [InlineData("Berserk c003", "Berserk")]
    [InlineData("Berserk #12", "Berserk")]
    [InlineData("Berserk Chapter 10", "Berserk")]
    [InlineData("Berserk - Chapter", "Berserk")]
    public void Normalize_StripsVolumeAndChapterTokens(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(input).Primary);
    }

    [Theory]
    [InlineData("[Group] Some Series (Digital) {HQ}", "Some Series")]
    [InlineData("Some Series [x2]", "Some Series")]
    [InlineData("(C99) [Circle] Some Doujin", "Some Doujin")]
    public void Normalize_RemovesBracketTags(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(input).Primary);
    }

    [Fact]
    public void Normalize_TrailingEnglishTitle_BecomesSecondVariant()
    {
        var n = TitleNormalizer.Normalize("Dungeon Meshi [Delicious in Dungeon]");

        Assert.Equal("Dungeon Meshi", n.Primary);
        Assert.Equal(["Dungeon Meshi", "Delicious in Dungeon"], n.Variants);
    }

    [Fact]
    public void Normalize_SingleWordOrLeadingBracket_IsNotAVariant()
    {
        Assert.Single(TitleNormalizer.Normalize("Some Series [Digital]").Variants);
        Assert.Single(TitleNormalizer.Normalize("[Scan Group Name] Some Series").Variants);
    }

    [Fact]
    public void Normalize_YearInParentheses_BecomesAHint()
    {
        var n = TitleNormalizer.Normalize("Some Run (1989) v02");

        Assert.Equal("Some Run", n.Primary);
        Assert.Equal(1989, n.YearHint);
    }

    [Fact]
    public void Normalize_EditionWords_AreRemovedAndKeptAsHints()
    {
        var n = TitleNormalizer.Normalize("Some Series Deluxe Omnibus v01");

        Assert.Equal("Some Series", n.Primary);
        Assert.Contains("Deluxe", n.EditionHints);
        Assert.Contains("Omnibus", n.EditionHints);
    }

    [Fact]
    public void Normalize_UnderscoresAndDots_BecomeSpaces_WhenNoSpaces()
    {
        Assert.Equal("Some Long Series", TitleNormalizer.Normalize("Some_Long_Series_v01.cbz").Primary);
        Assert.Equal("Some Long Series", TitleNormalizer.Normalize("Some.Long.Series.cbr").Primary);
        // With spaces present, dots are kept (a title like "Dr. Stone" survives).
        Assert.Equal("Dr. Stone", TitleNormalizer.Normalize("Dr. Stone v01").Primary);
    }

    [Fact]
    public void Normalize_FullWidth_IsFoldedByNfkc()
    {
        Assert.Equal("ABC 12", TitleNormalizer.Normalize("ＡＢＣ １２").Primary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[Only Tags] (2020)")]
    public void Normalize_EmptyOrAllTags_YieldsEmptyPrimary(string? input)
    {
        var n = TitleNormalizer.Normalize(input);

        Assert.Equal(string.Empty, n.Primary);
        Assert.Empty(n.Variants);
    }

    [Fact]
    public void ScoringForm_StripsMacronsAndLongVowels()
    {
        Assert.Equal(TitleNormalizer.ScoringForm("Shingeki no Kyojin"), TitleNormalizer.ScoringForm("Shingeki no Kyōjin"));
        Assert.Equal(TitleNormalizer.ScoringForm("Kyoukai"), TitleNormalizer.ScoringForm("Kyōkai"));
        Assert.Equal(TitleNormalizer.ScoringForm("Yuusha"), TitleNormalizer.ScoringForm("Yūsha"));
    }

    [Fact]
    public void ScoringForm_UnifiesTimesSignAmpersandAndPunctuation()
    {
        Assert.Equal("a x b", TitleNormalizer.ScoringForm("A × B"));
        Assert.Equal("cats and dogs", TitleNormalizer.ScoringForm("Cats & Dogs!"));
        Assert.Equal("re zero", TitleNormalizer.ScoringForm("Re:Zero"));
    }
}
