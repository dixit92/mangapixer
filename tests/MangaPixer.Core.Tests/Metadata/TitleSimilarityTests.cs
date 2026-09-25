namespace com.lifepixer.mangapixer.Tests.Core.Metadata;

using com.lifepixer.mangapixer.Core.Metadata;
using Xunit;

/// <summary>
/// Unit tests for <see cref="TitleSimilarity"/> (1.24.0): the in-house scoring
/// used to rank identify candidates.
/// </summary>
public sealed class TitleSimilarityTests
{
    [Fact]
    public void Identical_AfterNormalization_IsOne()
    {
        Assert.Equal(1.0, TitleSimilarity.Score("Berserk", "BERSERK!"));
        Assert.Equal(1.0, TitleSimilarity.Score("Shingeki no Kyojin", "Shingeki no Kyōjin"));
    }

    [Fact]
    public void Subset_Title_IsPenalized_NotAPerfectMatch()
    {
        // A plain token-set ratio would call these identical.
        var subset = TitleSimilarity.Score("Berserk", "Berserk of Gluttony");

        Assert.True(subset < TitleSimilarity.PossibleThreshold, $"subset scored {subset}");
    }

    [Fact]
    public void WordOrder_DoesNotMatterMuch()
    {
        Assert.True(TitleSimilarity.Score("Dungeon Delicious", "Delicious Dungeon") >= TitleSimilarity.StrongThreshold);
    }

    [Fact]
    public void SmallTypo_StaysStrongOrPossible()
    {
        var s = TitleSimilarity.Score("Vinland Saga", "Vinland Sgaa");

        Assert.True(s >= TitleSimilarity.PossibleThreshold, $"typo scored {s}");
    }

    [Fact]
    public void Unrelated_IsWeak()
    {
        Assert.True(TitleSimilarity.Score("Berserk", "Cooking Papa") < TitleSimilarity.PossibleThreshold);
    }

    [Fact]
    public void SideStory_RanksBelowTheExactTitle()
    {
        var exact = TitleSimilarity.Score("Berserk", "Berserk");
        var gaiden = TitleSimilarity.Score("Berserk", "Berserk Gaiden");

        Assert.True(exact > gaiden);
    }

    [Fact]
    public void Empty_IsZero()
    {
        Assert.Equal(0, TitleSimilarity.Score("", "x"));
        Assert.Equal(0, TitleSimilarity.Score(null, null));
    }

    [Fact]
    public void Best_TakesTheMaxOverVariantsAndCandidates()
    {
        var best = TitleSimilarity.Best(["Dungeon Meshi", "Delicious in Dungeon"], ["Delicious in Dungeon", "Other"]);

        Assert.Equal(1.0, best);
    }

    [Theory]
    [InlineData(0.95, MatchStrength.Strong)]
    [InlineData(0.85, MatchStrength.Strong)]
    [InlineData(0.7, MatchStrength.Possible)]
    [InlineData(0.6, MatchStrength.Possible)]
    [InlineData(0.59, MatchStrength.Weak)]
    public void Label_UsesTheThresholds(double score, MatchStrength expected)
    {
        Assert.Equal(expected, TitleSimilarity.Label(score));
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "", 3)]
    public void Levenshtein_ClassicCases(string a, string b, int expected)
    {
        Assert.Equal(expected, TitleSimilarity.Levenshtein(a, b));
    }

    [Fact]
    public void VeryLongInputs_AreBoundedAndStillScore()
    {
        var a = new string('a', 10_000);
        var b = new string('a', 9_000) + new string('b', 1_000);

        var s = TitleSimilarity.Score(a, b);

        Assert.InRange(s, 0, 1);
    }
}
