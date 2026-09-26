namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using System.Net;
using System.Net.Http.Headers;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of <see cref="MetadataGateway"/> (1.24.0, lane B2): the
/// gates in their approved order, each refusal making ZERO handler calls; the
/// persisted budget (day rollover) and backoff (Retry-After seconds/date, ladder,
/// 5xx streak, survives a "restart" = a fresh gateway over the same database); the
/// token bucket; the post-call switch re-check; the search cache; and that a
/// sentinel query never appears in any log line.
/// </summary>
public sealed class MetadataGatewayTests : IAsyncLifetime
{
    private const string Mu = "mangaupdates";
    private MetadataTestDb _db = null!;
    private GatewayHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new GatewayHarness(_db);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private async Task<MetadataGatewayException> RefusedAsync(Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(call);
        return ex;
    }

    // --- Gates, in order; a refusal makes zero calls ---

    [Fact]
    public async Task FreshInstall_EverythingOff_RefusesWithZeroCalls()
    {
        var gateway = _h.Gateway();
        var ex = await RefusedAsync(() => gateway.SearchAsync(Mu, _db.LibraryId, "Berserk", 1));
        Assert.Equal(409, ex.HttpStatus);
        Assert.Equal("metadata_disabled", ex.Code);
        await RefusedAsync(() => gateway.GetSeriesAsync(Mu, _db.LibraryId, "51239621230"));
        await RefusedAsync(() => gateway.FetchImageAsync(Mu, _db.LibraryId, "https://cdn.mangaupdates.com/image/i1.png"));
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task ConfigHardKill_WinsOverTheUi()
    {
        using var h = new GatewayHarness(_db, config: new Dictionary<string, string?> { ["Metadata:NetworkDisabled"] = "true" });
        await h.EnableAsync();
        var ex = await RefusedAsync(() => h.Gateway().SearchAsync(Mu, _db.LibraryId, "Berserk", 1));
        Assert.Equal("metadata_network_disabled", ex.Code);
        Assert.Equal(0, h.Handler.CallCount);
    }

    [Fact]
    public async Task StaleConsentVersion_Refuses()
    {
        await _h.EnableAsync(consentVersion: MetadataConsent.CurrentVersion - 1);
        var ex = await RefusedAsync(() => _h.Gateway().SearchAsync(Mu, _db.LibraryId, "Berserk", 1));
        Assert.Equal("metadata_disabled", ex.Code);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task LibrarySwitchOff_Refuses_OtherLibraryStillWorks()
    {
        var other = await _db.AddLibraryAsync("otherlib", "Other");
        await _h.EnableAsync(library: false);
        var ex = await RefusedAsync(() => _h.Gateway().SearchAsync(Mu, _db.LibraryId, "Berserk", 1));
        Assert.Equal(409, ex.HttpStatus);
        Assert.Equal("library_metadata_disabled", ex.Code);
        Assert.Equal(0, _h.Handler.CallCount);

        await _h.EnableAsync(other.Id);
        var page = await _h.Gateway().SearchAsync(Mu, other.Id, "Berserk", 1);
        Assert.Equal(5, page.Hits.Count);
        Assert.Equal(1, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Enabled_Search_CountsBudget_AndCachesByNormalizedQuery()
    {
        await _h.EnableAsync();
        var gateway = _h.Gateway();
        await gateway.SearchAsync(Mu, _db.LibraryId, "  Berserk ", 1);
        await gateway.SearchAsync(Mu, _db.LibraryId, "berserk", 1); // cache hit: same normalized query
        Assert.Equal(1, _h.Handler.CallCount);
        Assert.Equal(1, (await _h.Budget().GetAsync()).Used);

        await gateway.SearchAsync(Mu, _db.LibraryId, "Berserk", 2); // another page is another request
        await gateway.SearchAsync(Mu, _db.LibraryId, "Berserk", 2, hideDoujinshiAndNovels: true); // another filter too
        Assert.Equal(3, _h.Handler.CallCount);
        Assert.Equal("Berserk", System.Text.Json.JsonDocument.Parse(_h.Handler.Seen[0].Body!).RootElement.GetProperty("search").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyOrOversizedQuery_Is400_NoCall(string query)
    {
        await _h.EnableAsync();
        var ex = await RefusedAsync(() => _h.Gateway().SearchAsync(Mu, _db.LibraryId, query, 1));
        Assert.Equal("invalid_query", ex.Code);
        ex = await RefusedAsync(() => _h.Gateway().SearchAsync(Mu, _db.LibraryId, new string('a', 201), 1));
        Assert.Equal("invalid_query", ex.Code);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    // --- Budget ---

    [Fact]
    public async Task Budget_Exhausted_Is429_ZeroCalls_AndResetsAtUtcMidnight()
    {
        await _h.EnableAsync();
        var row = await _db.Db.AppSettings.FirstAsync();
        row.MetadataDailyBudget = 2;
        await _db.Db.SaveChangesAsync();

        var gateway = _h.Gateway();
        await gateway.GetSeriesAsync(Mu, _db.LibraryId, "51239621230");
        await gateway.GetSeriesAsync(Mu, _db.LibraryId, "15180124327");
        var ex = await RefusedAsync(() => gateway.GetSeriesAsync(Mu, _db.LibraryId, "51239621230"));
        Assert.Equal(429, ex.HttpStatus);
        Assert.Equal("budget_exhausted", ex.Code);
        Assert.Equal(2, _h.Handler.CallCount);

        // Persisted: a fresh gateway (= a restart) is still out of budget.
        await RefusedAsync(() => _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "51239621230"));
        Assert.Equal(2, _h.Handler.CallCount);

        _h.Time.Now = new DateTimeOffset(2026, 9, 26, 0, 0, 1, TimeSpan.Zero);
        await _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "51239621230");
        Assert.Equal(3, _h.Handler.CallCount);
        var settings = await _h.ReadSettingsAsync();
        Assert.Equal(1, settings.MetadataBudgetUsed);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero), settings.MetadataBudgetDayUtc);
    }

    // --- Token bucket ---

    [Fact]
    public async Task TokenBucket_Overflow_IsProviderBusy_ZeroCalls()
    {
        using var h = new GatewayHarness(_db, GatewayHarness.SingleTokenRates());
        await h.EnableAsync();
        await h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "51239621230");
        var ex = await RefusedAsync(() => h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "15180124327"));
        Assert.Equal(429, ex.HttpStatus);
        Assert.Equal("provider_busy", ex.Code);
        Assert.Equal(1, h.Handler.CallCount);
        Assert.Equal(1, (await h.Budget().GetAsync()).Used); // a refused call spends no budget
    }

    [Fact]
    public void DefaultRates_AreTheApprovedNumbers()
    {
        var o = new MetadataRateLimitOptions();
        Assert.Equal(2, o.Api.TokenLimit);
        Assert.Equal(2.0, o.Api.TokensPerPeriod / o.Api.ReplenishmentPeriod.TotalSeconds);
        Assert.Equal(5.0, o.Images.TokensPerPeriod / o.Images.ReplenishmentPeriod.TotalSeconds);
        Assert.Equal(10, o.Api.QueueLimit);
    }

    // --- Backoff ---

    [Fact]
    public async Task RateLimited_HonoursRetryAfterSeconds_Persisted_AcrossRestart()
    {
        await _h.EnableAsync();
        _h.Handler.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return r;
        };
        var ex = await RefusedAsync(() => _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "51239621230"));
        Assert.Equal(503, ex.HttpStatus);
        Assert.Equal("provider_backoff", ex.Code);
        Assert.Equal(_h.Time.Now.AddSeconds(90), ex.RetryAt);

