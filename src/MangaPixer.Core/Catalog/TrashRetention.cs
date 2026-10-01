namespace com.lifepixer.mangapixer.Core.Catalog;

/// <summary>
/// The move window, which is also the trash retention (1.31.0, owner decision): how long a tombstoned node stays a move
/// candidate - also across libraries - before the trash may purge it. The admin picks one of five presets; the stored value is
/// the number of days, null meaning the default (Monthly). Pure.
/// </summary>
public static class TrashRetention
{
    public const int Daily = 1;
    public const int Weekly = 7;
    public const int Monthly = 30;
    public const int Quarterly = 90;
    public const int Yearly = 365;

    /// <summary>The default when nothing is stored: Monthly (the owner's 30 days).</summary>
    public const int DefaultDays = Monthly;

    /// <summary>The only values an admin can choose, shortest first.</summary>
    public static IReadOnlyList<int> AllowedDays { get; } = [Daily, Weekly, Monthly, Quarterly, Yearly];

    public static bool IsAllowed(int days) => AllowedDays.Contains(days);

    /// <summary>The effective window in days: the stored value when it is one of the presets, otherwise the default.</summary>
    public static int DaysOf(int? storedDays) => storedDays is { } d && IsAllowed(d) ? d : DefaultDays;

    public static TimeSpan WindowOf(int? storedDays) => TimeSpan.FromDays(DaysOf(storedDays));

    /// <summary>
    /// The oldest tombstone time still inside the window at <paramref name="now"/>: a node tombstoned at or after this is a move
    /// candidate; one tombstoned before it is past the window (the trash may purge it).
    /// </summary>
    public static DateTimeOffset WindowStart(DateTimeOffset now, int? storedDays) => now - WindowOf(storedDays);
}
