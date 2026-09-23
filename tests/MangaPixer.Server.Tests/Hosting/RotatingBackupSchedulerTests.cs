namespace com.lifepixer.mangapixer.Tests.Server.Hosting;

using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB test of the rewritten backup scheduler loop on a manual
/// <see cref="TimeProvider"/>: first run after the initial delay, a live
/// "disabled" change parks the loop, and re-enabling with a new interval wakes
/// it without a restart.
/// </summary>
public sealed class RotatingBackupSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mangapixer-sched-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact(Timeout = 60000)]
    public async Task Loop_FollowsLiveSettingsChanges()
    {
        var dataRoot = Path.Combine(_root, "data");
        var backups = Path.Combine(dataRoot, "backups");
        Directory.CreateDirectory(dataRoot);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MangaPixerDbContext>(o =>
            o.UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(dataRoot, "mangapixer.db"))));
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(new RotatingBackupOptions { SafetyBackupDirectory = backups });
        services.AddSingleton<BackupSettingsResolver>();
        services.AddSingleton<IBackupLocationFileSystem, PhysicalBackupLocationFileSystem>();
        services.AddSingleton(_ => BackupLocationValidator.ForCurrentPlatform());
        services.AddSingleton<RotatingBackupState>();
        services.AddSingleton(new AppRootOptions { DataRoot = dataRoot });
        services.AddScoped<AuditService>();
        services.AddScoped<BackupService>();
        services.AddScoped<BackupLocationService>();
        services.AddScoped<RotatingBackupService>();
        await using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            await db.Database.EnsureCreatedAsync();
            await DatabaseInitialization.ConfigureDatabaseAsync(db);
        }

        var resolver = provider.GetRequiredService<BackupSettingsResolver>();
        var state = provider.GetRequiredService<RotatingBackupState>();
        using var hosted = new RotatingBackupHostedService(provider, resolver, state, time,
            NullLogger<RotatingBackupHostedService>.Instance);
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => state.LocationStatus == BackupLocationStatuses.Ok);
            Assert.Equal(0, CountSnapshots(backups));

            // Initial delay elapses: the first backup runs.
            await WaitUntilAsync(() => time.PendingTimers > 0);
            time.Advance(RotatingBackupHostedService.InitialDelay);
            await WaitUntilAsync(() => CountSnapshots(backups) == 1);

            // Disabled live: a whole day passes without a run.
            resolver.Apply(new AppSettingsEntity { BackupsEnabled = false });
            await WaitUntilAsync(() => time.PendingTimers == 0);
            time.Advance(TimeSpan.FromHours(25));
            await Task.Delay(500);
            Assert.Equal(1, CountSnapshots(backups));

            // Re-enabled with a 1 h interval: overdue, so it runs right away...
            resolver.Apply(new AppSettingsEntity { BackupsEnabled = true, BackupIntervalHours = 1 });
            await WaitUntilAsync(() => CountSnapshots(backups) == 2);

            // ...and again one interval later (once the loop is waiting again).
            await WaitUntilAsync(() => time.PendingTimers > 0);
            time.Advance(TimeSpan.FromHours(1));
            await WaitUntilAsync(() => CountSnapshots(backups) == 3);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    private static int CountSnapshots(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "rotating-*.db").Count() : 0;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), "Condition not reached in time.");
    }

    /// <summary>Deterministic clock: timers fire only when <see cref="Advance"/> passes their due time.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset start) => _now = start;

        /// <summary>Timers waiting for a due time (the loop is parked in its delay).</summary>
        public int PendingTimers
        {
            get { lock (_gate) return _timers.Count(t => t.DueAt is not null); }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(t => t.DueAt is { } at && at <= _now).ToList();
                foreach (var t in due) t.DueAt = null;
            }
            foreach (var t in due) t.Fire();
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? DueAt { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan)
                    {
                        DueAt = null;
                        return true;
                    }
                    DueAt = owner._now + dueTime;
                    owner._timers.Add(this);
                }
                if (dueTime == TimeSpan.Zero)
                {
                    lock (owner._gate) DueAt = null;
                    Fire();
                }
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (owner._gate) owner._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
