namespace com.lifepixer.mangapixer.Tests.Core;

using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

public sealed class LibraryScanSchedulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_IsDaily()
    {
        Assert.Equal("1d", LibraryScanSchedules.Default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    [InlineData("1h")]
    [InlineData("6h")]
    [InlineData("1d")]
    [InlineData("7d")]
    public void IsValid_AcceptedTokenOrNull_ReturnsTrue(string? token)
    {
        Assert.True(LibraryScanSchedules.IsValid(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("daily")]
    [InlineData("1D")]
    [InlineData("2h")]
    [InlineData(" 1d")]
    public void IsValid_UnknownOrWrongCase_ReturnsFalse(string token)
    {
        Assert.False(LibraryScanSchedules.IsValid(token));
    }

    [Fact]
    public void Resolve_Null_IsDefaultAndRecognised()
    {
        Assert.Equal("1d", LibraryScanSchedules.Resolve(null, out var recognised));
        Assert.True(recognised);
    }

    [Fact]
    public void Resolve_Unrecognised_FallsBackToDefaultAndFlagsIt()
    {
        Assert.Equal("1d", LibraryScanSchedules.Resolve("fortnightly", out var recognised));
        Assert.False(recognised);
    }

    [Theory]
    [InlineData("1h", 1)]
    [InlineData("6h", 6)]
    [InlineData("1d", 24)]
    [InlineData("7d", 168)]
    [InlineData(null, 24)]
    [InlineData("garbage", 24)]
    public void IntervalOf_MapsPresets(string? token, int hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), LibraryScanSchedules.IntervalOf(token));
    }

    [Fact]
    public void Off_HasNoIntervalAndIsNeverDue()
    {
        Assert.Null(LibraryScanSchedules.IntervalOf("off"));
        Assert.Null(LibraryScanSchedules.NextDue("off", null, Now));
        Assert.False(LibraryScanSchedules.IsDue("off", null, Now));
        Assert.False(LibraryScanSchedules.IsDue("off", Now.AddYears(-1), Now));
    }

    [Fact]
    public void NeverScanned_IsDueNow()
    {
        Assert.Equal(Now, LibraryScanSchedules.NextDue("1d", null, Now));
        Assert.True(LibraryScanSchedules.IsDue("7d", null, Now));
    }

    [Fact]
    public void Due_ExactlyOneIntervalAfterLastCompletedScan()
    {
        var last = Now.AddHours(-6);
        Assert.Equal(Now, LibraryScanSchedules.NextDue("6h", last, Now));
        Assert.True(LibraryScanSchedules.IsDue("6h", last, Now));
        Assert.False(LibraryScanSchedules.IsDue("6h", last.AddTicks(1), Now));
    }

    [Fact]
    public void NullStoredSchedule_UsesDailyInterval()
    {
        Assert.False(LibraryScanSchedules.IsDue(null, Now.AddHours(-23), Now));
        Assert.True(LibraryScanSchedules.IsDue(null, Now.AddHours(-24), Now));
    }

    [Fact]
    public void Allowed_ListsPresetsInUiOrder()
    {
        Assert.Equal(["off", "1h", "6h", "1d", "7d"], LibraryScanSchedules.Allowed);
    }
}
