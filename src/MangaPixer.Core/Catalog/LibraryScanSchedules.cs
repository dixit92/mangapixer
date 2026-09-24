namespace com.lifepixer.mangapixer.Core.Catalog;

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
}
