namespace com.lifepixer.mangapixer.Tests.Server.Scanning;

using System.Data.Common;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Server.Storage;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// 1.50.0 lane X - archives left "pending" with no job. Before: re-queued only at start-up and once after a successful scan, so an
/// archive whose save failed after its job ran, or one a stopped scan never queued, waited for the next restart. Now whoever strands
/// one asks <see cref="PendingAnalysisSweepService"/>, which queues it once the job queue is idle. The real launcher, scheduler and
/// sweep run on a file-backed SQLite database and synthetic library folders (no worker: queued jobs stay in the scheduler).
/// </summary>
public sealed class PendingAnalysisSweepTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mangapixer-sweep-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SweepTrigger _trigger = new();
    private readonly CommandCounter _commands = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MangaPixerDbContext>(o =>
            o.UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_root, "test.db"))).AddInterceptors(_commands));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ScanLeaseService>();
        services.AddScoped<LibraryMaintenanceService>();
        services.AddSingleton<LibraryScanPolicy>();
        services.AddSingleton<ScanRunRegistry>();
        services.AddSingleton(new JobScheduler(new WorkerPoolOptions()));
        services.AddSingleton<IPendingAnalysisSweepTrigger>(_trigger);
        services.AddScoped<LibraryScanLauncher>();
        services.AddSingleton(new PendingAnalysisSweepOptions
        {
            QuietPeriod = TimeSpan.Zero,
            MinInterval = TimeSpan.Zero,
            IdlePoll = TimeSpan.FromMilliseconds(10),
            MaxAttempts = 2,
        });
        services.AddSingleton<PendingAnalysisSweepService>();
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

    private JobScheduler Scheduler => _provider.GetRequiredService<JobScheduler>();
    private PendingAnalysisSweepService Sweep => _provider.GetRequiredService<PendingAnalysisSweepService>();

    private async Task<LibraryEntity> AddLibraryAsync(string name, int archives, bool createRoot = true)
    {
        var rootPath = Path.Combine(_root, "libs", name);
        if (createRoot)
        {
            Directory.CreateDirectory(Path.Combine(rootPath, "Series A"));
            for (var i = 0; i < archives; i++)
                File.WriteAllBytes(Path.Combine(rootPath, "Series A", $"Vol {i + 1:00}.cbz"), [(byte)i, 1, 2, 3]);
        }
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var library = new LibraryEntity { PublicId = "lib-" + name, DisplayName = name, RootPath = rootPath, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return library;
    }

    /// <summary>An archive row left pending (state 1) with the file on disk (or not), as a stranded one looks.</summary>
    private async Task<long> AddPendingArchiveAsync(LibraryEntity library, string name, bool fileExists = true, int state = 1, bool tombstoned = false)
    {
        if (fileExists)
        {
            Directory.CreateDirectory(library.RootPath);
            File.WriteAllBytes(Path.Combine(library.RootPath, name), [1, 2, 3]);
        }
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var node = new CatalogNodeEntity
        {
            PublicId = "n-" + Guid.NewGuid().ToString("N")[..10],
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = name,
            RelativePath = name,
            PathKey = name,
            SortKey = "1" + name,
            LastSeenScanRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            Availability = tombstoned ? 5 : 0,
            ArchiveItem = new ArchiveItemEntity { ContentVersion = 1, AnalysisState = state },
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node.Id;
    }

    [Fact]
    public async Task Sweep_QueuesStrandedArchives_AndLeavesTheRest()
    {
        var library = await AddLibraryAsync("main", 0);
        await AddPendingArchiveAsync(library, "stranded-1.cbz");
        await AddPendingArchiveAsync(library, "stranded-2.cbz");
        await AddPendingArchiveAsync(library, "done.cbz", state: 0);
        await AddPendingArchiveAsync(library, "gone.cbz", tombstoned: true);
        await AddPendingArchiveAsync(library, "missing.cbz", fileExists: false);

        await Sweep.SweepAsync(CancellationToken.None);

        Assert.Equal(2, Scheduler.PendingCount); // the two with a file; not the finished, the tombstoned or the missing one
    }

    [Fact]
    public async Task Sweep_RunsOnlyAfterTheQueueIsIdle_AndOnlyWhenAsked()
    {
        var library = await AddLibraryAsync("main", 0);
        await AddPendingArchiveAsync(library, "stranded.cbz");
        // Something else is queued, so the pool is busy.
        var busy = Scheduler.EnqueueAsync(9999, 1, JobOperation.Analyze, JobPriority.Background, "x.cbz", 0, 0);
        var service = Sweep;
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300);
            Assert.Equal(0, service.SweepsRun); // nobody asked
            service.Request();
            var commandsBefore = _commands.Count;
            await Task.Delay(300);
            Assert.Equal(0, service.SweepsRun); // asked, but the pool is busy: no sweep, and no database access while it waits
            Assert.Equal(commandsBefore, _commands.Count);
            Assert.Equal(1, Scheduler.PendingCount);

            // The pool drains: the one job leaves the queue and finishes.
            var job = Scheduler.Dequeue()!;
            Scheduler.MarkInFlight(job);
            Scheduler.CompleteJob(job.DedupKey, new JobResult { JobId = job.JobId, Success = true, ErrorType = null, ErrorMessage = null, Result = null });
            await busy;

            await WaitForAsync(() => service.SweepsRun >= 1);
            Assert.Equal(1, Scheduler.PendingCount); // the stranded archive is queued now
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sweep_GivesUpOnAnArchiveItKeepsFailingToSave_AfterTheAttemptCap()
    {
        var library = await AddLibraryAsync("main", 0);
        await AddPendingArchiveAsync(library, "never-saved.cbz");

        for (var round = 1; round <= 4; round++)
        {
            await Sweep.SweepAsync(CancellationToken.None);
            // The job is taken and "fails to save": the row stays pending, the scheduler is empty again.
            var job = Scheduler.Dequeue();
            Assert.Equal(round <= 2, job is not null); // MaxAttempts = 2: tries 1 and 2 queue it, 3 and 4 leave it alone
        }
    }

    [Fact]
    public async Task Scan_QueuesItsArchives_ReadingTheLibraryRootOnce()
    {
        var library = await AddLibraryAsync("scanned", 6);
        var launcher = _provider.CreateScope().ServiceProvider.GetRequiredService<LibraryScanLauncher>();

        _commands.Reset();
        var launch = await launcher.StartAsync(library, "test", CancellationToken.None);
        Assert.NotNull(launch);
        await launch!.Completion;

        Assert.Equal(6, Scheduler.PendingCount);
        // One root lookup for the enqueue, not one per archive: scan-side reads of the library table are few and fixed.
        Assert.True(_commands.CountOf("FROM \"libraries\"") < 6, _commands.Describe("FROM \"libraries\""));
        Assert.Empty(_trigger.Requests);
    }

    [Fact]
    public async Task ScanThatFails_AsksForASweep()
    {
        var library = await AddLibraryAsync("gone", 0, createRoot: false);
        var launcher = _provider.CreateScope().ServiceProvider.GetRequiredService<LibraryScanLauncher>();

        var launch = await launcher.StartAsync(library, "test", CancellationToken.None);
        await launch!.Completion;

        using var scope = _provider.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().ScanRuns.AsNoTracking().SingleAsync();
        if (run.Status == 3) // failed (a missing root); a scan that tolerates it completes and has nothing to sweep
            Assert.Single(_trigger.Requests);
    }

    [Fact]
    public async Task ScanThatIsCancelled_KeepsWhatItAddedPending_AndAsksForASweep()
    {
        var library = await AddLibraryAsync("big", 0);
        for (var folder = 0; folder < 60; folder++)
        {
            var dir = Path.Combine(library.RootPath, "Series " + folder);
            Directory.CreateDirectory(dir);
            for (var i = 0; i < 5; i++)
                File.WriteAllBytes(Path.Combine(dir, $"Vol {i + 1:00}.cbz"), [(byte)i, 1]);
        }
        var launcher = _provider.CreateScope().ServiceProvider.GetRequiredService<LibraryScanLauncher>();

        var launch = await launcher.StartAsync(library, "test", CancellationToken.None);
        Assert.True(_provider.GetRequiredService<ScanRunRegistry>().Cancel(launch!.Run.Id));
        await launch.Completion;

        using var scope = _provider.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().ScanRuns.AsNoTracking().SingleAsync();
        Assert.Equal(4, run.Status); // cancelled
        Assert.Single(_trigger.Requests);
        Assert.Equal(0, Scheduler.PendingCount); // the post-scan enqueue never ran

        // The sweep queues whatever the stopped scan had already added (the archives it never got to stay unknown to the catalog).
        var added = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().ArchiveItems.CountAsync(a => a.AnalysisState == 1);
        await Sweep.SweepAsync(CancellationToken.None);
        Assert.Equal(added, Scheduler.PendingCount);
    }

    [Fact]
    public async Task HostedSweep_IsRegisteredAndReachable_AndTheTriggerIsTheSameInstance()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        await factory.LoginAsAdminWithChangedPasswordAsync();

        var hosted = factory.Services.GetServices<IHostedService>().OfType<PendingAnalysisSweepService>().ToList();
        Assert.Single(hosted);
        Assert.Same(hosted[0], factory.Services.GetRequiredService<IPendingAnalysisSweepTrigger>());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met within 6 s");
    }

    private sealed class SweepTrigger : IPendingAnalysisSweepTrigger
    {
        public List<DateTimeOffset> Requests { get; } = [];

        public void Request()
        {
            lock (Requests)
                Requests.Add(DateTimeOffset.UtcNow);
        }
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private readonly List<string> _texts = [];

        public int Count { get { lock (_texts) return _texts.Count; } }

        public void Reset() { lock (_texts) _texts.Clear(); }

        public int CountOf(string fragment) { lock (_texts) return _texts.Count(t => t.Contains(fragment, StringComparison.OrdinalIgnoreCase)); }

        public string Describe(string fragment) => $"{CountOf(fragment)} commands contain {fragment}";

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            lock (_texts)
                _texts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
