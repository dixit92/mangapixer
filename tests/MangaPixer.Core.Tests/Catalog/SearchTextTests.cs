namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>
/// 1.49.0 lane S: the plain search's word rules (owner decisions 4-6) - words in any order, accents and punctuation ignored, quotes keep a
/// phrase, short words start a word, the rank tiers, and the typo distance with its length bands. Public, well-known titles only.
/// </summary>
public sealed class SearchTextTests
{
    private static int? Rank(string query, string name, bool archive = false) =>
        SearchText.Rank(SearchText.Parse(query), SearchText.FoldName(name, archive));

    private static int? Fuzzy(string query, string name) =>
        SearchText.FuzzyDistance(SearchText.Parse(query), SearchText.FoldName(name, false));

    [Theory]
    [InlineData("Kaguya-sama: Love is War", "kaguya sama love is war")]
    [InlineData("  Pokémon   Adventures ", "pokemon adventures")]
    [InlineData("JoJo's Bizarre Adventure", "jojo s bizarre adventure")]
    [InlineData("[Group] Title (2020)", "group title 2020")]
    [InlineData("ÀÉÎÕÜ", "aeiou")]
    [InlineData("", "")]
    public void Fold_LowerCases_RemovesAccents_AndMakesPunctuationOneSpace(string text, string folded) =>
        Assert.Equal(folded, SearchText.Fold(text));

    [Fact]
    public void Parse_SplitsOnPunctuation_KeepsQuotedPhrases_AndAnUnclosedQuoteRunsToTheEnd()
    {
        var q = SearchText.Parse("piece, one \"monkey d\" \"luffy");
        Assert.Equal(["piece", "one", "luffy"], q.Words.Select(w => w.Folded));
        Assert.Equal(["monkey d"], q.Phrases.Select(p => string.Join(' ', p.Select(w => w.Folded))));
        Assert.Equal(["piece", "one", "luffy", "monkey"], q.IndexWords.Select(w => w.Folded));
    }

    [Fact]
    public void ExactFtsQuery_IsEveryWordOfThreeOrMoreCharacters_Quoted_AndAnded()
    {
        Assert.Equal("\"piece\" AND \"One\"", SearchText.ExactFtsQuery(SearchText.Parse("piece One")));
        Assert.Equal("\"Zero\"", SearchText.ExactFtsQuery(SearchText.Parse("Re:Zero")));
        Assert.Null(SearchText.ExactFtsQuery(SearchText.Parse("ab")));
        Assert.Null(SearchText.ExactFtsQuery(SearchText.Parse("\"\" ;;; ")));
        Assert.True(SearchText.Parse(" ;; ").IsEmpty);
    }

    [Fact]
    public void AllowsTypos_OnlyWithAnUnquotedWordOfFourOrMoreCharacters()
    {
        Assert.True(SearchText.AllowsTypos(SearchText.Parse("Naruto vol")));
        Assert.False(SearchText.AllowsTypos(SearchText.Parse("one ab")));
        Assert.False(SearchText.AllowsTypos(SearchText.Parse("\"one piece\"")));
    }

    [Theory]
    [InlineData("one piece", "One Piece", SearchText.RankExact)]
    [InlineData("one piece", "One Piece Color Walk", SearchText.RankStartsWith)]
    [InlineData("piece one", "One Piece", SearchText.RankWordStarts)]
    [InlineData("iece", "One Piece", SearchText.RankContains)]
    [InlineData("pokemon", "Pokémon Adventures", SearchText.RankStartsWith)]
    [InlineData("kaguya sama", "Kaguya-sama: Love is War", SearchText.RankStartsWith)]
    public void Rank_Tiers_ExactThenStartsWithThenAWordStartsThenContains(string query, string name, int rank) =>
        Assert.Equal(rank, Rank(query, name));

    [Fact]
    public void Rank_IgnoresTheArchiveExtension()
    {
        Assert.Equal(SearchText.RankExact, Rank("akira 01", "Akira 01.cbz", archive: true));
        Assert.Equal(SearchText.RankStartsWith, Rank("akira 01", "Akira 01.cbz", archive: false));
    }

    [Fact]
    public void Rank_AShortWordMustStartAWord_AndAPhraseMustAppearInOrder()
    {
        Assert.Equal(SearchText.RankExact, Rank("re zero", "Re:Zero"));
        Assert.Null(Rank("zero e", "Re:Zero"));           // "e" starts no word
        Assert.NotNull(Rank("\"one piece\"", "One Piece"));
        Assert.Null(Rank("\"piece one\"", "One Piece"));  // a phrase keeps its order
        Assert.NotNull(Rank("\"kaguya sama\"", "Kaguya-sama"));
    }

