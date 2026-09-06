namespace com.lifepixer.mangaplex.Tests.Core.Ordering;

using com.lifepixer.mangaplex.Core.Ordering;
using Xunit;

/// <summary>
/// Golden tests for natural-order comparison covering edge cases required by P02:
/// digit overflow, leading zeros, case sensitivity, CJK, Unicode, nested names.
/// These tests are the authoritative specification for ordering behavior.
/// </summary>
public sealed class NaturalOrderGoldenTests
{
    /// <summary>
    /// Verifies that a full list of mixed names sorts in the expected order.
    /// This is the golden ordering test — any change to NaturalOrderComparer
    /// must preserve this ordering.
    /// </summary>
    [Fact]
    public void GoldenList_MixedNames_SortsCorrectly()
    {
        var input = new[]
        {
            "Volume 10",
            "volume 2",
            "Volume 1",
            "Volume 2",
            "Volume 001",
            "Volume 01",
            "Volume 1.5",
            "Volume 10.5",
            "Special",
            "special",
            "第1巻",
            "第2巻",
            "第10巻",
            "Chapter 001",
            "Chapter 01",
            "Chapter 1",
            "Chapter 2",
            "Chapter 10",
            "Chapter 100",
            "Chapter 1000",
            "附录",
            "Appendix",
            "cover.png",
            "Cover.png",
            "page001.png",
            "page002.png",
            "page010.png",
            "page100.png",
            "page2.png",
            "page1.png",
        };

        var expected = new[]
        {
            "Appendix",
            "Chapter 001",
            "Chapter 01",
            "Chapter 1",
            "Chapter 2",
            "Chapter 10",
            "Chapter 100",
            "Chapter 1000",
            "Cover.png",
            "Special",
            "Volume 001",
            "Volume 01",
            "Volume 1",
            "Volume 1.5",
            "Volume 2",
            "Volume 10",
            "Volume 10.5",
            "cover.png",
            "page001.png",
            "page1.png",
            "page002.png",
            "page2.png",
            "page010.png",
            "page100.png",
            "special",
            "volume 2",
            "第1巻",
            "第2巻",
            "第10巻",
            "附录",
        };

        var sorted = input.OrderBy(x => x, NaturalOrderComparer.Instance).ToArray();
        Assert.Equal(expected, sorted);
    }

    [Theory]
    [InlineData("9999999999999999999", "10000000000000000000", -1)]
    [InlineData("10000000000000000000", "9999999999999999999", 1)]
    [InlineData("9999999999999999999", "9999999999999999999", 0)]
    public void Compare_DigitOverflow_HandlesByLength(string x, string y, int expected)
    {
        // Numbers exceeding int64 range should compare by digit length, not overflow
        var result = NaturalOrderComparer.Instance.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Theory]
    [InlineData("001", "01", -1)]
    [InlineData("01", "1", -1)]
    [InlineData("001", "1", -1)]
    [InlineData("0001", "001", -1)]
    [InlineData("1", "01", 1)]
    [InlineData("01", "001", 1)]
    [InlineData("1", "001", 1)]
    [InlineData("000", "00", -1)]
    [InlineData("00", "0", -1)]
    [InlineData("0", "00", 1)]
    public void Compare_LeadingZeros_MoreZerosFirst(string x, string y, int expected)
    {
        var result = NaturalOrderComparer.Instance.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Theory]
    [InlineData("abc", "ABC", 1)]
    [InlineData("ABC", "abc", -1)]
    [InlineData("abc", "abc", 0)]
    [InlineData("Abc", "aBC", -1)]
    [InlineData("aBC", "Abc", 1)]
    public void Compare_CaseSensitivity_UppercaseFirst(string x, string y, int expected)
    {
        // Uppercase has lower ordinal values, so comes first
        var result = NaturalOrderComparer.Instance.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Theory]
    [InlineData("第1巻", "第2巻", -1)]
    [InlineData("第2巻", "第10巻", -1)]
    [InlineData("第10巻", "第2巻", 1)]
    [InlineData("第1巻", "第1巻", 0)]
    [InlineData("第1話", "第2話", -1)]
    [InlineData("第10話", "第2話", 1)]
    public void Compare_Cjk_NumericOrderWorks(string x, string y, int expected)
    {
        var result = NaturalOrderComparer.Instance.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Theory]
    [InlineData("café", "cafe", 1)]
    [InlineData("café", "cafz", 1)] // 'é' (233) > 'z' (122) in ordinal comparison
    [InlineData("naïve", "naive", 1)]
    [InlineData("résumé", "resume", 1)]
    public void Compare_UnicodeAccents_OrdinalComparison(string x, string y, int expected)
    {
        var result = NaturalOrderComparer.Instance.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Theory]
    [InlineData("Vol.1", "Vol.2", -1)]
    [InlineData("Vol.10", "Vol.2", 1)]
    [InlineData("Vol.1.5", "Vol.1", 1)]
    [InlineData("Vol.1.5", "Vol.2", -1)]
    [InlineData("1-2", "1-10", -1)]
    [InlineData("1-10", "1-2", 1)]
    [InlineData("1.2.3", "1.2.10", -1)]
    [InlineData("1.2.10", "1.2.3", 1)]
    public void Compare_NestedNames_NumericOrderInSegments(string x, string y, int expected)
    {
        var result = NaturalOrderComparer.Instance.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Theory]
    [InlineData("a", "a", 0)]
    [InlineData("a", "b", -1)]
    [InlineData("b", "a", 1)]
    [InlineData("aaa", "aaa", 0)]
    [InlineData("aaa", "aaab", -1)]
    [InlineData("aaab", "aaa", 1)]
    public void Compare_PureText_OrdinalComparison(string x, string y, int expected)
    {
        var result = NaturalOrderComparer.Instance.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Theory]
    [InlineData("10", "9", 1)]
    [InlineData("9", "10", -1)]
    [InlineData("2", "10", -1)]
    [InlineData("10", "2", 1)]
    [InlineData("100", "99", 1)]
    [InlineData("99", "100", -1)]
    public void Compare_PureDigits_NumericOrder(string x, string y, int expected)
    {
        var result = NaturalOrderComparer.Instance.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Fact]
    public void Compare_FullSortKey_RoundTrip()
    {
        // Verify that sort key encoding produces the same ordering as the comparer
        // Correct order: page001 (eff=1,z=2) < page01 (eff=1,z=1) < page1 (eff=1,z=0)
        // < page2 (eff=1,z=0) < page10 (eff=2,sig="10") < page020 (eff=2,sig="20")
        // < page100 (eff=3)
        var names = new[] { "page10", "page2", "page1", "page001", "page01", "page100", "page020" };
        var comparerSorted = names.OrderBy(x => x, NaturalOrderComparer.Instance).ToArray();
        var sortKeySorted = names.OrderBy(SortKey.EncodeName, StringComparer.Ordinal).ToArray();

        Assert.Equal(comparerSorted, sortKeySorted);
    }

    [Fact]
    public void Compare_SortKey_MixedCase_MatchesComparer()
    {
        // Verify sort key ordering matches comparer for mixed-case names
        var names = new[] { "Cover.png", "cover.png", "ABC", "abc", "Abc", "aBC" };
        var comparerSorted = names.OrderBy(x => x, NaturalOrderComparer.Instance).ToArray();
        var sortKeySorted = names.OrderBy(SortKey.EncodeName, StringComparer.Ordinal).ToArray();

        Assert.Equal(comparerSorted, sortKeySorted);
    }
}