        var settings = await _h.ReadSettingsAsync();
        Assert.Equal(_h.Time.Now.AddSeconds(90), settings.MetadataBackoffUntil);
        Assert.Equal("rate_limited", settings.MetadataLastErrorCode);

        // "Restart": new gateway, same database -> still backing off, no call.
        _h.Handler.Respond = null;
        var again = await RefusedAsync(() => _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "51239621230"));
        Assert.Equal("provider_backoff", again.Code);
        Assert.Equal(1, _h.Handler.CallCount);

        _h.Time.Advance(TimeSpan.FromSeconds(91));
        Assert.NotNull(await _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "51239621230"));
        Assert.Equal(0, (await _h.ReadSettingsAsync()).MetadataBackoffStep); // success resets the ladder
    }

    [Fact]
    public async Task RetryAfterDate_IsHonoured_AndCappedAtOneHour()
    {
        await _h.EnableAsync();
        _h.Handler.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(_h.Time.Now.AddHours(5));
            return r;
        };
        var ex = await RefusedAsync(() => _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "1"));
        Assert.Equal(_h.Time.Now.AddHours(1), ex.RetryAt);
        Assert.Equal("http_503", (await _h.ReadSettingsAsync()).MetadataLastErrorCode);
    }

    [Fact]
    public async Task RateLimited_WithoutRetryAfter_ClimbsTheLadder()
    {
        await _h.EnableAsync();
        _h.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var expected = new[] { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(1) };
        foreach (var delay in expected)
        {
            var ex = await RefusedAsync(() => _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "1"));
            Assert.Equal(_h.Time.Now + delay, ex.RetryAt);
            _h.Time.Advance(delay + TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task ThreeConsecutive5xx_BackOffFiveMinutes()
    {
        await _h.EnableAsync();
        _h.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        for (var i = 0; i < 2; i++)
        {
            var ex = await RefusedAsync(() => _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "1"));
            Assert.Equal("provider_error", ex.Code);
            Assert.Null((await _h.ReadSettingsAsync()).MetadataBackoffUntil);
        }
        var third = await RefusedAsync(() => _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "1"));
        Assert.Equal(_h.Time.Now.AddMinutes(5), third.RetryAt);
        Assert.Equal(_h.Time.Now.AddMinutes(5), (await _h.ReadSettingsAsync()).MetadataBackoffUntil);
        Assert.Equal("http_5xx", (await _h.ReadSettingsAsync()).MetadataLastErrorCode);

        var refused = await RefusedAsync(() => _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "1"));
        Assert.Equal("provider_backoff", refused.Code);
        Assert.Equal(3, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Timeout_IsTypedAndCountsTowardTheStreak()
    {
        await _h.EnableAsync();
        _h.Handler.Respond = _ => throw new TaskCanceledException("simulated timeout", new TimeoutException());
        var ex = await RefusedAsync(() => _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "1"));
        Assert.Equal(504, ex.HttpStatus);
        Assert.Equal("provider_timeout", ex.Code);
        Assert.Equal("timeout", (await _h.ReadSettingsAsync()).MetadataLastErrorCode);
    }

    [Fact]
    public async Task Redirect_IsRefused_NotFollowed()
    {
        await _h.EnableAsync();
        _h.Handler.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            r.Headers.Location = new Uri("https://elsewhere.example/");
            return r;
        };
        var ex = await RefusedAsync(() => _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "1"));
        Assert.Equal("redirect_refused", ex.Code);
        Assert.Single(_h.Handler.Seen);
    }

    // --- Images ---

    [Fact]
    public async Task Image_NotOnAllowlist_IsRefused_ZeroCalls()
    {
        await _h.EnableAsync();
        foreach (var url in new[] { "https://evil.example/i.png", "http://cdn.mangaupdates.com/i.png", "https://api.mangaupdates.com/i.png", "not a url" })
        {
            var ex = await RefusedAsync(() => _h.Gateway().FetchImageAsync(Mu, _db.LibraryId, url));
            Assert.Equal("host_not_allowed", ex.Code);
        }
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Image_WithoutImageMagicBytes_IsRejected()
    {
        await _h.EnableAsync();
        _h.Handler.Respond = _ => ScriptedHandler.Bytes("<svg onload=alert(1)>"u8.ToArray(), "image/png");
        var ex = await RefusedAsync(() => _h.Gateway().FetchImageAsync(Mu, _db.LibraryId, "https://cdn.mangaupdates.com/image/i1.png"));
        Assert.Equal("not_an_image", ex.Code);

        _h.Handler.Respond = null;
        var bytes = await _h.Gateway().FetchImageAsync(Mu, _db.LibraryId, "https://cdn.mangaupdates.com/image/i1.png");
        Assert.Equal(MuFixtures.Png, bytes);
        Assert.Equal(2, (await _h.Budget().GetAsync()).Used); // images count against the budget too
    }

    // --- Kill switch during a call ---

    [Fact]
    public async Task SwitchTurnedOffDuringCall_ResultIsDropped()
    {
        await _h.EnableAsync();
        _h.Handler.Respond = _ =>
        {
            // The admin turns the global switch off while the request is in flight.
            using var db2 = new com.lifepixer.mangapixer.Server.Persistence.MangaPixerDbContext(
                new DbContextOptionsBuilder<com.lifepixer.mangapixer.Server.Persistence.MangaPixerDbContext>()
                    .UseSqlite(_db.Db.Database.GetConnectionString()!).Options);
            db2.AppSettings.Where(s => s.Id == AppSettingsEntity.SingletonId)
                .ExecuteUpdate(s => s.SetProperty(x => x.MetadataEnabled, false));
            return ScriptedHandler.Json(MuFixtures.Load("berserk-get"));
        };
        var ex = await RefusedAsync(() => _h.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "51239621230"));
        Assert.Equal("metadata_disabled", ex.Code);
        Assert.Equal(1, _h.Handler.CallCount);
    }

    // --- Privacy: logs ---

    [Fact]
    public async Task Logs_NeverContainTheQuery_TitlesOrUrls()
    {
        const string sentinel = "Sentinel Qx7 Private Folder Name";
        await _h.EnableAsync();
        var gateway = _h.Gateway();
        await gateway.SearchAsync(Mu, _db.LibraryId, sentinel, 1);
        await gateway.SearchAsync(Mu, _db.LibraryId, "Berserk " + sentinel, 1);
        await gateway.GetSeriesAsync(Mu, _db.LibraryId, "51239621230");
        await gateway.FetchImageAsync(Mu, _db.LibraryId, "https://cdn.mangaupdates.com/image/i501230.png");
        _h.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await RefusedAsync(() => gateway.SearchAsync(Mu, _db.LibraryId, sentinel + " again", 1));

        var lines = _h.Logs.Lines;
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("search", StringComparison.Ordinal));
        foreach (var line in lines)
        {
            Assert.DoesNotContain("Sentinel", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Qx7", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Berserk", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("://", line, StringComparison.Ordinal);
            Assert.DoesNotContain("mangaupdates.com", line, StringComparison.OrdinalIgnoreCase);
        }
    }
}
