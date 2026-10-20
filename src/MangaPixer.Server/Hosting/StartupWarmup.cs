namespace com.lifepixer.mangapixer.Server.Hosting;

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Home;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Start-up warm-up (1.46.0 lane D). The first requests after a restart used to pay for JIT, EF query compilation, an empty SQLite page
/// cache and a media worker that had never encoded a page (lane M: library root 1.0 s instead of 0.55 s, first page 0.8 s instead of
/// 0.46 s). Once the app is serving (<see cref="IHostApplicationLifetime.ApplicationStarted"/>), this runs the hot read paths once in
/// the background so the first real visitor does not pay for them.
/// <list type="bullet">
/// <item><description><b>Never delays start-up or <c>/health</c>:</b> nothing runs in <c>StartAsync</c>; the work starts after the
/// server listens, on its own task.</description></item>
/// <item><description><b>Yields to real requests:</b> before every step it waits until no request is in flight
/// (<see cref="RequestActivity"/>, <c>/health</c> excluded); if requests keep coming for <see cref="StartupWarmupOptions.IdleWait"/>, it
/// stops - real traffic is warming the app then.</description></item>
/// <item><description><b>Read-only by construction:</b> every step runs in a scope whose database connection is opened
/// <c>Mode=ReadOnly</c>, so a write would fail rather than happen; the results are thrown away (serialized once with the API's JSON
/// options, which warms the serializer). It never browses into a folder (a folder's Volumes view queues volume-contents scan work), and
/// it reads as an active admin WITHOUT a connected tracker account (so no tracker-mark walk runs) - or does nothing user-scoped when
/// there is none. Nothing it reads is logged: counts, step names and timings only.</description></item>
/// <item><description><b>No network:</b> database reads, and one page extract on the media worker the pool already started at boot
/// (<see cref="MediaWorkerPool.WorkerCount"/> &gt; 0; it never starts one): a generated grey image in a one-page zip in a scratch
/// workspace, encoded as the reader's sized WebP and deleted at once. No source media, no page cache entry.</description></item>
/// </list>
/// Steps: per visible library (at most <see cref="StartupWarmupOptions.MaxLibraries"/>), the root level by name, the second page, the
/// unread filter, the Completion filter and the recently-updated sort, and the root's jump rail; then home (New chapters, Continue
/// reading, favourites), the library view preferences and a search. <see cref="StartupWarmupOptions.Rounds"/> rounds (the second one
/// lets the runtime's tiered JIT optimise what the first one compiled). <c>MangaPixer:Warmup:Enabled=false</c> switches it off.
/// </summary>
public sealed class StartupWarmupOptions
{
    public const string SectionName = "MangaPixer:Warmup";

    public bool Enabled { get; set; } = true;

    /// <summary>Pause after the app started, before the first step (lets the start-up hosted services settle).</summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a step waits for no request in flight before the warm-up stops.</summary>
    public TimeSpan IdleWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The warm-up stops after this long, whatever is left.</summary>
    public TimeSpan Budget { get; set; } = TimeSpan.FromMinutes(5);

    public int Rounds { get; set; } = 2;

    public int MaxLibraries { get; set; } = 12;

    public static StartupWarmupOptions FromConfiguration(IConfiguration configuration, bool testHost)
    {
        var section = configuration.GetSection(SectionName);
        var options = new StartupWarmupOptions
        {
            // A test host (TestHostStorageOverride) leaves it off unless the test switches it on: it must not race the test's own work.
            Enabled = section.GetValue<bool?>("Enabled") ?? !testHost,
        };
        if (section.GetValue<double?>("StartDelaySeconds") is { } delay and >= 0)
            options.StartDelay = TimeSpan.FromSeconds(delay);
        if (section.GetValue<double?>("IdleWaitSeconds") is { } idle and > 0)
            options.IdleWait = TimeSpan.FromSeconds(idle);
        if (section.GetValue<int?>("Rounds") is { } rounds and >= 1 and <= 5)
            options.Rounds = rounds;
        return options;
    }
}

/// <summary>How the warm-up ended (for the log line and tests).</summary>
public enum StartupWarmupOutcome
{
    Disabled,
    Completed,

