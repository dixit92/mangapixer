namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.43.0 merge of the engine (lane E) and the adapters (lane A): the app now registers the REAL MyAnimeList and MangaUpdates clients, so
/// the tracker test harness must make every tracker call go to its fakes - never to an adapter that would try the network. This pins it
/// independently of the order in which the app and the test register their clients.
/// </summary>
public sealed class TrackerHarnessGuardTests
{
    [Fact]
    public async Task TheHarness_AnswersEveryTrackerWithItsFake_NeverARealAdapter()
    {
        await using var harness = new TrackerTestHarness();
        using var scope = harness.Factory.Services.CreateScope();
        var clients = scope.ServiceProvider.GetRequiredService<TrackerClients>();

        Assert.Same(harness.Mal, clients.Get(TrackerSlugs.MyAnimeList));
        Assert.Same(harness.MangaUpdates, clients.Get(TrackerSlugs.MangaUpdates));
        Assert.All(scope.ServiceProvider.GetServices<ITrackerClient>(), c => Assert.IsType<FakeTrackerClient>(c));
    }
}
