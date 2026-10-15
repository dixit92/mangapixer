namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers.Adapters;

using System.Globalization;
using System.Net;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.43.0 (lane A): the adapters as the app registers them (the real host's services, primary handlers replaced by a scripted fake) and
/// the ONE <c>api.mangaupdates.com</c> pacer the metadata fetcher and the MangaUpdates tracker share.
/// </summary>
public sealed class TrackerAdapterWiringTests
{
    [Fact]
    [Trait("Category", "Http")]
    public async Task TheHost_RegistersBothTrackers_OnTheirOwnAllowlistedClients()
    {
        var handler = new ScriptedHandler
        {
            Respond = r => r.RequestUri!.Host switch
            {
                TrackerHttp.MyAnimeListApiHost => ScriptedHandler.Json("""{"id":7,"name":"synthetic_reader"}"""),
                MetadataHttp.MangaUpdatesApiHost => ScriptedHandler.Json("""{"user_id":8,"username":"synthetic_reader"}"""),
                _ => throw new InvalidOperationException("unexpected host"),
            },
        };
        using var factory = new ApiTokenTestFactory(configureServices: services =>
        {
            services.AddHttpClient(TrackerHttp.MyAnimeListClient).ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddHttpClient(TrackerHttp.MangaUpdatesClient).ConfigurePrimaryHttpMessageHandler(() => handler);
        });

        var clients = factory.Services.GetServices<ITrackerClient>().ToDictionary(c => c.Slug);
        Assert.Equal(TrackerSlugs.All.Order(), clients.Keys.Order());

        Assert.Equal("7", (await clients[TrackerSlugs.MyAnimeList].WhoAmIAsync(TrackerAdapterHarness.Tokens, CancellationToken.None)).RemoteUserId);
        Assert.Equal("8", (await clients[TrackerSlugs.MangaUpdates].WhoAmIAsync(TrackerAdapterHarness.Tokens, CancellationToken.None)).RemoteUserId);
        Assert.All(handler.Seen, s =>
        {
            Assert.Equal(MetadataHttp.UserAgent, s.Headers["User-Agent"]);
            Assert.Equal("Bearer " + TrackerAdapterHarness.Access, s.Headers["Authorization"]);
            Assert.False(s.Headers.ContainsKey("Cookie"));
        });

        // The registered clients keep the allowlist: a redirect is refused, not followed.
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.example/") } };
        var ex = await Assert.ThrowsAsync<TrackerException>(() => clients[TrackerSlugs.MangaUpdates].WhoAmIAsync(TrackerAdapterHarness.Tokens, CancellationToken.None));
        Assert.Equal(TrackerFailure.Rejected, ex.Failure);
        Assert.Null(ex.StatusCode);

        // One api.mangaupdates.com bucket for both features.
        Assert.Same(factory.Services.GetRequiredService<MetadataGatewayState>().ApiLimiterOf(MetadataProviderAllowlist.MangaUpdates),
            factory.Services.GetRequiredService<TrackerPacing>().MangaUpdates);
    }

    [Fact]
    [Trait("Category", "ServiceDb")]
    public async Task TheSharedPacer_HoldsATrackerCall_AfterAMetadataCall_AndTheOtherWayRound()
    {
        await using var db = await MetadataTestDb.CreateAsync();
        // One api.mangaupdates.com token that does not refill during the test, no queue.
        using var gateway = new GatewayHarness(db, GatewayHarness.SingleTokenRates());
        using var trackers = new TrackerAdapterHarness(gateway.State);
        await gateway.EnableAsync();
        trackers.Answer(_ => TrackerAdapterHarness.Json("""{"user_id":8,"username":"synthetic_reader"}"""));

        // A metadata call takes the token ...
        Assert.NotNull(await gateway.Gateway().GetSeriesAsync(MetadataProviderAllowlist.MangaUpdates, db.LibraryId,
            MuFixtures.BerserkId.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(1, gateway.Handler.CallCount);

        // ... so the tracker call right after it waits its turn: refused here (no queue, no refill) and sends nothing.
        var ex = await Assert.ThrowsAsync<TrackerException>(() => trackers.MangaUpdates.WhoAmIAsync(TrackerAdapterHarness.Tokens, CancellationToken.None));
        Assert.Equal(TrackerFailure.RateLimited, ex.Failure);
        Assert.NotNull(ex.RetryAfter);
        Assert.Equal(0, trackers.Handler.CallCount);

        // The other way round: a tracker call takes a fresh bucket's only token and the metadata call is held (provider_busy).
        using var gateway2 = new GatewayHarness(db, GatewayHarness.SingleTokenRates());
        using var trackers2 = new TrackerAdapterHarness(gateway2.State);
        trackers2.Answer(_ => TrackerAdapterHarness.Json("""{"user_id":8,"username":"synthetic_reader"}"""));
        await trackers2.MangaUpdates.WhoAmIAsync(TrackerAdapterHarness.Tokens, CancellationToken.None);
        var busy = await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway2.Gateway().GetSeriesAsync(MetadataProviderAllowlist.MangaUpdates,
            db.LibraryId, MuFixtures.BerserkId.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal("provider_busy", busy.Code);
        Assert.Equal(0, gateway2.Handler.CallCount);

        // A tracker call never spends the metadata daily budget.
        Assert.Equal(1, (await gateway.Budget().GetAsync()).Used);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MyAnimeList_IsPacedAtOneRequestASecond()
    {
        var options = new TrackerRateLimitOptions().MyAnimeList;
        Assert.Equal(1, options.TokenLimit);
        Assert.Equal(1.0, options.TokensPerPeriod / options.ReplenishmentPeriod.TotalSeconds);
    }
}
