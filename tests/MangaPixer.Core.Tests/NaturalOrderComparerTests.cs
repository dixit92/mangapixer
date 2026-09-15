namespace com.lifepixer.mangapixer.Tests.Core.Ordering;

using com.lifepixer.mangapixer.Core.Ordering;
using Xunit;

public class NaturalOrderComparerTests
{
    private readonly NaturalOrderComparer _comparer = NaturalOrderComparer.Instance;

    [Theory]
    [InlineData("1", "2", -1)]
    [InlineData("2", "1", 1)]
    [InlineData("1", "10", -1)]
    [InlineData("10", "2", 1)]
    [InlineData("10", "10", 0)]
    [InlineData("chapter1", "chapter2", -1)]
    [InlineData("chapter2", "chapter10", -1)]
    [InlineData("chapter10", "chapter2", 1)]
    public void Compare_DigitRuns_NumericOrder(string x, string y, int expected)
    {
        var result = _comparer.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Theory]
    [InlineData("001", "01", -1)]
    [InlineData("01", "1", -1)]
    [InlineData("1", "01", 1)]
    [InlineData("001", "001", 0)]
    public void Compare_LeadingZeros_DeterministicTieBreak(string x, string y, int expected)
    {
        var result = _comparer.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Theory]
    [InlineData("abc", "ABC", 1)]
    [InlineData("ABC", "abc", -1)]
    [InlineData("abc", "abc", 0)]
    public void Compare_Text_OrdinalCultureIndependent(string x, string y, int expected)
    {
        var result = _comparer.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Fact]
    public void Compare_NullHandling()
    {
        Assert.Equal(0, _comparer.Compare(null, null));
        Assert.Equal(-1, _comparer.Compare(null, "a"));
        Assert.Equal(1, _comparer.Compare("a", null));
    }

    [Theory]
    [InlineData("a1b2", "a1b2", 0)]
    [InlineData("a1b2", "a1b3", -1)]
    [InlineData("a2b1", "a1b2", 1)]
    public void Compare_MixedAlphanumeric(string x, string y, int expected)
    {
        var result = _comparer.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }

    [Theory]
    [InlineData("第1巻", "第2巻", -1)]
    [InlineData("第10巻", "第2巻", 1)]
    [InlineData("第1巻", "第1巻", 0)]
    public void Compare_CjkText_NumericOrderWorks(string x, string y, int expected)
    {
        var result = _comparer.Compare(x, y);
        Assert.Equal(expected, Math.Sign(result));
    }
}
