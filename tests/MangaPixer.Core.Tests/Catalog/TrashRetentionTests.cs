namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>Unit tests for the move window / trash retention presets (1.31.0).</summary>
public sealed class TrashRetentionTests
{
    [Fact]
    public void Presets_AreDailyWeeklyMonthlyQuarterlyYearly_AndMonthlyIsTheDefault()
    {
        Assert.Equal([1, 7, 30, 90, 365], TrashRetention.AllowedDays);
        Assert.Equal(30, TrashRetention.DefaultDays);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(30, 30)]
    [InlineData(90, 90)]
    [InlineData(365, 365)]
    [InlineData(0, 30)]
    [InlineData(14, 30)]
    [InlineData(-7, 30)]
    public void DaysOf_KeepsAPreset_AndFallsBackToMonthlyOtherwise(int? stored, int expected) =>
        Assert.Equal(expected, TrashRetention.DaysOf(stored));

    [Fact]
    public void WindowStart_IsNowMinusTheWindow()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), TrashRetention.WindowStart(now, null));
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), TrashRetention.WindowStart(now, TrashRetention.Weekly));
        Assert.Equal(TimeSpan.FromDays(365), TrashRetention.WindowOf(TrashRetention.Yearly));
    }
}
