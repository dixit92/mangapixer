namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>
/// 1.52.0 lane R: the <c>rating:</c> term of the plain search - the viewer's own rating as a minimum in stars (<c>rating:4</c> = score 8
/// and up, <c>rating:3.5</c> = 7 and up) or <c>rating:none</c> (not rated); it is no word, combines with words, phrases and <c>-word</c>
/// terms, and a value it does not accept leaves the text plain words as before. Public, well-known titles only.
/// </summary>
public sealed class SearchRatingTermTests
{
    private static string[] Words(SearchQuery q) => q.AllWords.Select(w => w.Folded).ToArray();

    private static string[] NegatedTerms(SearchQuery q) => q.Negated.Select(t => string.Join(' ', t.Select(w => w.Folded))).ToArray();

    [Theory]
    [InlineData("rating:4", 8)]
    [InlineData("rating:3.5", 7)]
    [InlineData("rating:3,5", 7)]
    [InlineData("rating:0.5", 1)]
    [InlineData("rating:.5", 1)]
    [InlineData("rating:5", 10)]
    [InlineData("rating:4.0", 8)]
    [InlineData("RATING:2", 4)]
    [InlineData("Rating:1", 2)]
    public void Parse_ReadsTheMinimumInStars_AsTheStoredScore(string text, int minScore)
    {
        var q = SearchText.Parse(text);
        Assert.Equal(minScore, q.Rating?.MinScore);
        Assert.Empty(q.AllWords);
        Assert.True(q.IsEmpty);
        Assert.True(q.ListsRatedOnly);
    }

    [Theory]
    [InlineData("rating:none")]
    [InlineData("rating:NONE")]
    [InlineData("rating:None")]
    public void Parse_None_IsTheNotRatedFilter_AndAloneListsNothing(string text)
    {
        var q = SearchText.Parse(text);
        Assert.NotNull(q.Rating);
        Assert.Null(q.Rating!.MinScore);
        Assert.True(q.IsEmpty);
        Assert.False(q.ListsRatedOnly);
    }

    [Theory]
    [InlineData("rating:6", "rating 6")]
    [InlineData("rating:0", "rating 0")]
    [InlineData("rating:5.5", "rating 5 5")]
    [InlineData("rating:3.3", "rating 3 3")]
    [InlineData("rating:-1", "rating 1")]
    [InlineData("rating:abc", "rating abc")]
    [InlineData("rating:", "rating")]
    [InlineData("rating:4stars", "rating 4stars")]
    [InlineData("rating:10", "rating 10")]
    [InlineData("rating:4.25", "rating 4 25")]
    public void Parse_AValueItDoesNotAccept_LeavesPlainWords(string text, string words)
    {
        var q = SearchText.Parse(text);
        Assert.Null(q.Rating);
        Assert.Equal(words, string.Join(' ', Words(q)));
    }

    [Fact]
    public void Parse_OnlyATermStartIsTheFilter_NotAWordThatEndsInRating()
    {
        var q = SearchText.Parse("superrating:4");
        Assert.Null(q.Rating);
        Assert.Equal(["superrating", "4"], Words(q));
    }

    [Fact]
    public void Parse_CombinesWithWords_Phrases_AndMinusTerms()
    {
        var q = SearchText.Parse("one piece rating:4 -film \"east blue\"");
        Assert.Equal(8, q.Rating?.MinScore);
        Assert.Equal(["one", "piece"], q.Words.Select(w => w.Folded));
        Assert.Equal(["east blue"], q.Phrases.Select(p => string.Join(' ', p.Select(w => w.Folded))));
        Assert.Equal(["film"], NegatedTerms(q));
        Assert.False(q.IsEmpty);
        Assert.False(q.ListsRatedOnly);
    }

    [Fact]
    public void Parse_TheTermFirst_ThenWords()
    {
        var q = SearchText.Parse("rating:3.5 berserk");
        Assert.Equal(7, q.Rating?.MinScore);
        Assert.Equal(["berserk"], Words(q));
    }

    [Fact]
    public void Parse_ARatingWithOnlyMinusTerms_StillListsTheRated()
    {
        var q = SearchText.Parse("rating:4 -dragon");
        Assert.True(q.ListsRatedOnly);
        Assert.Equal(["dragon"], NegatedTerms(q));
        Assert.True(q.HasOnlyExclusions); // the 1.50.0 flag is unchanged; the search lists the rated ones (ListsRatedOnly)
    }

    [Fact]
    public void Parse_NoneWithWords_IsTheNotRatedFilterOverThoseWords()
    {
        var q = SearchText.Parse("akira rating:none");
        Assert.Null(q.Rating?.MinScore);
        Assert.NotNull(q.Rating);
        Assert.Equal(["akira"], Words(q));
    }

    [Fact]
    public void Parse_TwoTerms_TheLastOneCounts()
    {
        Assert.Equal(4, SearchText.Parse("rating:4 rating:2").Rating?.MinScore);
        Assert.Null(SearchText.Parse("rating:4 rating:none").Rating?.MinScore);
    }

    [Fact]
    public void Parse_AMinusRating_IsAMinusWordTerm_NotTheFilter()
    {
        var q = SearchText.Parse("naruto -rating:4");
        Assert.Null(q.Rating);
        Assert.Equal(["rating 4"], NegatedTerms(q));
    }

    [Fact]
    public void Parse_InsideAQuotedPhrase_IsPartOfThePhrase()
    {
        var q = SearchText.Parse("\"rating:4 club\"");
        Assert.Null(q.Rating);
        Assert.Equal(["rating 4 club"], q.Phrases.Select(p => string.Join(' ', p.Select(w => w.Folded))));
    }

    [Fact]
    public void Parse_NoRatingTerm_HasNoFilter()
    {
        var q = SearchText.Parse("monster");
        Assert.Null(q.Rating);
        Assert.False(q.ListsRatedOnly);
    }

    [Theory]
    [InlineData(8, 8, true)]
    [InlineData(8, 10, true)]
    [InlineData(8, 7, false)]
    [InlineData(8, null, false)]
    [InlineData(null, null, true)]
    [InlineData(null, 1, false)]
    public void Accepts_IsAMinimum_OrNotRated(int? min, int? score, bool accepted) =>
        Assert.Equal(accepted, new SearchRatingTerm(min).Accepts(score));
}
