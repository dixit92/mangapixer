namespace com.lifepixer.mangapixer.Server.Tests.Features.Updates;

using System.Net;
using System.Text;
using com.lifepixer.mangapixer.Server.Features.Updates;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for <see cref="UpdateCheckService"/>: opt-in persistence
/// and the 24h cadence gate. The GitHub call is always mocked via an injected
/// <see cref="HttpMessageHandler"/> — no test ever touches api.github.com — and a
/// controllable clock drives the cadence assertions deterministically.
/// </summary>
public sealed class UpdateCheckServiceTests : IDisposable
{
    private readonly string _dir;

    public UpdateCheckServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-updchk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private MangaPixerDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .ConfigureSqlite(Path.Combine(_dir, name))
            .Options;
        var db = new MangaPixerDbContext(options);
        db.Database.Migrate();
        return db;
    }

    private static UpdateCheckService NewService(
        MangaPixerDbContext db, CountingHandler handler, MutableClock clock, string current = "1.21.0")
        => new(db, new StubHttpClientFactory(handler), clock, logger: null, currentVersionOverride: current);

    [Fact]
    public async Task Enabling_persists_flag_and_runs_first_check()
    {
        var dbPath = "persist.db";
        var handler = CountingHandler.ReturningTag("v1.99.0");
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        await using (var db = NewContext(dbPath))
        {
            var svc = NewService(db, handler, clock);
            var status = await svc.SetEnabledAsync(true);

            Assert.True(status.Enabled);
            Assert.Equal("1.21.0", status.CurrentVersion);
            Assert.Equal("1.99.0", status.LatestVersion);
            Assert.True(status.UpdateAvailable);
            Assert.NotNull(status.LastChecked);
        }

        // Enabling triggered exactly one outbound call.
        Assert.Equal(1, handler.CallCount);

        // The flag and cached version survive into a fresh context (real persistence).
        await using (var db = NewContext(dbPath))
        {
            var row = await db.AppSettings.SingleAsync();
            Assert.Equal(AppSettingsEntity.SingletonId, row.Id);
            Assert.True(row.UpdateCheckEnabled);
            Assert.Equal("1.99.0", row.UpdateLastKnownLatestVersion);
        }
    }

    [Fact]
    public async Task Disabled_never_calls_out()
    {
        var handler = CountingHandler.ReturningTag("v1.99.0");
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        await using var db = NewContext("disabled.db");
        var svc = NewService(db, handler, clock);

        var status = await svc.GetStatusAsync(force: false);

        Assert.False(status.Enabled);
        Assert.Null(status.LatestVersion);
        Assert.False(status.UpdateAvailable);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Cadence_gate_limits_automatic_checks_to_once_per_24h()
    {
        var handler = CountingHandler.ReturningTag("v1.99.0");
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        await using var db = NewContext("cadence.db");

        // Enable directly (bypass the enable-triggered check by seeding the row)
        // so this test isolates the automatic-check cadence on GetStatusAsync.
        db.AppSettings.Add(new AppSettingsEntity { Id = AppSettingsEntity.SingletonId, UpdateCheckEnabled = true });
        await db.SaveChangesAsync();

        var svc = NewService(db, handler, clock);

        // First status: no prior check → runs one.
        await svc.GetStatusAsync(force: false);
        Assert.Equal(1, handler.CallCount);

        // Immediately again, well within 24h → gate blocks the call.
        await svc.GetStatusAsync(force: false);
        Assert.Equal(1, handler.CallCount);

        // 23h later → still inside the window.
        clock.Advance(TimeSpan.FromHours(23));
        await svc.GetStatusAsync(force: false);
        Assert.Equal(1, handler.CallCount);

        // Past 24h → a new automatic check runs.
        clock.Advance(TimeSpan.FromHours(1));
        await svc.GetStatusAsync(force: false);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Force_bypasses_the_cadence_gate()
    {
        var handler = CountingHandler.ReturningTag("v1.99.0");
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        await using var db = NewContext("force.db");
        db.AppSettings.Add(new AppSettingsEntity { Id = AppSettingsEntity.SingletonId, UpdateCheckEnabled = true });
        await db.SaveChangesAsync();

        var svc = NewService(db, handler, clock);

        await svc.GetStatusAsync(force: false);
        Assert.Equal(1, handler.CallCount);

        // No clock movement, but force=true still checks (the "Check now" action).
        await svc.GetStatusAsync(force: true);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Failed_check_degrades_gracefully_and_still_advances_cadence()
    {
        var handler = CountingHandler.ReturningStatus(HttpStatusCode.ServiceUnavailable);
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        await using var db = NewContext("fail.db");
        db.AppSettings.Add(new AppSettingsEntity { Id = AppSettingsEntity.SingletonId, UpdateCheckEnabled = true });
        await db.SaveChangesAsync();

        var svc = NewService(db, handler, clock);

        // The call fails but does not throw; status still returns.
        var status = await svc.GetStatusAsync(force: false);
        Assert.Null(status.LatestVersion);
        Assert.False(status.UpdateAvailable);
        Assert.NotNull(status.LastChecked);   // timestamp advanced despite failure
        Assert.Equal(1, handler.CallCount);

        // The advanced timestamp means the gate now suppresses the next auto-check.
        await svc.GetStatusAsync(force: false);
        Assert.Equal(1, handler.CallCount);
    }

    // --- Test doubles ---------------------------------------------------------

    /// <summary>A controllable clock (no external FakeTimeProvider dependency).</summary>
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>Counts requests and returns a canned response — never hits the network.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;
        public int CallCount { get; private set; }

        private CountingHandler(Func<HttpResponseMessage> factory) => _factory = factory;

        public static CountingHandler ReturningTag(string tag) => new(() =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"tag_name\":\"{tag}\"}}", Encoding.UTF8, "application/json"),
            });

        public static CountingHandler ReturningStatus(HttpStatusCode status) => new(() =>
            new HttpResponseMessage(status));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_factory());
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        // Do not dispose the shared handler between calls (tests reuse it to count).
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
