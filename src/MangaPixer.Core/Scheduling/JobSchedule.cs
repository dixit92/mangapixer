namespace com.lifepixer.mangapixer.Core.Scheduling;

/// <summary>
/// When a scheduled job runs (1.32.0): at a whole <see cref="Hour"/> of the server's local day - every day, every
/// <see cref="EveryDays"/> days, or once a week on <see cref="Weekday"/>. A weekday and an every-N-days count do not combine.
/// </summary>
public readonly record struct TimeOfDaySchedule(int Hour, DayOfWeek? Weekday = null, int EveryDays = 1)
{
    public bool IsValid => JobSchedule.IsValidHour(Hour) && EveryDays is >= 1 and <= 30 && (Weekday is null || EveryDays == 1);
}

/// <summary>
/// The shared due-time rule of the scheduled jobs (1.32.0), generalizing the 1.31.0 trash run's <c>TrashSchedule.NextDue</c>.
/// Pure. The floor is the job's persisted last run (or the moment it was turned on, whichever is later): a slot at or before
/// the floor is covered. A slot that passed after the floor while the server was down is caught up ONCE (due now); a job
/// with no floor waits for its next slot. On a day a clock change skips the hour the slot is an hour later; on a day the
/// hour happens twice it is the second one (once either way).
/// </summary>
public static class JobSchedule
{
    public static bool IsValidHour(int hour) => hour is >= 0 and <= 23;

    /// <summary>
    /// The next due time: <paramref name="nowUtc"/> when a slot passed after <paramref name="floorUtc"/> (a missed run, caught up
    /// once); otherwise the next slot. Every N days counts from the floor: the first slot after floor + (N - 1) days.
    /// </summary>
    public static DateTimeOffset NextDue(DateTimeOffset nowUtc, TimeZoneInfo zone, TimeOfDaySchedule schedule, DateTimeOffset? floorUtc)
    {
        if (!schedule.IsValid)
            throw new ArgumentOutOfRangeException(nameof(schedule));
        if (floorUtc is not { } floor)
            return FirstSlotAfter(nowUtc, zone, schedule);
        var due = FirstSlotAfter(floor + TimeSpan.FromDays(schedule.EveryDays - 1), zone, schedule);
        return due < nowUtc ? nowUtc : due;
    }

    /// <summary>The first slot strictly after <paramref name="instantUtc"/>.</summary>
    public static DateTimeOffset FirstSlotAfter(DateTimeOffset instantUtc, TimeZoneInfo zone, TimeOfDaySchedule schedule)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instantUtc, zone).DateTime);
        // A week plus a day covers a weekly slot and a slot pushed past midnight by a clock change.
        for (var d = 0; d <= 8; d++)
        {
            var day = date.AddDays(d);
            if (schedule.Weekday is { } weekday && day.DayOfWeek != weekday)
                continue;
            var slot = SlotOn(day, zone, schedule.Hour);
            if (slot > instantUtc)
                return slot;
        }
        throw new InvalidOperationException("No slot within eight days.");
    }

    /// <summary>The run time on a local date, in UTC (an hour later when a clock change skips it).</summary>
    public static DateTimeOffset SlotOn(DateOnly localDate, TimeZoneInfo zone, int hour)
    {
        var local = localDate.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
