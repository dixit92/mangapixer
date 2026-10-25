namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.48.0 lane A (access invariants, gap 3): the start-up warm-up (<see cref="StartupWarmup"/>) reads as an active ADMIN and fills the
/// Completion and New chapters caches for every library. A restricted reader's FIRST browse, home, Completion and New chapters right after
/// it are still theirs: only their granted libraries (the Private one hidden under Incognito), their own read state, nothing of the rest.
/// </summary>
public sealed class StartupWarmupReaderHttpTests
{
    private static readonly Dictionary<string, string?> On = new()
    {
        ["MangaPixer:Warmup:Enabled"] = "true",
        ["MangaPixer:Warmup:StartDelaySeconds"] = "0",
    };

    [Fact]
    public async Task AReadersFirstAnswers_AfterTheAdminWarmUp_AreTheReadersOwn()
    {
        await using var factory = new ApiTokenTestFactory(On);
        await factory.Services.GetRequiredService<StartupWarmupHostedService>().Completion.WaitAsync(TimeSpan.FromSeconds(60));
        var admin = await factory.AdminAsync();
        var w = await TwoUserSeed.SeedAsync(factory, admin, "warmreader");

        // The warm-up runs as the admin (no tracker account anywhere): every library is warmed.
        var report = await factory.Services.GetRequiredService<StartupWarmup>().RunAsync(CancellationToken.None);
        Assert.Equal(StartupWarmupOutcome.Completed, report.Outcome);
        Assert.True(report.AsUser);
        Assert.Equal(3, report.Libraries);
        Assert.Equal(0, report.Failed);

        // The reader's very first requests - nothing they sent before warmed anything for them.
        var libraries = (await w.Reader.Client.GetFromJsonAsync<List<LibraryDto>>("/api/v1/libraries", TestJson.Web))!;
        Assert.Equal([w.G.PublicId, w.P.PublicId], libraries.Select(l => l.Id).Order(StringComparer.Ordinal).ToArray());

        var home = await TwoUserCacheHttpTests.RecentAsync(w.Reader.Client);
        Assert.Equal([w.G.PublicId, w.P.PublicId], home.Libraries.Select(l => l.LibraryId).Order(StringComparer.Ordinal).ToArray());
        var saga = home.Libraries.Single(l => l.LibraryId == w.G.PublicId).Stacks.Single(s => s.Id == w.G.SeriesPublicId);
        Assert.Equal("unread", saga.ReadState); // the admin read it; the reader did not
        Assert.False(saga.IsFavorite);
        var homeIncognito = await TwoUserCacheHttpTests.RecentAsync(w.Reader.Incognito);
        Assert.Equal([w.G.PublicId], homeIncognito.Libraries.Select(l => l.LibraryId).ToArray());

        var root = await TwoUserCacheHttpTests.BrowseAsync(w.Reader.Client, w.G, string.Empty);
        var adminRoot = await TwoUserCacheHttpTests.BrowseAsync(admin, w.G, string.Empty);
        Assert.Equal(adminRoot.Items.Select(i => i.Id), root.Items.Select(i => i.Id));
        Assert.Equal(adminRoot.LinkedSeries, root.LinkedSeries);
        Assert.Equal(adminRoot.CompletionCounts, root.CompletionCounts);
        Assert.Equal([w.G.SeriesPublicId], (await TwoUserCacheHttpTests.BrowseAsync(w.Reader.Client, w.G, "completion=HaveItAll")).Items.Select(i => i.Id).ToArray());

        foreach (var (client, lib) in new[] { (w.Reader.Client, w.U), (w.Reader.Incognito, w.P) })
        {
            var hidden = await TwoUserCacheHttpTests.BrowseAsync(client, lib, "completion=HaveItAll");
            Assert.Empty(hidden.Items);
            Assert.Null(hidden.CompletionCounts);
        }

        var raw = await w.Reader.Client.GetStringAsync("/api/v1/home/recent-chapters") + await w.Reader.Client.GetStringAsync("/api/v1/libraries");
        Assert.DoesNotContain(w.U.Word, raw, StringComparison.Ordinal);
    }
}