    /// <summary>Requests kept arriving: real traffic is warming the app; the rest was skipped.</summary>
    Yielded,

    /// <summary>The app stopped, or the budget ran out.</summary>
    Stopped,
}

/// <summary>The warm-up's report: counts and timings only.</summary>
public sealed record StartupWarmupReport(
    StartupWarmupOutcome Outcome, int Steps, int Failed, int Libraries, bool AsUser, string Worker, long ElapsedMs, long WaitedMs);

/// <summary>
/// Requests in flight (all but <c>/health</c>), counted by a middleware at the front of the pipeline (added by an
/// <see cref="IStartupFilter"/>, so no change to the pipeline order of <c>Program.cs</c>).
/// </summary>
public sealed class RequestActivity
{
    private int _inFlight;

    public int InFlight => Volatile.Read(ref _inFlight);

    public void Enter() => Interlocked.Increment(ref _inFlight);

    public void Exit() => Interlocked.Decrement(ref _inFlight);

    /// <summary>Waits until no request is in flight; false when that did not happen within <paramref name="max"/>.</summary>
    public async Task<bool> WaitForIdleAsync(TimeSpan max, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (InFlight > 0)
        {
            if (Stopwatch.GetElapsedTime(started) >= max)
                return false;
            await Task.Delay(25, ct);
        }
        return true;
    }

    internal sealed class StartupFilter(RequestActivity activity) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path.StartsWithSegments("/health"))
                {
                    await nextMiddleware(context);
                    return;
                }
                activity.Enter();
                try
                {
                    await nextMiddleware(context);
                }
                finally
                {
                    activity.Exit();
                }
            });
            next(app);
        };
    }
}

/// <summary>Runs <see cref="StartupWarmup"/> once the app is serving. <see cref="Completion"/> resolves with its report.</summary>
public sealed class StartupWarmupHostedService(
    StartupWarmup warmup, StartupWarmupOptions options, IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly TaskCompletionSource<StartupWarmupReport> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The report of this host's warm-up (tests; resolves once, also when it is switched off).</summary>
    public Task<StartupWarmupReport> Completion => _completion.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            _completion.TrySetResult(new StartupWarmupReport(StartupWarmupOutcome.Disabled, 0, 0, 0, false, "none", 0, 0));
            return;
        }
        try
        {
            // Started = the server listens. Until then this task only waits (StartAsync returned long ago).
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
            using (stoppingToken.Register(() => started.TrySetCanceled(stoppingToken)))
                await started.Task;
            if (options.StartDelay > TimeSpan.Zero)
                await Task.Delay(options.StartDelay, stoppingToken);
            _completion.TrySetResult(await warmup.RunAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            _completion.TrySetResult(new StartupWarmupReport(StartupWarmupOutcome.Stopped, 0, 0, 0, false, "none", 0, 0));
        }
        catch (Exception ex)
        {
            // Never take the host down: the warm-up is an optimisation.
            warmup.LogFailure(ex);
            _completion.TrySetResult(new StartupWarmupReport(StartupWarmupOutcome.Stopped, 0, 1, 0, false, "none", 0, 0));
        }
    }
}

