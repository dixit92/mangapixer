namespace com.lifepixer.mangapixer.Tests.Core.Scheduling;

using com.lifepixer.mangapixer.Core.Scheduling;
using Xunit;

/// <summary>
/// Unit tests for the shared due-time rule of the scheduled jobs (1.32.0): the trash run's 1.31.0 behaviour, weekly and
/// every-N-days slots, and clock changes. Zones are built in the test (no system time-zone data).
/// </summary>
public sealed class JobScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    /// <summary>UTC-5 with summer time (+1 h) from the second Sunday of March to the first Sunday of November at 02:00.</summary>
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone("test-eastern", TimeSpan.FromHours(-5), "test-eastern",
        "test-eastern", "test-eastern-summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

    private static DateTimeOffset At(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private static readonly TimeOfDaySchedule Daily4 = new(4);

    [Fact]
    public void Daily_BehavesLikeThe131TrashRun()
    {
        // Turned on at 14:00: waits for tomorrow's slot; before today's slot: today.
        Assert.Equal(At(10, 2, 4), JobSchedule.NextDue(At(10, 1, 15), Utc, Daily4, At(10, 1, 14)));
        Assert.Equal(At(10, 1, 4), JobSchedule.NextDue(At(10, 1, 3), Utc, Daily4, At(10, 1, 2)));
        // No floor: the next slot, never at once.
        Assert.Equal(At(10, 2, 4), JobSchedule.NextDue(At(10, 1, 15), Utc, Daily4, null));
        // After today's run: tomorrow.
        Assert.Equal(At(10, 3, 4), JobSchedule.NextDue(At(10, 2, 4, 1), Utc, Daily4, At(10, 2, 4)));
        // Down at 04:00 on the 3rd, back at 09:00: caught up once, now.
        Assert.Equal(At(10, 3, 9), JobSchedule.NextDue(At(10, 3, 9), Utc, Daily4, At(10, 2, 4)));
        // The hour moves to 02:00 after today's 04:00 run: today's 02:00 is covered, the next is tomorrow.
        Assert.Equal(At(10, 2, 2), JobSchedule.NextDue(At(10, 1, 10), Utc, new TimeOfDaySchedule(2), At(10, 1, 4)));
    }

    [Fact]
    public void Weekly_RunsOnTheWeekdayOnly()
    {
        // 2026-10-01 is a Thursday; the next Sunday 03:00 is the 4th.
        var sunday3 = new TimeOfDaySchedule(3, DayOfWeek.Sunday);
        Assert.Equal(At(10, 4, 3), JobSchedule.NextDue(At(10, 1, 12), Utc, sunday3, null));
        Assert.Equal(At(10, 11, 3), JobSchedule.NextDue(At(10, 4, 3, 5), Utc, sunday3, At(10, 4, 3)));
        // Missed Sunday's run (down from Saturday to Monday): caught up on Monday.
        Assert.Equal(At(10, 5, 8), JobSchedule.NextDue(At(10, 5, 8), Utc, sunday3, At(9, 27, 3)));
    }

    [Fact]
    public void EveryNDays_CountsFromTheLastRun()
    {
        var every2 = new TimeOfDaySchedule(4, EveryDays: 2);
        Assert.Equal(At(10, 3, 4), JobSchedule.NextDue(At(10, 1, 5), Utc, every2, At(10, 1, 4)));
        // A run off the slot (02:00) still waits about two days, to the first slot after 02:00 a day later.
        Assert.Equal(At(10, 2, 4), JobSchedule.NextDue(At(10, 1, 3), Utc, every2, At(10, 1, 2)));
    }

    [Fact]
    public void TheSlotIsServerLocalTime_AcrossClockChanges()
    {
        // Winter: 04:00 at UTC-5 is 09:00 UTC; summer: 08:00 UTC.
        Assert.Equal(At(1, 16, 9), JobSchedule.NextDue(At(1, 15, 12), Eastern, Daily4, null));
        Assert.Equal(At(7, 16, 8), JobSchedule.NextDue(At(7, 15, 12), Eastern, Daily4, null));
        // 2026-03-08: 02:00-03:00 does not exist; a 02:00 job runs at 03:00 summer time (07:00 UTC), once.
        var daily2 = new TimeOfDaySchedule(2);
        Assert.Equal(At(3, 8, 7), JobSchedule.NextDue(At(3, 7, 12), Eastern, daily2, null));
        Assert.Equal(At(3, 9, 6), JobSchedule.NextDue(At(3, 8, 7, 1), Eastern, daily2, At(3, 8, 7)));
        // 2026-11-01: 01:00-02:00 happens twice; a 01:00 job runs once, at the second (06:00 UTC).
        var daily1 = new TimeOfDaySchedule(1);
        Assert.Equal(At(11, 1, 6), JobSchedule.NextDue(At(10, 31, 12), Eastern, daily1, null));
        Assert.Equal(At(11, 2, 6), JobSchedule.NextDue(At(11, 1, 6, 1), Eastern, daily1, At(11, 1, 6)));
    }

    [Fact]
    public void Validation()
    {
        Assert.True(new TimeOfDaySchedule(0).IsValid);
        Assert.True(new TimeOfDaySchedule(23, DayOfWeek.Monday).IsValid);
        Assert.False(new TimeOfDaySchedule(24).IsValid);
        Assert.False(new TimeOfDaySchedule(-1).IsValid);
        Assert.False(new TimeOfDaySchedule(3, DayOfWeek.Monday, EveryDays: 2).IsValid);
        Assert.False(new TimeOfDaySchedule(3, EveryDays: 0).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => JobSchedule.NextDue(At(10, 1, 0), Utc, new TimeOfDaySchedule(24), null));
    }
}
