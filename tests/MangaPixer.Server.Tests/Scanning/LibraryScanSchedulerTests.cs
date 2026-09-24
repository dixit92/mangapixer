namespace com.lifepixer.mangapixer.Tests.Server.Scanning;

using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Service-with-DB tests of <see cref="LibraryScanScheduler"/> on a real
/// file-backed SQLite database and synthetic library folders, driving the
/// real <see cref="LibraryScanLauncher"/> (the admin scan path) with a
/// settable clock.
/// </summary>
public sealed class LibraryScanSchedulerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mangapixer-scansched-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTime _time = new(DateTimeOffset.UtcNow);
    private readonly CollectingLoggerProvider _logs = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(_logs));
        services.AddDbContext<MangaPixerDbContext>(o =>
            o.UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_root, "test.db"))));
        services.AddSingleton<TimeProvider>(_time);
        services.AddScoped<ScanLeaseService>();
        services.AddScoped<LibraryMaintenanceService>();
        services.AddSingleton<LibraryScanPolicy>();
        services.AddSingleton<ScanRunRegistry>();
        services.AddSingleton(new JobScheduler(new WorkerPoolOptions()));
        services.AddScoped<LibraryScanLauncher>();
        services.AddSingleton(new LibraryScanSchedulerOptions());
        services.AddSingleton<LibraryScanScheduler>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { }
    }

    private LibraryScanScheduler Scheduler => _provider.GetRequiredService<LibraryScanScheduler>();

    private async Task<long> AddLibraryAsync(string name, string? schedule = null, DateTimeOffset? lastCompleted = null, bool createRoot = true)
    {
        var rootPath = Path.Combine(_root, "libs", name);
        if (createRoot)
            Directory.CreateDirectory(Path.Combine(rootPath, "Series A"));

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var library = new LibraryEntity
        {
            PublicId = "lib-" + name,
            DisplayName = name,
            RootPath = rootPath,
            CreatedAt = _time.GetUtcNow(),
            ScanSchedule = schedule,
            LastScanCompleted = lastCompleted,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return library.Id;
    }

    private async Task<List<ScanRunEntity>> ScanRunsAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.ScanRuns.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
    }

    private async Task<LibraryEntity> LibraryAsync(long id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.Libraries.AsNoTracking().FirstAsync(l => l.Id == id);
    }

    [Fact]
    public async Task NeverScanned_NullSchedule_IsScannedAsDailyThroughTheLauncher()
    {
        var id = await AddLibraryAsync("never");

        Assert.Equal(1, await Scheduler.EvaluateAsync(CancellationToken.None));

        var run = Assert.Single(await ScanRunsAsync());
        Assert.Equal(id, run.LibraryId);
        Assert.Equal(2, run.Status); // completed
        Assert.Equal(LibraryScanScheduler.LeaseOwner, run.LeaseOwner);
        Assert.True(run.NodesObserved > 0);
        var library = await LibraryAsync(id);
        Assert.NotNull(library.LastScanCompleted);
        Assert.Equal("active", library.State);

        // Not due again until a day after that scan (NULL = daily).
        Assert.Equal(0, await Scheduler.EvaluateAsync(CancellationToken.None));
        _time.Now = library.LastScanCompleted!.Value.AddHours(23);
        Assert.Equal(0, await Scheduler.EvaluateAsync(CancellationToken.None));
        _time.Now = library.LastScanCompleted!.Value.AddHours(24);
        Assert.Equal(1, await Scheduler.EvaluateAsync(CancellationToken.None));
        Assert.Equal(2, (await ScanRunsAsync()).Count);
    }

    [Fact]
    public async Task DueDependsOnPresetAndLastCompletedScan()
    {
        var now = _time.GetUtcNow();
        var hourly = await AddLibraryAsync("hourly", "1h", now.AddHours(-2));
        await AddLibraryAsync("weekly", "7d", now.AddDays(-2));
        await AddLibraryAsync("sixhours", "6h", now.AddHours(-5));
        var off = await AddLibraryAsync("off", "off");

        Assert.Equal(1, await Scheduler.EvaluateAsync(CancellationToken.None));

        var run = Assert.Single(await ScanRunsAsync());
        Assert.Equal(hourly, run.LibraryId);
        Assert.Null((await LibraryAsync(off)).LastScanCompleted);
    }

    [Fact]
    public async Task Off_IsNeverScanned()
    {
        await AddLibraryAsync("off", "off");
        _time.Now = _time.Now.AddDays(30);

        Assert.Equal(0, await Scheduler.EvaluateAsync(CancellationToken.None));
        Assert.Empty(await ScanRunsAsync());
    }

    [Fact]
    public async Task RunningScan_BlocksThePass_ButAnExpiredLeaseDoesNot()
    {
        var busy = await AddLibraryAsync("busy");
        await AddLibraryAsync("waiting");

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            db.ScanRuns.Add(new ScanRunEntity
            {
                LibraryId = busy,
                ScanRevision = 1,
                Status = 1,
                LeaseOwner = "server:1",
                LeaseExpiry = DateTimeOffset.UtcNow.AddMinutes(30),
                StartedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // A live running scan: nothing starts, neither the busy library nor the other.
        Assert.Equal(0, await Scheduler.EvaluateAsync(CancellationToken.None));
        Assert.Single(await ScanRunsAsync());

        // A stale row whose lease has expired (crash) no longer blocks scheduling.
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var stale = await db.ScanRuns.SingleAsync();
            stale.LeaseExpiry = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(2, await Scheduler.EvaluateAsync(CancellationToken.None));
        Assert.Equal(3, (await ScanRunsAsync()).Count);
    }

    [Fact]
    public async Task InProcessRunningScan_BlocksThePass()
    {
        await AddLibraryAsync("any");
        var registry = _provider.GetRequiredService<ScanRunRegistry>();
        registry.Register(987654);
        try
        {
            Assert.Equal(0, await Scheduler.EvaluateAsync(CancellationToken.None));
        }
        finally
        {
            registry.Complete(987654);
        }
        Assert.Equal(1, await Scheduler.EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OverdueLibraries_AreScannedOneAtATime()
    {
        var ids = new[]
        {
            await AddLibraryAsync("first"),
            await AddLibraryAsync("second"),
            await AddLibraryAsync("third"),
        };

        Assert.Equal(3, await Scheduler.EvaluateAsync(CancellationToken.None));

        var runs = await ScanRunsAsync();
        Assert.Equal(ids, runs.Select(r => r.LibraryId));
        Assert.All(runs, r => Assert.Equal(2, r.Status));
        for (var i = 1; i < runs.Count; i++)
            Assert.True(runs[i].StartedAt >= runs[i - 1].CompletedAt, "Scheduled scans overlapped.");
    }

    [Fact]
    public async Task UnavailableRoot_IsSkippedWithOneWarningPerOutage()
    {
        var id = await AddLibraryAsync("offline", createRoot: false);

        for (var i = 0; i < 3; i++)
            Assert.Equal(0, await Scheduler.EvaluateAsync(CancellationToken.None));
        Assert.Empty(await ScanRunsAsync());
        Assert.Equal(1, _logs.Count(LogEvents.Scanning.ScheduledScanSkippedRootUnavailable));

        // The root comes back: scanned, and the recovery is logged once.
        Directory.CreateDirectory(Path.Combine((await LibraryAsync(id)).RootPath, "Series A"));
        Assert.Equal(1, await Scheduler.EvaluateAsync(CancellationToken.None));
        Assert.Equal(1, _logs.Count(LogEvents.Scanning.ScheduledScanRootAvailableAgain));
        Assert.Equal(1, _logs.Count(LogEvents.Scanning.ScheduledScanSkippedRootUnavailable));
    }

    [Fact]
    public async Task UnrecognisedStoredSchedule_UsesDefaultAndWarnsOnce()
    {
        var id = await AddLibraryAsync("odd", "fortnightly");

        Assert.Equal(1, await Scheduler.EvaluateAsync(CancellationToken.None));
        Assert.Equal(0, await Scheduler.EvaluateAsync(CancellationToken.None));
        Assert.Equal(1, _logs.Count(LogEvents.Scanning.ScheduledScanUnrecognisedSchedule));

        // Treated as daily.
        _time.Now = (await LibraryAsync(id)).LastScanCompleted!.Value.AddDays(1);
        Assert.Equal(1, await Scheduler.EvaluateAsync(CancellationToken.None));
        Assert.Equal(1, _logs.Count(LogEvents.Scanning.ScheduledScanUnrecognisedSchedule));
    }

    [Fact]
    public void EstimateNextScan_ClampsToFirstEvaluationAndIsNullWhenOff()
    {
        var scheduler = Scheduler;
        var now = _time.GetUtcNow();

        Assert.Null(scheduler.EstimateNextScan("off", now));
        Assert.Equal(now.AddHours(1), scheduler.EstimateNextScan("1h", now));
        Assert.Equal(now, scheduler.EstimateNextScan(null, null));

        scheduler.FirstEvaluationUtc = now.AddMinutes(3);
        Assert.Equal(now.AddMinutes(3), scheduler.EstimateNextScan(null, null));
        Assert.Equal(now.AddDays(1), scheduler.EstimateNextScan("1d", now));
    }

    [Fact]
    public void Options_FromConfiguration_ParsesAndValidates()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MangaPixer:Scanning:Scheduler:Enabled"] = "false",
            ["MangaPixer:Scanning:Scheduler:TickSeconds"] = "5",
            ["MangaPixer:Scanning:Scheduler:StartupDelaySeconds"] = "0",
        }).Build();
        var options = LibraryScanSchedulerOptions.FromConfiguration(config);
        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(5), options.TickInterval);
        Assert.Equal(TimeSpan.Zero, options.StartupDelay);

        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MangaPixer:Scanning:Scheduler:TickSeconds"] = "0",
            ["MangaPixer:Scanning:Scheduler:StartupDelaySeconds"] = "-1",
        }).Build();
        var defaults = LibraryScanSchedulerOptions.FromConfiguration(invalid);
        Assert.True(defaults.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), defaults.TickInterval);
        Assert.Equal(TimeSpan.FromMinutes(3), defaults.StartupDelay);
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        private readonly List<int> _eventIds = [];

        public int Count(int eventId)
        {
            lock (_eventIds)
                return _eventIds.Count(e => e == eventId);
        }

        public ILogger CreateLogger(string categoryName) => new Collector(this);
        public void Dispose() { }

        private sealed class Collector(CollectingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._eventIds)
                    owner._eventIds.Add(eventId.Id);
            }
        }
    }
}
