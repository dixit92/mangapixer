namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// The admin Scheduled jobs section (1.32.0, <c>GET /api/v1/admin/jobs</c>): every job MangaPixer runs on its own, with its rhythm,
/// last run and next run, and the server's clock. Job keys, library ids and names an admin already sees, counts and times only -
/// never a path or a title.
/// </summary>
public sealed record ScheduledJobsDto
{
    /// <summary>The server's clock now (UTC instant).</summary>
    public required DateTimeOffset ServerTime { get; init; }

    /// <summary>The server's time zone as an IANA id (e.g. <c>America/New_York</c>; <c>UTC</c> when unset). Every hour is in this zone.</summary>
    public required string TimeZone { get; init; }

    /// <summary>The zone's current offset from UTC, in minutes.</summary>
    public required int UtcOffsetMinutes { get; init; }

    /// <summary>The jobs in display order (one row per library for the scans).</summary>
    public required IReadOnlyList<ScheduledJobDto> Jobs { get; init; }

    /// <summary>The series information refresh's cadence and today's counts.</summary>
    public required RefreshCadenceDto Refresh { get; init; }
}

/// <summary>One scheduled job (or one library's scans).</summary>
public sealed record ScheduledJobDto
{
    /// <summary>The job key (<c>library-scan</c>, <c>metadata-refresh</c>, <c>backup</c>, <c>trash</c>, <c>cache-eviction</c>, ...).</summary>
    public required string Key { get; init; }

    /// <summary>The library of a <c>library-scan</c> row (opaque id), else null.</summary>
    public string? LibraryId { get; init; }

    /// <summary>The library's display name for a <c>library-scan</c> row, else null.</summary>
    public string? LibraryName { get; init; }

    /// <summary><c>daily</c>, <c>weekly</c>, <c>interval</c>, <c>continuous</c>, <c>startup</c> or <c>onDemand</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>False when the job is switched off (or waits for a switch, e.g. automatic matching).</summary>
    public required bool Enabled { get; init; }

    /// <summary>True when the admin can choose its time here (<c>PUT /api/v1/admin/jobs/{key}</c>, or the library's scan schedule).</summary>
    public required bool Configurable { get; init; }

    /// <summary>True when server configuration pins the time (it cannot be changed here).</summary>
    public bool ManagedByConfig { get; init; }

    /// <summary>The server-local hour it runs at, or null for "any time" / not a daily job.</summary>
    public int? Hour { get; init; }

    /// <summary>The hour used when none is chosen (null = "any time").</summary>
    public int? DefaultHour { get; init; }

    /// <summary>The weekday of a weekly library scan (0 = Sunday ... 6), else null.</summary>
    public int? Weekday { get; init; }

    /// <summary>The interval in hours of an interval job (scans, backups, sweeps), else null.</summary>
    public double? IntervalHours { get; init; }

    /// <summary>The scan preset of a <c>library-scan</c> row (<c>off</c>, <c>1h</c>, <c>6h</c>, <c>1d</c>, <c>7d</c>), else null.</summary>
    public string? ScanSchedule { get; init; }

    public DateTimeOffset? LastStartedAt { get; init; }
    public DateTimeOffset? LastFinishedAt { get; init; }

    /// <summary><c>ok</c>, <c>failed</c>, <c>skipped</c> or <c>waiting</c>; null while running or never run.</summary>
    public string? LastOutcome { get; init; }

    /// <summary>What the last run did, counts only (e.g. <c>87 refreshed</c>).</summary>
    public string? LastDetail { get; init; }

    /// <summary>When it runs next (a time in the past = due at the next check), or null (off, continuous, on demand).</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>Why it waits (a short code, e.g. <c>automatic_off</c>, <c>budget_exhausted</c>), or null.</summary>
    public string? WaitingCode { get; init; }

    /// <summary>True while a run is going.</summary>
    public bool Running { get; init; }
}

/// <summary>The refresh cadence (1.32.0): the admin's choice, today's refresh count and how many series follow each cadence.</summary>
public sealed record RefreshCadenceDto
{
    /// <summary>"Check ongoing series": 7, 14 or 30 days.</summary>
    public required int OngoingDays { get; init; }

    /// <summary>"Check finished series": 30, 90 or 180 days.</summary>
    public required int FinishedDays { get; init; }

    /// <summary>"Follow each series' publishing pace".</summary>
    public required bool FollowPace { get; init; }

    public required IReadOnlyList<int> AllowedOngoingDays { get; init; }
    public required IReadOnlyList<int> AllowedFinishedDays { get; init; }

    /// <summary>Refreshes started today (the budget day, midnight server time). No cap of their own: the daily budget is the only one.</summary>
    public required int UsedToday { get; init; }

    /// <summary>Linked series past their check date now.</summary>
    public required int Overdue { get; init; }

    /// <summary>How many linked series are checked at each cadence (days ascending).</summary>
    public required IReadOnlyList<RefreshCadenceCountDto> ByDays { get; init; }
}

public sealed record RefreshCadenceCountDto
{
    public required int Days { get; init; }
    public required int Count { get; init; }
}

/// <summary>
/// <c>PUT /api/v1/admin/jobs/{key}</c> (1.32.0): the hour of a daily job (<c>metadata-refresh</c>, <c>cache-eviction</c>, <c>trash</c>,
/// <c>backup</c>). Replaces the hour: null = the built-in default (backups: any time).
/// </summary>
public sealed record UpdateJobScheduleRequest
{
    public int? Hour { get; init; }
}

/// <summary><c>PUT /api/v1/admin/jobs/metadata-refresh/cadence</c> (1.32.0): any field left null keeps its value.</summary>
public sealed record UpdateRefreshCadenceRequest
{
    public int? OngoingDays { get; init; }
    public int? FinishedDays { get; init; }
    public bool? FollowPace { get; init; }
}

/// <summary>
/// One linked series' refresh cadence (1.32.0, admin only, <c>GET /api/v1/admin/jobs/metadata-refresh/series/{nodeId}</c>): how often
/// it is checked, why, and when next.
/// </summary>
public sealed record SeriesRefreshCadenceDto
{
    public required int Days { get; init; }

    /// <summary><c>finished</c>, <c>choice</c>, <c>pace</c> or <c>paused</c>.</summary>
    public required string Reason { get; init; }

    /// <summary>The estimated days between two volumes when the pace gave the cadence, else null.</summary>
    public double? VolumeIntervalDays { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }
    public required DateTimeOffset NextCheckAt { get; init; }
}
