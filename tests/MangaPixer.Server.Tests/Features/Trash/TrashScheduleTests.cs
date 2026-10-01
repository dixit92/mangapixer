namespace com.lifepixer.mangapixer.Tests.Server.Features.Trash;

using com.lifepixer.mangapixer.Server.Features.Trash;
using Xunit;

/// <summary>Unit tests for the daily automatic trash run's due time (1.31.0): 04:00 server local time, a missed run caught up once.</summary>
public sealed class TrashScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void JustTurnedOn_WaitsForTheNextSlot_NeverRunsAtOnce()
    {
        Assert.Equal(At(2, 4), TrashSchedule.NextDue(At(1, 15), Utc, lastRunUtc: null, enabledAtUtc: At(1, 14)));
        Assert.Equal(At(1, 4), TrashSchedule.NextDue(At(1, 3), Utc, null, At(1, 2)));
        Assert.Equal(At(2, 4), TrashSchedule.NextDue(At(1, 15), Utc, null, null));
    }

    [Fact]
    public void AfterTodaysRun_TheNextIsTomorrow()
    {
        Assert.Equal(At(3, 4), TrashSchedule.NextDue(At(2, 4, 1), Utc, lastRunUtc: At(2, 4), enabledAtUtc: At(1, 14)));
    }

    [Fact]
    public void AMissedSlot_AfterTheSwitchWasTurnedOn_IsDueNow()
    {
        // Ran on the 2nd, the server was down at 04:00 on the 3rd, back at 09:00.
        Assert.Equal(At(3, 9), TrashSchedule.NextDue(At(3, 9), Utc, lastRunUtc: At(2, 4), enabledAtUtc: At(1, 14)));
        // Turned on again at 05:00 after a long pause: today's 04:00 was before it, so no catch-up.
        Assert.Equal(At(4, 4), TrashSchedule.NextDue(At(3, 9), Utc, lastRunUtc: At(2, 4), enabledAtUtc: At(3, 5)));
    }

    [Fact]
    public void TheSlotIsServerLocalTime()
    {
        // 04:00 at UTC+2 is 02:00 UTC.
        Assert.Equal(At(2, 2), TrashSchedule.NextDue(At(1, 15), Plus2, null, At(1, 14)));
        Assert.Equal(4, TrashSchedule.RunHour);
    }
}
