namespace com.lifepixer.mangapixer.Server.Hosting;

using System.Collections.Concurrent;
using System.Threading.Channels;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Timing and limits of <see cref="PendingAnalysisSweepService"/> (tests set them to zero).</summary>
public sealed class PendingAnalysisSweepOptions
{
    /// <summary>The pool must stay idle this long before a sweep runs.</summary>
    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>At least this long between two sweeps, however often one is requested.</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the in-memory "is the pool idle" check repeats while waiting (no database access).</summary>
    public TimeSpan IdlePoll { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Queued tries per archive and content version per process; an archive that never reaches a saved result stops being retried.</summary>
    public int MaxAttempts { get; set; } = 3;
}

/// <summary>
/// 1.50.0 lane X: re-queues archives left "pending" with no job. Pending archives were re-queued only at start-up and once after a
/// successful scan, so one whose save failed after its job ran (event 4027), or that a stopped scan never queued, waited for the next
/// restart or scan. Whoever strands one calls <see cref="Request"/>; the sweep then waits until the job queue is idle (an in-memory
/// check - the database is not touched while the pool is busy), and runs ONE query per active library through the same enqueue the
/// post-scan path uses. A pending archive whose file is gone, or whose analysis keeps failing to save, is tried
/// <see cref="PendingAnalysisSweepOptions.MaxAttempts"/> times and then left alone, so the sweep cannot loop.
/// </summary>
public sealed class PendingAnalysisSweepService : BackgroundService, IPendingAnalysisSweepTrigger
{
    private readonly IServiceScopeFactory _scopes;
    private readonly JobScheduler _scheduler;
    private readonly PendingAnalysisSweepOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PendingAnalysisSweepService> _logger;
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });
    private readonly ConcurrentDictionary<(long ItemId, long ContentVersion), int> _attempts = new();
    private long _lastSweepTimestamp;
    private int _sweeps;

    public PendingAnalysisSweepService(
        IServiceScopeFactory scopes,
        JobScheduler scheduler,
        PendingAnalysisSweepOptions options,
        TimeProvider time,
        ILogger<PendingAnalysisSweepService> logger)
    {
        _scopes = scopes;
        _scheduler = scheduler;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Sweeps that ran in this process (tests).</summary>
    internal int SweepsRun => Volatile.Read(ref _sweeps);

    public void Request()
    {
        if (_requests.Writer.TryWrite(true))
            _logger.LogDebug(LogEvents.Worker.PendingSweepRequested, "Pending-archive sweep requested");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _requests.Reader.WaitToReadAsync(stoppingToken))
            {
                _requests.Reader.TryRead(out _);
                await WaitUntilDueAsync(stoppingToken);
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Minimum interval since the last sweep, then the pool idle for the quiet period (in-memory checks only).</summary>
    private async Task WaitUntilDueAsync(CancellationToken ct)
    {
        if (_lastSweepTimestamp != 0)
        {
            var wait = _options.MinInterval - _time.GetElapsedTime(_lastSweepTimestamp);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, _time, ct);
        }
        while (true)
        {
            while (!IsIdle)
                await Task.Delay(_options.IdlePoll, _time, ct);
            if (_options.QuietPeriod > TimeSpan.Zero)
                await Task.Delay(_options.QuietPeriod, _time, ct);
            if (IsIdle)
                return;
        }
    }

    private bool IsIdle => _scheduler.PendingCount == 0 && _scheduler.InFlightCount == 0;

    /// <summary>One sweep over every active library (also called by tests).</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var libraryIds = await db.Libraries.AsNoTracking().Where(l => l.State == "active").Select(l => l.Id).ToListAsync(ct);
            int found = 0, queued = 0, gaveUp = 0;
            foreach (var libraryId in libraryIds)
            {
                var (f, q) = await PendingAnalysisEnqueue.EnqueueLibraryAsync(db, _scheduler, _logger, libraryId, "sweep",
                    admit: (item, version) =>
                    {
                        var tries = _attempts.AddOrUpdate((item, version), 1, static (_, n) => n + 1);
                        if (tries <= _options.MaxAttempts)
                            return true;
                        gaveUp++;
                        return false;
                    }, ct);
                found += f;
                queued += q;
            }
            _lastSweepTimestamp = _time.GetTimestamp();
            Interlocked.Increment(ref _sweeps);
            _logger.LogInformation(LogEvents.Worker.PendingSweepRan,
                "Pending-archive sweep: {Libraries} libraries, {Found} pending, {Queued} queued", libraryIds.Count, found, queued);
            if (gaveUp > 0)
                _logger.LogWarning(LogEvents.Worker.PendingSweepGaveUp,
                    "Pending-archive sweep left {Count} archive(s) alone after {Attempts} tries", gaveUp, _options.MaxAttempts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastSweepTimestamp = _time.GetTimestamp();
            _logger.LogWarning(LogEvents.Worker.PendingSweepFailed, "Pending-archive sweep failed: {Error}", ex.GetType().Name);
        }
    }
}
