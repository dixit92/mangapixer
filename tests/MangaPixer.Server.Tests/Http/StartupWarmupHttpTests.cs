namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (real host) for the start-up warm-up (1.46.0 lane D, <see cref="StartupWarmup"/>): it runs once the app serves, reads as
/// an active user without a tracker account, changes nothing (its scope cannot write), yields while a request is in flight, logs counts
/// only, and is off in test hosts unless a test switches it on.
/// </summary>
public sealed class StartupWarmupHttpTests
{
    private static readonly Dictionary<string, string?> On = new()
    {
        ["MangaPixer:Warmup:Enabled"] = "true",
        ["MangaPixer:Warmup:StartDelaySeconds"] = "0",
    };

    private static async Task<StartupWarmupReport> BootReportAsync(ApiTokenTestFactory factory) =>
        await factory.Services.GetRequiredService<StartupWarmupHostedService>().Completion.WaitAsync(TimeSpan.FromSeconds(60));

    [Fact]
    public async Task OnBoot_ItRunsAfterTheAppStarted_AndHealthAnswers()
    {
        using var factory = new ApiTokenTestFactory(On);

        // A fresh instance has no user: nothing user-scoped to warm, and the test host runs no media worker.
        var report = await BootReportAsync(factory);
        Assert.Equal(StartupWarmupOutcome.Completed, report.Outcome);
        Assert.False(report.AsUser);
        Assert.Equal("skipped", report.Worker);
        Assert.Equal(0, report.Failed);

        var health = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task TestHosts_LeaveItOff_UnlessSwitchedOn()
    {
        using var factory = new ApiTokenTestFactory();
        Assert.Equal(StartupWarmupOutcome.Disabled, (await BootReportAsync(factory)).Outcome);
    }

    [Fact]
    public async Task WithALibrary_ItRunsEveryStep_ReadOnly_WithTheSameAnswersAfterwards_AndLogsCountsOnly()
    {
        using var factory = new ApiTokenTestFactory(On);
        await BootReportAsync(factory);
        var admin = await factory.AdminAsync();
        var (libraryPublicId, _) = await SeedLibraryAsync(factory, "Warmlib Alpha", "Warmseries Bravo");

        var browse = $"/api/v1/libraries/{libraryPublicId}/browse?pageSize=50&sort=name&direction=asc";
        var before = new[]
        {
            await admin.GetStringAsync(browse),
            await admin.GetStringAsync("/api/v1/home/recent-chapters"),
            await admin.GetStringAsync("/api/v1/libraries"),
        };

        var report = await factory.Services.GetRequiredService<StartupWarmup>().RunAsync(CancellationToken.None);

        Assert.Equal(StartupWarmupOutcome.Completed, report.Outcome);
        Assert.True(report.AsUser);
        Assert.Equal(1, report.Libraries);
        // Every step ran and none failed - a step that tried to write would have failed on the read-only connection.
        Assert.Equal(0, report.Failed);
        Assert.Equal(1 + 2 * (6 + 5), report.Steps); // plan + 2 rounds x (6 per library + 5 home / search steps)

        var after = new[]
        {
            await admin.GetStringAsync(browse),
            await admin.GetStringAsync("/api/v1/home/recent-chapters"),
            await admin.GetStringAsync("/api/v1/libraries"),
        };
        Assert.Equal(before, after);

        var log = factory.Sink.Events.Where(e => e.Properties.TryGetValue("EventId", out var id) && id.ToString().Contains(
            LogEvents.Startup.WarmupFinished.ToString(), StringComparison.Ordinal)).Select(e => e.RenderMessage()).ToList();
        Assert.Equal(2, log.Count); // the boot run and this one
        Assert.DoesNotContain(log, l => l.Contains("Warmlib", StringComparison.Ordinal) || l.Contains("Warmseries", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItReadsAsAUserWithoutATrackerAccount()
    {
        using var factory = new ApiTokenTestFactory(On);
        await BootReportAsync(factory);
        await factory.AdminAsync();
        await SeedLibraryAsync(factory, "Lib One", "Series One");
        var (_, granted) = await SeedLibraryAsync(factory, "Lib Two", "Series Two");
        await factory.CreateUserAsync("reader1", "ReaderPass123!", isAdmin: false);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var adminId = db.Users.Single(u => u.NormalizedUserName == "ADMIN").Id;
            var readerId = db.Users.Single(u => u.NormalizedUserName == "READER1").Id;
            db.TrackerAccounts.Add(new TrackerAccountEntity { UserId = adminId, Tracker = "mangaupdates", CreatedAt = DateTimeOffset.UtcNow });
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = readerId, LibraryId = granted, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var report = await factory.Services.GetRequiredService<StartupWarmup>().RunAsync(CancellationToken.None);

        // The admin (both libraries) has a tracker account: the reader (one granted library) is the one the warm-up reads as.
        Assert.Equal(StartupWarmupOutcome.Completed, report.Outcome);
        Assert.True(report.AsUser);
        Assert.Equal(1, report.Libraries);
        Assert.Equal(0, report.Failed);
    }

    [Fact]
    public async Task ItYields_WhileARequestIsInFlight()
    {
        using var factory = new ApiTokenTestFactory(new Dictionary<string, string?>(On) { ["MangaPixer:Warmup:IdleWaitSeconds"] = "1" });
        await BootReportAsync(factory);
        var activity = factory.Services.GetRequiredService<RequestActivity>();
        var warmup = factory.Services.GetRequiredService<StartupWarmup>();

        activity.Enter();
        try
        {
            var report = await warmup.RunAsync(CancellationToken.None);
            Assert.Equal(StartupWarmupOutcome.Yielded, report.Outcome);
            Assert.Equal(0, report.Steps);
            Assert.True(report.WaitedMs >= 900, $"waited {report.WaitedMs} ms");
        }
        finally
        {
            activity.Exit();
        }

        Assert.Equal(StartupWarmupOutcome.Completed, (await warmup.RunAsync(CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task RequestsInFlight_CountApiRequests_NotHealth()
    {
        var seen = new List<(string Path, int InFlight)>();
        using var factory = new ApiTokenTestFactory(configureServices: services =>
            services.AddSingleton<IStartupFilter>(sp => new ProbeFilter(sp.GetRequiredService<RequestActivity>(), seen)));
        var client = factory.CreateClient();

        await client.GetAsync("/health");
        await client.GetAsync("/api/v1/auth/csrf");

        lock (seen)
        {
            Assert.Contains(("/health", 0), seen);
            Assert.Contains(("/api/v1/auth/csrf", 1), seen);
        }
        Assert.Equal(0, factory.Services.GetRequiredService<RequestActivity>().InFlight);
    }

    [Fact]
    public async Task ItsScope_CannotWrite_AndOtherScopesStillCan()
    {
        using var factory = new ApiTokenTestFactory(On);
        await BootReportAsync(factory);
        await SeedLibraryAsync(factory, "Lib One", "Series One");
        var warmup = factory.Services.GetRequiredService<StartupWarmup>();

        await using (var scope = warmup.ReadOnlyScope(out var readOnly))
        {
            Assert.Equal(1, await readOnly.Libraries.CountAsync());
            var refused = await Assert.ThrowsAsync<SqliteException>(() =>
                readOnly.Database.ExecuteSqlRawAsync("UPDATE libraries SET DisplayName = 'changed'"));
            Assert.Equal(8, refused.SqliteErrorCode); // SQLITE_READONLY
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync("UPDATE libraries SET DisplayName = DisplayName"));
        }
    }

    /// <summary>One library with one series folder of three archives (added now, so they are New chapters too). Returns its ids.</summary>
    private static async Task<(string PublicId, long Id)> SeedLibraryAsync(ApiTokenTestFactory factory, string libraryName, string seriesName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var now = DateTimeOffset.UtcNow;
        var key = Guid.NewGuid().ToString("N")[..8];
        var library = new LibraryEntity { PublicId = "wl" + key, DisplayName = libraryName, RootPath = "/synthetic/" + key, CreatedAt = now };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var series = Node(library.Id, null, "ws" + key, CatalogNodeKind.Folder, seriesName, now);
        db.CatalogNodes.Add(series);
        await db.SaveChangesAsync();
        for (var i = 1; i <= 3; i++)
            db.CatalogNodes.Add(Node(library.Id, series.Id, $"wa{key}{i}", CatalogNodeKind.Archive, $"{seriesName} {i:D3}.cbz", now.AddMinutes(-i)));
        await db.SaveChangesAsync();
        return (library.PublicId, library.Id);
    }

    private static CatalogNodeEntity Node(long libraryId, long? parentId, string publicId, CatalogNodeKind kind, string name,
        DateTimeOffset createdAt) => new()
        {
            PublicId = publicId,
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = (int)kind,
            DisplayName = name,
            RelativePath = name,
            PathKey = publicId,
            SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name.ToLowerInvariant(),
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = createdAt,
        };

    /// <summary>Records <see cref="RequestActivity.InFlight"/> inside the pipeline (registered after, so inside, the counting filter).</summary>
    private sealed class ProbeFilter(RequestActivity activity, List<(string Path, int InFlight)> seen) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                lock (seen)
                    seen.Add((context.Request.Path.Value ?? "", activity.InFlight));
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
