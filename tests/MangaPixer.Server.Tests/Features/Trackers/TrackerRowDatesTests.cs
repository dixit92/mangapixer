namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Xunit;

/// <summary>
/// The Tracking page's "Dates on &lt;tracker&gt;" cell (1.43.0, owner review 2026-10-15): the user's own date first, then what the tracker
/// keeps, and only when the tracker keeps none the automatic date MangaPixer would send - never MangaPixer's first mark next to the
/// tracker's finish (the review instance showed a 2026 start beside a 2025 finish for a series finished on MyAnimeList in 2025).
/// </summary>
public sealed class TrackerRowDatesTests
{
    private static TrackerEntryEntity Entry(string? autoStart = null, string? autoFinish = null, string? remoteStart = null,
        string? remoteFinish = null, string? ownStart = null, string? ownFinish = null) => new()
        {
            Tracker = "mal",
            AutoStartedOn = autoStart,
            AutoFinishedOn = autoFinish,
            RemoteStartedOn = remoteStart,
            RemoteFinishedOn = remoteFinish,
            StartedOnOverride = ownStart,
            FinishedOnOverride = ownFinish,
        };

    [Fact]
    public void TheTrackersDates_ShowBeforeTheAutomaticOnes()
    {
        var dates = TrackerListService.DatesOf(Entry(autoStart: "2026-10-15", remoteStart: "2025-10-05", remoteFinish: "2025-10-17"));
        Assert.Equal(new DateOnly(2025, 10, 5), dates.StartedOn);
        Assert.Equal(new DateOnly(2025, 10, 17), dates.FinishedOn);
        Assert.False(dates.StartedOnOverridden);
        Assert.False(dates.FinishedOnOverridden);
    }

    [Fact]
    public void TheAutomaticDates_ShowOnlyWhereTheTrackerKeepsNone()
    {
        var dates = TrackerListService.DatesOf(Entry(autoStart: "2026-10-15", autoFinish: "2026-10-16"));
        Assert.Equal(new DateOnly(2026, 10, 15), dates.StartedOn);
        Assert.Equal(new DateOnly(2026, 10, 16), dates.FinishedOn);
        Assert.Null(dates.RemoteStartedOn);
        Assert.Null(dates.RemoteFinishedOn);
    }

    [Fact]
    public void TheUsersDates_WinOverBoth()
    {
        var dates = TrackerListService.DatesOf(Entry(autoStart: "2026-10-15", remoteStart: "2025-10-05", remoteFinish: "2025-10-17",
            ownStart: "2024-01-02", ownFinish: "2024-02-03"));
        Assert.Equal(new DateOnly(2024, 1, 2), dates.StartedOn);
        Assert.Equal(new DateOnly(2024, 2, 3), dates.FinishedOn);
        Assert.True(dates.StartedOnOverridden);
        Assert.True(dates.FinishedOnOverridden);
    }
}
