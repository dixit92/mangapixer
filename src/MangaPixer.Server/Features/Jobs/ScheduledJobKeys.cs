namespace com.lifepixer.mangapixer.Server.Features.Jobs;

/// <summary>The stable keys of the scheduled jobs (1.32.0): the <c>job_runs</c> primary key and the API's job ids.</summary>
public static class ScheduledJobKeys
{
    public const string LibraryScan = "library-scan";
    public const string MetadataRefresh = "metadata-refresh";
    public const string Backup = "backup";
    public const string Trash = "trash";
    public const string CacheEviction = "cache-eviction";
    public const string SessionCleanup = "session-cleanup";
    public const string AutoMatch = "auto-match";
    public const string VolumeCovers = "volume-covers";
    public const string CoverDecisions = "cover-decisions";
    public const string ContentSignatures = "content-signatures";
    public const string Thumbnails = "thumbnails";
    public const string UpdateCheck = "update-check";

    /// <summary>The order of the Scheduled jobs section (library scans first, one row per library).</summary>
    public static IReadOnlyList<string> Ordered { get; } =
    [
        LibraryScan, MetadataRefresh, Backup, Trash, CacheEviction, AutoMatch, VolumeCovers, CoverDecisions, ContentSignatures,
        SessionCleanup, Thumbnails, UpdateCheck,
    ];
}

/// <summary>The built-in hours of the daily jobs (1.32.0 provisional decision Q8), server local time.</summary>
public static class ScheduledJobDefaults
{
    /// <summary>The series information refresh: early in the budget day, before a day of automatic matching spends it.</summary>
    public const int MetadataRefreshHour = 3;

    /// <summary>The cache clean-up: a quiet hour for readers, after the trash run (04:00).</summary>
    public const int CacheEvictionHour = 5;

    /// <summary>The effective hour: the stored one when it is 0-23, else <paramref name="fallback"/>.</summary>
    public static int HourOf(int? stored, int fallback) => stored is >= 0 and <= 23 ? stored.Value : fallback;
}
