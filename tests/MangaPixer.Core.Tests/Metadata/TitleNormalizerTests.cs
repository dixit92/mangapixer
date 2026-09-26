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

    [Theory]
    [InlineData("Some Series v00 (2008) [Scan Team Name] [OneShot].cbz")]
    [InlineData("Some Series [Scan Team Name] (Digital)")]
    [InlineData("Some Series [Scan Team Name] {HQ} v01")]
    [InlineData("Some Series [Vol. 0007 Ch. 5 - A Chapter Title [Scan Team Name]].cbz")]
    public void Normalize_BracketFollowedByFurtherTags_IsAGroupNotAVariant(string name)
    {
        var n = TitleNormalizer.Normalize(name);

        Assert.Equal("Some Series", n.Primary);
        Assert.Equal(["Some Series"], n.Variants);
    }

    [Fact]
    public void Normalize_TrailingEnglishTitleBeforeAYear_IsStillAVariant()
    {
        var n = TitleNormalizer.Normalize("Dungeon Meshi [Delicious in Dungeon] (2014)");

        Assert.Equal(["Dungeon Meshi", "Delicious in Dungeon"], n.Variants);
        Assert.Equal(2014, n.YearHint);
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

    [Theory]
    [InlineData("SOME TITLE! Master Edition", "SOME TITLE", "Master Edition")]
    [InlineData("Some Series Perfect Edition v01", "Some Series", "Perfect Edition")]
    [InlineData("Some Series - Complete Edition", "Some Series", "Complete Edition")]
    [InlineData("Some Series Collector's Edition", "Some Series", "Collector's Edition")]
    [InlineData("Some Series Full Color Edition", "Some Series", "Full Color Edition")]
    [InlineData("Some Series Kanzenban v03", "Some Series", "Kanzenban")]
    [InlineData("Some Series (Shinsoban)", "Some Series", null)] // a bracket tag is dropped before
    public void Normalize_EditionPhrases_AreRemovedAndKeptAsHints(string name, string primary, string? hint)
    {
        var n = TitleNormalizer.Normalize(name);

        Assert.Equal(primary, n.Primary);
        if (hint is not null)
            Assert.Contains(hint, n.EditionHints);
    }

    [Theory]
    [InlineData("Edition Wars")]
    [InlineData("The Editions of Master")]
    public void Normalize_EditionAlone_IsKept(string name) => Assert.Equal(name, TitleNormalizer.Normalize(name).Primary);

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
    // --- Stage 2 (auto-match) additions -----------------------------------------------------

    [Theory]
    [InlineData("Ren'ai Flops", "Renai Flops")]
    [InlineData("Hell’s Paradise", "Hells Paradise")]
    [InlineData("Kaguya-sama", "Kaguya sama")]
    public void ScoringForm_RemovesApostrophes_InsteadOfSplitting(string a, string b)
    {
        Assert.Equal(TitleNormalizer.ScoringForm(b), TitleNormalizer.ScoringForm(a));
        Assert.Equal("renai", TitleNormalizer.ScoringForm("Ren'ai"));
    }

    [Theory]
    [InlineData("Some Title (unclosed", "Some Title unclosed")]
    [InlineData("Some Title [Tag", "Some Title Tag")]
    [InlineData("Some Title) v01", "Some Title")]
    [InlineData("Some Title (Digital) [Group", "Some Title Group")]
    public void Normalize_UnbalancedBracket_DoesNotSurviveIntoPrimary(string input, string expected)
    {
        var primary = TitleNormalizer.Normalize(input).Primary;

        Assert.Equal(expected, primary);
        Assert.DoesNotContain(primary, c => c is '(' or ')' or '[' or ']' or '{' or '}');
    }

    [Theory]
    [InlineData("Some Series", new string[0])]
    [InlineData("Some Series 2", new[] { "2" })]
    [InlineData("Some Series 2 - The Return", new[] { "2" })]
    [InlineData("Some Series 2: The Return", new[] { "2" })]
    [InlineData("Some Series Part 3 - Subtitle", new[] { "3" })]
    [InlineData("Some Series Part III: Subtitle", new[] { "3" })]
    [InlineData("Some Series Season 2", new[] { "2" })]
    [InlineData("Some Series II", new[] { "2" })]
    [InlineData("20th Century Boys", new string[0])]
    [InlineData("7 Seeds", new string[0])]
    [InlineData("Ranma 1/2", new string[0])]
    [InlineData("Mob Psycho 100", new[] { "100" })]
    [InlineData("Some Series 2019", new string[0])]
    public void NumberTokens_FindsSequelAndPartNumbers(string title, string[] expected)
    {
        Assert.Equal(expected, TitleNormalizer.NumberTokens(title));
    }

    [Fact]
    public void DerivedVariants_SplitSubtitleAndSequelNumber()
    {
        var derived = TitleNormalizer.DerivedVariants("Some Long Series 2 - The Return");

        Assert.Contains(new DerivedTitle("Some Long Series 2", DerivedTitleKind.SubtitleSplit), derived);
        Assert.Contains(new DerivedTitle("Some Long Series", DerivedTitleKind.SequelNumberSplit), derived);
    }

    [Theory]
    [InlineData("Series - Subtitle")] // one word before the separator: no split
    [InlineData("Some Series")]
    [InlineData("")]
    public void DerivedVariants_NothingToDerive_IsEmpty(string title)
    {
        Assert.DoesNotContain(TitleNormalizer.DerivedVariants(title), d => d.Kind == DerivedTitleKind.SubtitleSplit);
    }

    [Fact]
    public void Normalize_FlagsDerivedVariants_AndKeepsPrimaryStable()
    {
        var n = TitleNormalizer.Normalize("Some Long Series Part 3 - Subtitle [English Series Name]");

        Assert.Equal("Some Long Series Part 3 - Subtitle", n.Primary);
        Assert.Equal(["Some Long Series Part 3 - Subtitle", "English Series Name"], n.Variants);
        Assert.Contains(n.Derived, d => d.Text == "Some Long Series Part 3" && d.Kind == DerivedTitleKind.SubtitleSplit);
        Assert.Contains(n.Derived, d => d.Text == "Some Long Series" && d.Kind == DerivedTitleKind.SequelNumberSplit);
        Assert.DoesNotContain(n.Derived, d => n.Variants.Contains(d.Text));
    }

    [Theory]
    [InlineData("Some Series v01 (2019) (Digital) (Group).cbz", "Some Series")]
    [InlineData("Some Series - Chapter 012.cbz", "Some Series")]
    [InlineData("Some Series 03.cbz", "Some Series")]
    [InlineData("Some Series 01-03.cbz", "Some Series")]
    [InlineData("[Group] Some Series v02 - A Subtitle.cbz", "Some Series")]
    [InlineData("(C99) [Circle (Artist)] Some Story (Some Parody) [English].cbz", "Some Story")]
    [InlineData("001 [A Chapter Title].cbz", "")]
    [InlineData("Vol 01.cbz", "")]
    [InlineData("012 - A Chapter Title.cbz", "")]
    [InlineData("7 Seeds v01.cbz", "7 Seeds")]
    public void ArchiveBaseTitle_StripsUnitsAndTrailingNumbers(string archive, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.ArchiveBaseTitle(archive));
    }

    [Fact]
    public void ArchiveTitle_ReturnsTheBaseMostArchivesShare()
    {
        Assert.Equal("English Name", TitleNormalizer.ArchiveTitle(
            ["English Name v01.cbz", "English Name v02.cbz", "English Name v03.cbz", "Other Thing.cbz"]));
        Assert.Equal("Two Halves", TitleNormalizer.ArchiveTitle(["Two Halves 1.cbz", "Two Halves 2.cbz", "Else.cbz", "More.cbz"]));
        Assert.Null(TitleNormalizer.ArchiveTitle(["Alpha.cbz", "Beta.cbz", "Gamma.cbz"]));
        Assert.Null(TitleNormalizer.ArchiveTitle(["001.cbz", "002.cbz"]));
        Assert.Null(TitleNormalizer.ArchiveTitle([]));
    }
}
