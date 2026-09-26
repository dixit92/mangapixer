namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the gateway's AUTOMATIC origin (stage 2): each refusal
/// (switch off, stale automatic consent, library Fetch off, budget spent, backoff)
/// makes ZERO calls; interactive identify is unaffected by the automatic switch;
/// automatic searches always carry the fixed type filter (doujinshi lifted only on
/// request); automatic calls wait for a token instead of failing "provider_busy";
/// a 429 during automatic work pauses interactive calls too; request counting.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class AutomaticGatewayTests : IAsyncLifetime
{
    private const string Mu = "mangaupdates";
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
        _h.Search["Alpha Saga"] = [new MuJson.Hit(101, "Alpha Saga")];
        _h.Records[101] = MuJson.Get(101, "Alpha Saga");
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private static MetadataCallContext Auto() => MetadataCallContext.Automatic();

    [Fact]
    public async Task AutomaticSwitchOff_RefusesAutomatic_ZeroCalls_InteractiveStillWorks()
    {
        await _h.EnableAutomaticAsync(automatic: false);
        var gateway = _h.Net.Gateway();
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway.SearchAutomaticAsync(Mu, _db.LibraryId, "Alpha Saga", false, Auto()));
        Assert.Equal(409, ex.HttpStatus);
        Assert.Equal("automatic_off", ex.Code);
        await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway.GetSeriesAsync(Mu, _db.LibraryId, "101", default, Auto()));
        await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway.FetchImageAsync(Mu, _db.LibraryId, "https://cdn.mangaupdates.com/image/i1.png", default, Auto()));
        Assert.Equal(0, _h.Handler.CallCount);

        // Manual identify keeps working under the v1 consent (owner decision 2).
        Assert.NotNull(await gateway.GetSeriesAsync(Mu, _db.LibraryId, "101"));
        Assert.Equal(1, _h.Handler.CallCount);
    }

    [Fact]
    public async Task StaleAutomaticConsent_Refuses()
    {
        await _h.EnableAutomaticAsync(autoConsentVersion: MetadataAutoConsent.CurrentVersion - 1);
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Net.Gateway().SearchAutomaticAsync(Mu, _db.LibraryId, "Alpha Saga", false, Auto()));
        Assert.Equal("automatic_off", ex.Code);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task LibraryFetchOff_RefusesAutomatic()
    {
        await _h.EnableAutomaticAsync();
        var lib = await _db.Db.Libraries.FirstAsync(l => l.Id == _db.LibraryId);
        lib.MetadataEnabled = false;
        await _db.Db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Net.Gateway().SearchAutomaticAsync(Mu, _db.LibraryId, "Alpha Saga", false, Auto()));
        Assert.Equal("library_metadata_disabled", ex.Code);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task OneGlobalBudget_AutomaticCountsAndStops_NoSeparateCapOrReserve()
    {
        await _h.EnableAutomaticAsync();
        var row = await _db.Db.AppSettings.FirstAsync();
        row.MetadataDailyBudget = 2;
        await _db.Db.SaveChangesAsync();

        var call = Auto();
        var gateway = _h.Net.Gateway();
        await gateway.SearchAutomaticAsync(Mu, _db.LibraryId, "Alpha Saga", false, call);
        await gateway.GetSeriesAsync(Mu, _db.LibraryId, "101", default, call);
        Assert.Equal(2, call.RequestsSent);
        Assert.Equal(2, (await _h.Net.Budget().GetAsync()).Used); // the ONE budget

        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway.GetSeriesAsync(Mu, _db.LibraryId, "101", default, Auto()));
        Assert.Equal("budget_exhausted", ex.Code);
        // Manual identify waits for the next UTC day too - no hidden reserve (decision 5).
        ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway.GetSeriesAsync(Mu, _db.LibraryId, "101"));
        Assert.Equal("budget_exhausted", ex.Code);
        Assert.Equal(2, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Backoff_RefusesAutomatic_ZeroCalls_AndAutomatic429PausesInteractive()
    {
        await _h.EnableAutomaticAsync();
        _h.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var gateway = _h.Net.Gateway();
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway.SearchAutomaticAsync(Mu, _db.LibraryId, "Alpha Saga", false, Auto()));
        Assert.Equal("provider_backoff", ex.Code);
        Assert.Equal(1, _h.Handler.CallCount);

        // The shared, persisted backoff now refuses both origins with no call.
        ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway.GetSeriesAsync(Mu, _db.LibraryId, "101", default, Auto()));
        Assert.Equal("provider_backoff", ex.Code);
        ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway.GetSeriesAsync(Mu, _db.LibraryId, "101"));
        Assert.Equal("provider_backoff", ex.Code);
        Assert.Equal(1, _h.Handler.CallCount);
    }

    [Fact]
    public async Task AutomaticSearch_AlwaysSendsTheFixedTypeFilter_DoujinshiLiftedOnlyOnRequest()
    {
        await _h.EnableAutomaticAsync();
        var gateway = _h.Net.Gateway();
        await gateway.SearchAutomaticAsync(Mu, _db.LibraryId, "Alpha Saga", allowDoujinshi: false, Auto());
        await gateway.SearchAutomaticAsync(Mu, _db.LibraryId, "Alpha Saga", allowDoujinshi: true, Auto());
        Assert.Equal(2, _h.Handler.CallCount); // different filters are different cache keys

        string[] Filter(int i) => JsonDocument.Parse(_h.Handler.Seen[i].Body!).RootElement.GetProperty("filter_types")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["Doujinshi", "Novel", "Artbook", "Drama CD"], Filter(0));
        Assert.Equal(["Novel", "Artbook", "Drama CD"], Filter(1));
        var keys = JsonDocument.Parse(_h.Handler.Seen[0].Body!).RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["filter_types", "page", "perpage", "search"], keys); // nothing else is sent

        // A cache hit sends nothing and counts nothing.
        var call = Auto();
        await gateway.SearchAutomaticAsync(Mu, _db.LibraryId, "alpha  saga", allowDoujinshi: false, call);
        Assert.Equal(0, call.RequestsSent);
        Assert.Equal(2, _h.Handler.CallCount);
    }

    [Fact]
    public async Task BusyBucket_InteractiveIsRefused_AutomaticWaitsForAToken()
    {
        var rates = new MetadataRateLimitOptions
        {
            AutomaticInterval = TimeSpan.Zero,
            Api = new TokenBucketRateLimiterOptions
            {
                TokenLimit = 1,
                TokensPerPeriod = 1,
                // Long enough that a slow first call (cold client, loaded CI host) cannot refill the
                // token before the second interactive call; the automatic call then waits for it.
                ReplenishmentPeriod = TimeSpan.FromSeconds(3),
                QueueLimit = 0,
                AutoReplenishment = true,
            },
        };
        using var h = new AutoMatchHarness(_db, rates);
        h.Records[101] = MuJson.Get(101, "Alpha Saga");
        await h.EnableAutomaticAsync();
        var gateway = h.Net.Gateway();

        await gateway.GetSeriesAsync(Mu, _db.LibraryId, "101"); // takes the only token
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway.GetSeriesAsync(Mu, _db.LibraryId, "101"));
        Assert.Equal("provider_busy", ex.Code);

        var record = await h.Net.Gateway().GetSeriesAsync(Mu, _db.LibraryId, "101", default, Auto());
        Assert.NotNull(record); // waited instead of failing
    }

    [Fact]
    public async Task AutomaticPacing_SpacesAutomaticRequests()
    {
        using var h = new AutoMatchHarness(_db, new MetadataRateLimitOptions { AutomaticInterval = TimeSpan.FromMilliseconds(400) });
        h.Records[101] = MuJson.Get(101, "Alpha Saga");
        await h.EnableAutomaticAsync();
        var gateway = h.Net.Gateway();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
            await gateway.GetSeriesAsync(Mu, _db.LibraryId, "101", default, Auto());
        Assert.True(watch.ElapsedMilliseconds >= 700, $"3 automatic calls took {watch.ElapsedMilliseconds} ms");
    }
}
