namespace com.lifepixer.mangapixer.Core.Catalog;

using com.lifepixer.mangapixer.Core.Scheduling;

/// <summary>
/// Per-library automatic scan schedule presets (1.23.0). The stored value is a
/// stable token; a null stored value means <see cref="Default"/> (daily), so
/// libraries registered before the scheduler existed get daily scans without a
/// data migration. The server rejects any other token with 400 on write, and
/// treats an unrecognised stored value as the default on read.
/// </summary>
public static class LibraryScanSchedules
{
    public const string Off = "off";
    public const string Hourly = "1h";
    public const string Every6Hours = "6h";
    public const string Daily = "1d";
    public const string Weekly = "7d";

    /// <summary>The effective schedule when none is stored.</summary>
    public const string Default = Daily;

    /// <summary>All accepted tokens, in the order the admin UI lists them.</summary>
    public static readonly IReadOnlyList<string> Allowed = [Off, Hourly, Every6Hours, Daily, Weekly];

    /// <summary>True for an accepted token, or null (clear back to the default).</summary>
    public static bool IsValid(string? token) => token is null || Allowed.Contains(token, StringComparer.Ordinal);

    /// <summary>
    /// The effective token for a stored value: the value itself when it is an
    /// accepted token, otherwise <see cref="Default"/>. <paramref name="recognised"/>
    /// is false only for a non-null value that is not an accepted token.
    /// </summary>
    public static string Resolve(string? stored, out bool recognised)
    {
        recognised = stored is null || Allowed.Contains(stored, StringComparer.Ordinal);
        return stored is not null && recognised ? stored : Default;
    }

    /// <summary>The effective token for a stored value (see <see cref="Resolve(string?, out bool)"/>).</summary>
    public static string Resolve(string? stored) => Resolve(stored, out _);

    /// <summary>
    /// The scan interval for an effective token, or null when scheduled scans
    /// are off. An unrecognised token yields the default interval.
    /// </summary>
    public static TimeSpan? IntervalOf(string? token) => Resolve(token) switch
    {
        Off => null,
        Hourly => TimeSpan.FromHours(1),
        Every6Hours => TimeSpan.FromHours(6),
        Weekly => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(1),
    };

    /// <summary>
    /// When a library with this schedule is next due: one interval after its
    /// last completed scan, or <paramref name="now"/> when it has never been
    /// scanned. Null when scheduled scans are off.
    /// </summary>
    public static DateTimeOffset? NextDue(string? token, DateTimeOffset? lastCompleted, DateTimeOffset now)
    {
        if (IntervalOf(token) is not { } interval)
            return null;
        return lastCompleted is { } last ? last + interval : now;
    }

    /// <summary>True when a library with this schedule is due for a scan at <paramref name="now"/>.</summary>
    public static bool IsDue(string? token, DateTimeOffset? lastCompleted, DateTimeOffset now) =>
        NextDue(token, lastCompleted, now) is { } due && due <= now;

    // 1.32.0: a time of day for Daily and Weekly (libraries.ScanHour / ScanWeekday). No hour = "Any time": one interval after the
    // last completed scan, as before.

    /// <summary>The weekday of a Weekly scan with an hour when none is chosen.</summary>
    public const DayOfWeek DefaultWeekday = DayOfWeek.Sunday;

    /// <summary>True for the presets that take a time of day (Daily, Weekly).</summary>
    public static bool TakesHour(string? token) => Resolve(token) is Daily or Weekly;

    /// <summary>
    /// Null when the time fits the token, else an error code: <c>hour_not_allowed</c> (Off / Hourly / Every 6 hours),
    /// <c>invalid_hour</c>, <c>weekday_not_allowed</c> (not Weekly, or no hour), <c>invalid_weekday</c> (0 = Sunday ... 6).
    /// </summary>
    public static string? ValidateTime(string? token, int? hour, int? weekday)
    {
        if (hour is { } h)
        {
            if (!TakesHour(token))
                return "hour_not_allowed";
            if (!JobSchedule.IsValidHour(h))
                return "invalid_hour";
        }
        if (weekday is { } d)
        {
            if (Resolve(token) != Weekly || hour is null)
                return "weekday_not_allowed";
            if (d is < 0 or > 6)
                return "invalid_weekday";
        }
        return null;
    }

    /// <summary>The time-of-day schedule of a library, or null when it scans "any time" (or its preset takes no hour).</summary>
    public static TimeOfDaySchedule? TimeOf(string? token, int? hour, int? weekday)
    {
        if (hour is not { } h || !JobSchedule.IsValidHour(h) || !TakesHour(token))
            return null;
        return Resolve(token) == Weekly
            ? new TimeOfDaySchedule(h, weekday is >= 0 and <= 6 ? (DayOfWeek)weekday.Value : DefaultWeekday)
            : new TimeOfDaySchedule(h);
    }

    /// <summary>
    /// <see cref="NextDue(string?, DateTimeOffset?, DateTimeOffset)"/> with a time of day: the next slot after the last completed
    /// scan (a slot missed while the server was down is due now, once). Never scanned = now. Null when scheduled scans are off.
    /// </summary>
    public static DateTimeOffset? NextDue(string? token, DateTimeOffset? lastCompleted, DateTimeOffset now, int? hour, int? weekday, TimeZoneInfo zone)
    {
        if (TimeOf(token, hour, weekday) is not { } time)
            return NextDue(token, lastCompleted, now);
        return lastCompleted is null ? now : JobSchedule.NextDue(now, zone, time, lastCompleted);
    }
}
