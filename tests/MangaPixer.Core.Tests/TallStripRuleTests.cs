namespace com.lifepixer.mangapixer.Tests.Core.Reading;

using com.lifepixer.mangapixer.Core.Reading;
using Xunit;

/// <summary>
/// Unit tests for the shared tall-strip rule (1.41.0, the 1.24.0 rule extracted): a page is tall from 2:1, fewer than 20 measured
/// pages is unknown, and at least half tall is a tall-strip sample.
/// </summary>
public sealed class TallStripRuleTests
{
    [Theory]
    [InlineData(100, 200, true)]   // exactly 2:1
    [InlineData(100, 199, false)]
    [InlineData(100, 1500, true)]
    [InlineData(1500, 100, false)] // a landscape spread
    [InlineData(700, 1000, false)] // a manga page
    public void IsTall_FromTwoToOne(int width, int height, bool expected) =>
        Assert.Equal(expected, TallStripRule.IsTall(width, height));

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(19, 19, null)]   // too few measured
    [InlineData(20, 10, true)]   // exactly half
    [InlineData(20, 9, false)]
    [InlineData(400, 400, true)]
    [InlineData(400, 199, false)]
    public void Classify_NeedsTwentyMeasuredAndHalfTall(int measured, int tall, bool? expected) =>
        Assert.Equal(expected, TallStripRule.Classify(measured, tall));

    [Fact]
    public void Classify_Sample_CountsTallPages()
    {
        var sample = Enumerable.Range(0, 30).Select(i => i < 15 ? (100, 300) : (100, 150)).ToList();
        Assert.True(TallStripRule.Classify(sample));
        Assert.False(TallStripRule.Classify(sample.Skip(1).ToList())); // 14 of 29
        Assert.Null(TallStripRule.Classify(sample.Take(19).ToList()));
    }

    [Fact]
    public void Constants_AreTheLongStandingRule()
    {
        Assert.Equal(2.0, TallStripRule.Ratio);
        Assert.Equal(400, TallStripRule.SamplePages);
        Assert.Equal(20, TallStripRule.MinMeasuredPages);
    }
}