/// <summary>The warm-up's steps (see <see cref="StartupWarmupOptions"/> for what it does and does not do).</summary>
public sealed partial class StartupWarmup(
    IServiceScopeFactory scopes,
    RequestActivity activity,
    StartupWarmupOptions options,
    MediaWorkerPool pool,
    ScratchWorkspaceManager scratch,
    ILogger<StartupWarmup> logger)
{
    /// <summary>The page variant the web reader asks for on a desktop (the 1440 rung of <c>page-variant.ts</c>).</summary>
    private const int WarmPageMaxDimension = 1440;

    public async Task<StartupWarmupReport> RunAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var run = new Run(clock, options.Budget);
        int libraryCount = 0;
        var asUser = false;
        var worker = "none";
        var outcome = StartupWarmupOutcome.Completed;
        try
        {
            var plan = await PlanAsync(run, ct);
            if (plan is null)
                return Finish(run.Ended, run, clock, 0, false, worker);
            asUser = plan.UserId is not null;
            libraryCount = plan.LibraryIds.Count;
            for (var round = 0; round < options.Rounds && outcome == StartupWarmupOutcome.Completed; round++)
            {
                outcome = await RunUserStepsAsync(plan, run, ct);
                // The worker once, after the first round's database work (its process is the same for every round).
                if (round == 0 && outcome == StartupWarmupOutcome.Completed)
                {
                    if (!await run.WaitForTurnAsync(activity, options.IdleWait, ct))
                        outcome = run.Expired ? StartupWarmupOutcome.Stopped : StartupWarmupOutcome.Yielded;
                    else
                        worker = await WarmWorkerAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = StartupWarmupOutcome.Stopped;
        }

        return Finish(outcome, run, clock, libraryCount, asUser, worker);
    }

    private StartupWarmupReport Finish(StartupWarmupOutcome outcome, Run run, Stopwatch clock, int libraryCount, bool asUser, string worker)
    {
        var report = new StartupWarmupReport(outcome, run.Steps, run.Failed, libraryCount, asUser, worker, clock.ElapsedMilliseconds,
            run.WaitedMs);
        logger.LogInformation(LogEvents.Startup.WarmupFinished,
            "Start-up warm-up {Outcome}: {Steps} steps ({Failed} failed) over {Libraries} libraries, worker {Worker}, {ElapsedMs} ms, {WaitedMs} ms waiting for requests",
            outcome, report.Steps, report.Failed, report.Libraries, worker, report.ElapsedMs, report.WaitedMs);
        return report;
    }

    internal void LogFailure(Exception ex) =>
        logger.LogWarning(LogEvents.Startup.WarmupFailed, "Start-up warm-up failed: {Error}", ex.GetType().Name);

    private sealed record Plan(long? UserId, IReadOnlyList<long> LibraryIds);

    /// <summary>The user the steps read as (an active admin, else an active user, without a tracker account) and their libraries.</summary>
    private async Task<Plan?> PlanAsync(Run run, CancellationToken ct)
    {
        Plan plan = new(null, []);
        var ran = await run.StepAsync("plan", activity, options.IdleWait, logger, async () =>
        {
            await using var scope = ReadOnlyScope(out var db);
            var userId = await db.Users.AsNoTracking()
                .Where(u => u.IsActive && !db.TrackerAccounts.Any(a => a.UserId == u.Id))
                .OrderByDescending(u => u.IsAdmin).ThenBy(u => u.Id)
                .Select(u => (long?)u.Id)
                .FirstOrDefaultAsync(ct);
            if (userId is null)
                return;
            var visible = await scope.ServiceProvider.GetRequiredService<LibraryAuthorizationService>()
                .GetVisibleLibraryIdsAsync(userId.Value, incognito: false, ct);
            plan = new Plan(userId, visible.Order().Take(options.MaxLibraries).ToList());
        }, ct);
        return ran ? plan : null;
    }

    private async Task<StartupWarmupOutcome> RunUserStepsAsync(Plan plan, Run run, CancellationToken ct)
    {
        if (plan.UserId is not { } userId)
            return StartupWarmupOutcome.Completed;

        string? searchWord = null;
        foreach (var libraryId in plan.LibraryIds)
        {
            string? nextCursor = null;
            var ok = await run.StepAsync("browse-root", activity, options.IdleWait, logger, async () =>
            {
                var page = await Browse(b => b.BrowseAsync(userId, libraryId, null, null, 50,
                    SortDirection.Ascending, "name", ct: ct));
                nextCursor = page.NextCursor;
                searchWord ??= page.Items.Select(i => WordPattern().Match(i.DisplayName)).FirstOrDefault(m => m.Success)?.Value;
            }, ct);
            ok = ok && await run.StepAsync("browse-root-page2", activity, options.IdleWait, logger, async () =>
            {
                if (nextCursor is not null)
                    await Browse(b => b.BrowseAsync(userId, libraryId, null, nextCursor, 50, SortDirection.Ascending,
                        "name", ct: ct));
            }, ct);
            ok = ok && await run.StepAsync("browse-root-unread", activity, options.IdleWait, logger, () =>
                Browse(b => b.BrowseAsync(userId, libraryId, null, null, 50, SortDirection.Ascending, "name",
                    readState: BrowseReadStateFilter.Unread, ct: ct)), ct);
            ok = ok && await run.StepAsync("browse-root-completion", activity, options.IdleWait, logger, () =>
                Browse(b => b.BrowseAsync(userId, libraryId, null, null, 50, SortDirection.Ascending, "name",
                    completion: SeriesAnswer.MissingSome, ct: ct)), ct);
            ok = ok && await run.StepAsync("browse-root-recent", activity, options.IdleWait, logger, () =>
                Browse(b => b.BrowseAsync(userId, libraryId, null, null, 50, SortDirection.Descending,
                    "recentlyUpdated", preferVolumes: true, ct: ct)), ct);
            ok = ok && await run.StepAsync("jump-rail-root", activity, options.IdleWait, logger, () =>
                InScopeAsync<JumpIndexService, JumpIndexDto>(s => s.GetJumpIndexAsync(userId, libraryId, null, false, null, ct)), ct);
            if (!ok)
                return run.Ended;
        }

        var steps = new (string Name, Func<Task> Body)[]
        {
            ("home-new-chapters", () => InScopeAsync<RecentChaptersService, RecentChaptersDto>(s => s.GetRecentChaptersAsync(userId, ct: ct))),
            ("home-continue", () => InScopeAsync<ReadingStateService, IReadOnlyList<ContinueReadingEntry>>(
                s => s.GetContinueReadingAsync(userId, 12, ct: ct))),
            ("home-favorites", () => InScopeAsync<CatalogBrowseService, PageResponse<CatalogNodeDto>>(
                s => s.ListFavoritesAsync(userId, pageSize: 12, ct: ct))),
            ("library-preferences", () => InScopeAsync<ReadingStateService, LibraryViewPreferencesDto>(
                s => s.GetLibraryPreferencesAsync(userId, ct))),
            ("search", () => searchWord is null
                ? Task.CompletedTask
                : InScopeAsync<CatalogBrowseService, SearchResultsDto>(s => s.SearchAsync(userId, searchWord, ct: ct))),
        };
        foreach (var (name, body) in steps)
        {
            if (!await run.StepAsync(name, activity, options.IdleWait, logger, body, ct))
                return run.Ended;
        }
        return StartupWarmupOutcome.Completed;
    }

    /// <summary>Runs one service call in a fresh read-only scope and serializes its answer like the API would (then drops it).</summary>
    private async Task<TResult> InScopeAsync<TService, TResult>(Func<TService, Task<TResult>> call) where TService : notnull
    {
        await using var scope = ReadOnlyScope(out _);
        var result = await call(scope.ServiceProvider.GetRequiredService<TService>());
        var json = scope.ServiceProvider.GetService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()?.Value.JsonSerializerOptions;
        if (json is not null && result is not null)
            _ = JsonSerializer.SerializeToUtf8Bytes(result, result.GetType(), json);
        return result;
    }

    private Task<PageResponse<CatalogNodeDto>> Browse(Func<CatalogBrowseService, Task<PageResponse<CatalogNodeDto>>> call) =>
        InScopeAsync(call);

    /// <summary>
    /// A scope whose <see cref="MangaPixerDbContext"/> (shared by every scoped service resolved from it) opens its connection read-only:
    /// a write through it fails instead of happening.
    /// </summary>
    internal AsyncServiceScope ReadOnlyScope(out MangaPixerDbContext db)
    {
        var scope = scopes.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var readOnly = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()) { Mode = SqliteOpenMode.ReadOnly };
        db.Database.SetConnectionString(readOnly.ToString());
        return scope;
    }

    /// <summary>
    /// One sized-WebP extract of a generated page on a worker the pool already runs (see the class remarks). Returns "warm", "skipped"
    /// (no worker running) or the extract's error code.
    /// </summary>
    internal async Task<string> WarmWorkerAsync(CancellationToken ct)
    {
        if (pool.WorkerCount == 0)
            return "skipped";
        var workspace = scratch.AllocateWorkspace();
        try
        {
            var archive = Path.Combine(workspace.Path, "warmup.cbz");
            await using (var file = File.Create(archive))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                await using var entry = zip.CreateEntry("001.png", CompressionLevel.Fastest).Open();
                await entry.WriteAsync(GreyPng(1200, 1800), ct);
            }
            var info = new FileInfo(archive);
            var outcome = await pool.ExtractPageAsync(archive, "001.png", $"webp@{WarmPageMaxDimension}:balanced", info.LastWriteTimeUtc.Ticks,
                info.Length, Path.Combine(workspace.Path, "out.webp"), thumbnailMaxDimension: 320, webpQuality: 82, ct,
                maxDimension: WarmPageMaxDimension, resizeFilter: "balanced", slotKind: MediaSlotKind.Thumbnail);
            return outcome.Success ? "warm" : outcome.ErrorType ?? "failed";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(LogEvents.Startup.WarmupStepFailed, "Start-up warm-up step {Step} failed: {Error}", "worker", ex.GetType().Name);
            return "failed";
        }
        finally
        {
            scratch.CleanupWorkspace(workspace.Path);
        }
    }

    /// <summary>A grey 8-bit grayscale PNG (taller than the 1440 rung, so the worker's resize path runs too).</summary>
    internal static byte[] GreyPng(int width, int height)
    {
        var raw = new byte[(width + 1) * height];
        for (var y = 0; y < height; y++)
            Array.Fill(raw, (byte)0x80, y * (width + 1) + 1, width); // filter byte 0 (none), then the row
        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth; colour type 0 (grey), compression 0, filter 0, no interlace
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, idat.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();

        static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> u32 = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(u32, data.Length);
            s.Write(u32);
            s.Write(type);
            s.Write(data);
            var crc = Crc32(Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
            BinaryPrimitives.WriteUInt32BigEndian(u32, crc);
            s.Write(u32);
        }

        static uint Crc32(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            return crc;
        }
    }

    [GeneratedRegex(@"\p{L}{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    /// <summary>Step bookkeeping: counts, the time spent waiting for requests, the budget.</summary>
    private sealed class Run(Stopwatch clock, TimeSpan budget)
    {
        public int Steps { get; private set; }
        public int Failed { get; private set; }
        public long WaitedMs { get; private set; }
        public bool Expired => clock.Elapsed >= budget;

        /// <summary>The outcome when a step could not run: out of budget, or requests kept arriving.</summary>
        public StartupWarmupOutcome Ended => Expired ? StartupWarmupOutcome.Stopped : StartupWarmupOutcome.Yielded;

        public async Task<bool> WaitForTurnAsync(RequestActivity activity, TimeSpan idleWait, CancellationToken ct)
        {
            if (Expired)
                return false;
            var waitStarted = clock.ElapsedMilliseconds;
            var idle = await activity.WaitForIdleAsync(idleWait, ct);
            WaitedMs += clock.ElapsedMilliseconds - waitStarted;
            return idle && !Expired;
        }

        /// <summary>Waits for its turn, then runs one step; a failing step is counted and skipped. False: stop here.</summary>
        public async Task<bool> StepAsync(string name, RequestActivity activity, TimeSpan idleWait, ILogger logger, Func<Task> body,
            CancellationToken ct)
        {
            if (!await WaitForTurnAsync(activity, idleWait, ct))
                return false;
            Steps++;
            try
            {
                await body();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Failed++;
                logger.LogDebug(LogEvents.Startup.WarmupStepFailed, "Start-up warm-up step {Step} failed: {Error}", name, ex.GetType().Name);
            }
            return true;
        }
    }
}

/// <summary>DI registration of the start-up warm-up.</summary>
public static class StartupWarmupServicesExtensions
{
    public static IServiceCollection AddStartupWarmup(this IServiceCollection services)
    {
        // Read here, inside Program.Main's synchronous boot, where a test host's storage override is visible.
        var testHost = TestHostStorageOverride.Current is not null;
        services.AddSingleton(sp => StartupWarmupOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>(), testHost));
        services.AddSingleton<RequestActivity>();
        services.AddTransient<IStartupFilter, RequestActivity.StartupFilter>();
        services.AddSingleton<StartupWarmup>();
        services.AddSingleton<StartupWarmupHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<StartupWarmupHostedService>());
        return services;
    }
}
