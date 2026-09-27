namespace com.lifepixer.mangapixer.Tests.Core.Reading;

using com.lifepixer.mangapixer.Core.Reading;
using Xunit;

/// <summary>
/// Unit tests for "near the end counts as finished" (1.27.0): a zero-based position is
/// at the end when at most n = min(5, max(1, floor(pageCount / 20))) pages follow it,
/// and the first page of a multi-page archive never is.
/// </summary>
public sealed class NearEndRuleTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    [InlineData(1, 0)]   // the only page
    [InlineData(2, 0)]   // opening page 1 of 2 never finishes it
    [InlineData(3, 1)]
    [InlineData(19, 1)]
    [InlineData(20, 1)]  // 5% of 20 = 1
    [InlineData(21, 1)]  // 1.05 rounds down
    [InlineData(39, 1)]
    [InlineData(40, 2)]
    [InlineData(99, 4)]
    [InlineData(100, 5)]
    [InlineData(200, 5)] // 10 capped at 5
    [InlineData(5000, 5)]
    public void PagesAfterAllowed_FollowsTheRoundingAndCap(int pageCount, int expected)
        => Assert.Equal(expected, NearEndRule.PagesAfterAllowed(pageCount));

    // (zero-based index, pageCount, at the end?) at every boundary the brief names.
    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(0, 2, false)]
    [InlineData(1, 2, true)]
    [InlineData(0, 3, false)]
    [InlineData(1, 3, true)]
    [InlineData(17, 20, false)] // 18/20
    [InlineData(18, 20, true)]  // 19/20 - owner example
    [InlineData(19, 20, true)]  // 20/20
    [InlineData(18, 21, false)] // 19/21: two pages follow
    [InlineData(19, 21, true)]  // 20/21
    [InlineData(93, 100, false)]
    [InlineData(94, 100, true)] // 95/100
    [InlineData(189, 200, false)] // 190/200 - owner example: resume mid
    [InlineData(193, 200, false)] // 194/200
    [InlineData(194, 200, true)]  // 195/200 - owner example
    [InlineData(199, 200, true)]
    [InlineData(250, 200, true)]  // past the last index (defensive)
    public void IsAtEnd_Boundaries(int pageIndex, int pageCount, bool expected)
        => Assert.Equal(expected, NearEndRule.IsAtEnd(pageIndex, pageCount));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void IsAtEnd_UnknownPageCount_IsNeverTheEnd(int? pageCount)
    {
        Assert.False(NearEndRule.IsAtEnd(0, pageCount));
        Assert.False(NearEndRule.IsAtEnd(500, pageCount));
        Assert.Null(NearEndRule.FirstEndIndex(pageCount));
    }

    // Double-page view: the saved index of a spread is its LAST page, so the spread
    // (17, 18) of a 20-page archive is at the end while (15, 16) is not.
    [Fact]
    public void Spread_SavedAsItsLastPage()
    {
        Assert.True(NearEndRule.IsAtEnd(18, 20));
        Assert.False(NearEndRule.IsAtEnd(16, 20));
    }

    [Fact]
    public void FirstEndIndex_MatchesIsAtEnd()
    {
        for (var pc = 1; pc <= 260; pc++)
        {
            var first = NearEndRule.FirstEndIndex(pc)!.Value;
            Assert.False(first > 0 && NearEndRule.IsAtEnd(first - 1, pc));
            Assert.True(NearEndRule.IsAtEnd(first, pc));
            Assert.InRange(pc - 1 - first, 0, NearEndRule.MaxPagesAfter);
        }
    }
}