    [Fact]
    public void ShortSearch_MatchesNamesWithAWordStartingWithIt()
    {
        var q = SearchText.Parse("ak");
        Assert.True(SearchText.IsShort(q));
        Assert.Equal(SearchText.RankStartsWith, SearchText.Rank(q, SearchText.Fold("Akira")));
        Assert.Equal(SearchText.RankWordStarts, SearchText.Rank(q, SearchText.Fold("Domu: Akira")));
        Assert.Null(SearchText.Rank(q, SearchText.Fold("Kakashi")));
        Assert.Equal(SearchText.RankExact, SearchText.Rank(SearchText.Parse("X"), SearchText.Fold("X")));
        Assert.Null(SearchText.FuzzyDistance(q, SearchText.Fold("Akria")));
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("naruto", "nauto", 1)]
    [InlineData("naruto", "narutto", 1)]
    [InlineData("naruto", "nartuo", 1)]   // a swap of two neighbours is one edit
    [InlineData("berserk", "berserk", 0)]
    [InlineData("", "abc", 3)]
    public void Distance_IsDamerauLevenshteinWithSwaps(string a, string b, int distance) =>
        Assert.Equal(distance, SearchText.Distance(a, b));

    [Fact]
    public void Distance_StopsEarly_AboveTheLimit() => Assert.True(SearchText.Distance("abcdefgh", "zyxwvuts", 2) > 2);

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(7, 1)]
    [InlineData(8, 2)]
    [InlineData(20, 2)]
    public void MaxDistance_ByWordLength(int length, int max) => Assert.Equal(max, SearchText.MaxDistance(length));

    [Fact]
    public void FuzzyDistance_ToleratesTyposByLength_AndNeedsEveryOtherWordExact()
    {
        Assert.Equal(1, Fuzzy("berserc", "Berserk"));
        Assert.Equal(1, Fuzzy("nartuo", "Naruto"));
        Assert.Null(Fuzzy("vinland sga", "Vinland Saga"));                          // "sga" is 3 characters: never fuzzy
        Assert.Equal(1, Fuzzy("vinlnd saga", "Vinland Saga"));
        Assert.Equal(1, Fuzzy("hunterxhuntre", "HunterxHunter"));
        Assert.Null(Fuzzy("bersek kingdom", "Berserk"));                            // every word must match
        Assert.Null(Fuzzy("berserk", "Berserk"));                                   // an exact hit is not a close match
        Assert.Equal(1, Fuzzy("akria", "Akira 01 Extra"));
        Assert.Null(Fuzzy("xxxxx", "Akira"));
    }

    [Fact]
    public void FuzzyDistance_MatchesAPartlyTypedWordWithATypo()
    {
        // "frieren" typed as "freiren" inside a longer name word's start.
        Assert.Equal(1, Fuzzy("freiren", "Frieren: Beyond Journey's End"));
        Assert.Equal(1, Fuzzy("yotsubq", "Yotsuba&!"));
    }

    [Theory]
    [InlineData("domu akira", "ak", true)]
    [InlineData("akira", "ak", true)]
    [InlineData("kakashi", "ak", false)]
    [InlineData("kak akira", "ak", true)]
    [InlineData("re zero", "re z", true)]
    [InlineData("x", "", true)]
    public void HasWordStartingWith_MatchesAtTheStartOrAfterASpace(string name, string text, bool expected) =>
        Assert.Equal(expected, SearchText.HasWordStartingWith(name, text));

    /// <summary>Every name within the typo distance of a word passes at least one of its SQL patterns (all one- and two-edit variants).</summary>
    [Theory]
    [InlineData("akra")]
    [InlineData("frieren")]
    [InlineData("naruto")]
    [InlineData("berserk")]
    [InlineData("vinland")]
    [InlineData("hunterxhunter")]
    [InlineData("yotsuba1")]
    public void TypoPatterns_NeverDropANameWithinTheDistance(string word)
    {
        var patterns = SearchText.TypoPatterns(new SearchWord(word, word))!;
        var regexes = patterns.Select(p => new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("%", ".*").Replace("_", ".") + "$")).ToList();
        var max = SearchText.MaxDistance(word.Length);
        var variants = new HashSet<string> { word };
        for (var round = 0; round < max; round++)
            variants.UnionWith(variants.SelectMany(Edits).ToList());
        foreach (var name in variants)
        {
            // As a whole name, inside a longer name, and as the start of a longer word: always found by a pattern.
            foreach (var shaped in new[] { name, "the " + name + " saga", name + "xyz" })
                Assert.True(regexes.Any(r => r.IsMatch(shaped)), $"'{shaped}' (from '{word}') passes no pattern");
        }
        Assert.Null(SearchText.TypoPatterns(new SearchWord("akí", "aki")));
        Assert.Null(SearchText.TypoPatterns(new SearchWord("café", "café")));
    }

    private static IEnumerable<string> Edits(string w)
    {
        const string letters = "aekrz1";
        for (var i = 0; i <= w.Length; i++)
        {
            foreach (var c in letters)
                yield return w[..i] + c + w[i..];                         // insert
            if (i < w.Length)
            {
                yield return w[..i] + w[(i + 1)..];                       // delete
                foreach (var c in letters)
                    yield return w[..i] + c + w[(i + 1)..];               // substitute
            }
            if (i + 1 < w.Length)
                yield return w[..i] + w[i + 1] + w[i] + w[(i + 2)..];     // swap neighbours
        }
    }
}
