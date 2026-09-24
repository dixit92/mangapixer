namespace com.lifepixer.mangapixer.Server.Scanning;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Scan scheduler knobs (1.23.0), from <c>MangaPixer:Scanning:Scheduler:*</c>.
/// The per-library schedule itself is admin-set (see <see cref="LibraryScanSchedules"/>);
/// these only control the evaluation loop.
/// </summary>
public sealed class LibraryScanSchedulerOptions
{
    /// <summary>Master switch for automatic scans. Default on.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How often due libraries are evaluated. Default 1 minute.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Grace period after boot before the first evaluation. Default 3 minutes.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(3);

    public static LibraryScanSchedulerOptions FromConfiguration(IConfiguration config)
    {
        var section = config.GetSection("MangaPixer:Scanning:Scheduler");
        var defaults = new LibraryScanSchedulerOptions();
        return new LibraryScanSchedulerOptions
        {
            Enabled = bool.TryParse(section["Enabled"], out var enabled) ? enabled : defaults.Enabled,
            TickInterval = int.TryParse(section["TickSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tick) && tick >= 1
                ? TimeSpan.FromSeconds(tick) : defaults.TickInterval,
            StartupDelay = int.TryParse(section["StartupDelaySeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay) && delay >= 0
                ? TimeSpan.FromSeconds(delay) : defaults.StartupDelay,
        };
    }
}

/// <summary>
/// Decides which libraries are due for an automatic scan and starts them, one
/// at a time, through <see cref="LibraryScanLauncher"/> (the same path as the
/// admin scan endpoints). Driven by <c>LibraryScanSchedulerHostedService</c>;
/// <see cref="EvaluateAsync"/> is one evaluation pass.
///
/// A library is due when its schedule is not off and at least one interval has
/// passed since its last completed scan, manual or scheduled
/// (<see cref="LibraryEntity.LastScanCompleted"/>, stamped by
/// <see cref="LibraryScanCoordinator"/>); a never-scanned library is due at once. A pass starts nothing while any scan is running
/// (SQLite is single-writer and the post-scan analysis shares the worker pool),
/// and it awaits each scan it starts before starting the next. A library whose
/// scheduled scan fails is not retried for <see cref="RetryBackoff"/> (or its
/// interval, if shorter). A library whose root is unavailable is skipped with
/// one warning per outage. Logs carry library IDs, counts and timings only.
/// </summary>
public sealed class LibraryScanScheduler
{
    /// <summary>The lease owner recorded on scheduled scan runs.</summary>
    public const string LeaseOwner = "scheduler";

    /// <summary>Minimum wait before a failed scheduled scan is retried.</summary>
    public static readonly TimeSpan RetryBackoff = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScanRunRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger<LibraryScanScheduler> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly HashSet<long> _unavailable = [];
    private readonly HashSet<long> _warnedUnrecognised = [];
    private readonly Dictionary<long, DateTimeOffset> _lastFailedAttempt = [];

    public LibraryScanScheduler(
        IServiceScopeFactory scopeFactory,
        ScanRunRegistry registry,
        TimeProvider time,
        LibraryScanSchedulerOptions options,
        ILogger<LibraryScanScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _time = time;
        Options = options;
        _logger = logger;
    }

    public LibraryScanSchedulerOptions Options { get; }

    /// <summary>
    /// When the hosted loop runs its first evaluation (boot + startup delay);
    /// null until the loop has started. Used to clamp the "next scan" estimate.
    /// </summary>
    public DateTimeOffset? FirstEvaluationUtc { get; set; }

    /// <summary>
    /// Approximate time of the next automatic scan for a library, or null when
    /// its schedule is off or the scheduler is disabled. Past-due libraries
    /// report their due time (the next evaluation picks them up).
    /// </summary>
    public DateTimeOffset? EstimateNextScan(string? storedSchedule, DateTimeOffset? lastCompleted)
    {
        if (!Options.Enabled)
            return null;
        var now = _time.GetUtcNow();
        var due = LibraryScanSchedules.NextDue(storedSchedule, lastCompleted, now);
        if (due is { } d && FirstEvaluationUtc is { } first && first > d)
            return first;
        return due;
    }

    /// <summary>
    /// One evaluation pass. Returns the number of scans started (each already
    /// finished when this returns).
    /// </summary>
    public async Task<int> EvaluateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await EvaluateCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> EvaluateCoreAsync(CancellationToken ct)
    {
        List<LibraryEntity> libraries;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            if (_registry.AnyRunning || await AnyActiveScanRowAsync(db, ct))
                return 0;

            libraries = await db.Libraries.AsNoTracking().OrderBy(l => l.Id).ToListAsync(ct);
        }

        var started = 0;
        foreach (var library in libraries)
        {
            ct.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow();

            var token = LibraryScanSchedules.Resolve(library.ScanSchedule, out var recognised);
            if (!recognised && _warnedUnrecognised.Add(library.Id))
                _logger.LogWarning(LogEvents.Scanning.ScheduledScanUnrecognisedSchedule,
                    "Library {LibraryId} has an unrecognised scan schedule; using the default", library.Id);

            if (library.State is not ("active" or "maintenance"))
                continue;
            if (!LibraryScanSchedules.IsDue(token, library.LastScanCompleted, now))
                continue;
            if (_lastFailedAttempt.TryGetValue(library.Id, out var failedAt)
                && now - failedAt < Min(RetryBackoff, LibraryScanSchedules.IntervalOf(token)!.Value))
                continue;
            if (!RootAvailable(library))
                continue;

            if (await RunScanAsync(library, ct))
                started++;
        }
        return started;
    }

    /// <summary>
    /// Starts one scan and waits for it. False when the lease was taken by
    /// someone else in between (nothing started).
    /// </summary>
    private async Task<bool> RunScanAsync(LibraryEntity library, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var launcher = scope.ServiceProvider.GetRequiredService<LibraryScanLauncher>();
        var startedAt = _time.GetTimestamp();
        var launch = await launcher.StartAsync(library, LeaseOwner, ct);
        if (launch is null)
            return false;

        _logger.LogInformation(LogEvents.Scanning.ScheduledScanStarted,
            "Scheduled scan started for library {LibraryId} (scan run {ScanRunId})", library.Id, launch.Run.Id);

        // Stopping the host abandons the wait, not the scan: the scan keeps its
        // own lifetime exactly like an admin-triggered one.
        await launch.Completion.WaitAsync(ct);

        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var status = await db.ScanRuns.AsNoTracking()
            .Where(s => s.Id == launch.Run.Id).Select(s => s.Status).FirstOrDefaultAsync(ct);
        if (status == 2)
            _lastFailedAttempt.Remove(library.Id);
        else
            _lastFailedAttempt[library.Id] = _time.GetUtcNow();

        _logger.LogInformation(LogEvents.Scanning.ScheduledScanFinished,
            "Scheduled scan for library {LibraryId} finished with status {Status} in {ElapsedMs} ms",
            library.Id, status, (long)_time.GetElapsedTime(startedAt).TotalMilliseconds);
        return true;
    }

    private bool RootAvailable(LibraryEntity library)
    {
        bool available;
        try
        {
            available = new ReadOnlyLibraryFileSystem(library.RootPath).RootExists();
        }
        catch (Exception)
        {
            available = false;
        }

        if (!available)
        {
            if (_unavailable.Add(library.Id))
                _logger.LogWarning(LogEvents.Scanning.ScheduledScanSkippedRootUnavailable,
                    "Scheduled scan skipped: library {LibraryId} root is not accessible (further skips are not logged until it returns)", library.Id);
            return false;
        }

        if (_unavailable.Remove(library.Id))
            _logger.LogInformation(LogEvents.Scanning.ScheduledScanRootAvailableAgain,
                "Library {LibraryId} root is accessible again; scheduled scans resume", library.Id);
        return true;
    }

    /// <summary>
    /// A queued or running scan-run row with a live lease. Rows whose lease has
    /// expired (a crash between startup recoveries) do not block scheduling.
    /// </summary>
    private async Task<bool> AnyActiveScanRowAsync(MangaPixerDbContext db, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var leases = await db.ScanRuns.AsNoTracking()
            .Where(s => s.Status == 0 || s.Status == 1)
            .Select(s => s.LeaseExpiry)
            .ToListAsync(ct);
        return leases.Any(expiry => expiry is null || expiry > now);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
