namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers.Adapters;

using System.Collections.Concurrent;
using System.Net;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Owner answer 3 (1.43.0, 2026-10-15): "MyAnimeList refuses dates" is remembered across restarts - in the existing <c>job_runs</c> table
/// (no migration), read on the first dated write, never cleared by the app. A "restart" is a NEW client instance with a NEW memory object
/// (nothing in process memory survives). Scripted answers only - no request to MyAnimeList.
/// </summary>
public sealed class MyAnimeListDatesMemoryTests
{
    private static TrackerEntryWrite Dated => new(TrackerEntryStatus.Reading, 12, 2, new DateOnly(2026, 1, 2), null, null);

    private static string[] Keys(SeenRequest seen) => TrackerAdapterHarness.Form(seen.Body).Select(p => p.Key).ToArray();

    private sealed class InMemoryDates : ITrackerDatesMemory
    {
        public ConcurrentDictionary<string, bool> Refused { get; } = new();
        public int Reads;

        public Task<bool> IsRefusedAsync(string tracker, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(Refused.ContainsKey(tracker));
        }

        public Task RememberRefusedAsync(string tracker, CancellationToken ct)
        {
            Refused[tracker] = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARefusal_IsRemembered_AndANewClientSendsNoDates_ReadingTheMemoryOnce()
    {
        using var h = new TrackerAdapterHarness();
        var memory = new InMemoryDates();
        var calls = 0;
        h.Answer(_ => ++calls == 1 ? TrackerAdapterHarness.Status(HttpStatusCode.BadRequest) : TrackerAdapterHarness.Json("{}"));

        await h.NewMal(memory).WriteEntryAsync(TrackerAdapterHarness.Tokens, "2", Dated, CancellationToken.None);
        Assert.True(memory.Refused[TrackerSlugs.MyAnimeList]);

        // "Restart": a new client. Its first dated write sends no dates and is not refused first.
        var restarted = h.NewMal(memory);
        Assert.False(restarted.DatesRefused); // not read yet (lazily, on the first dated write)
        await restarted.WriteEntryAsync(TrackerAdapterHarness.Tokens, "2", Dated, CancellationToken.None);
        await restarted.WriteEntryAsync(TrackerAdapterHarness.Tokens, "2", Dated, CancellationToken.None);

        Assert.Equal(4, h.Handler.Seen.Count);
        Assert.Equal(["status", "num_chapters_read", "num_volumes_read"], Keys(h.Handler.Seen[2]));
        Assert.Equal(["status", "num_chapters_read", "num_volumes_read"], Keys(h.Handler.Seen[3]));
        Assert.True(restarted.DatesRefused);
        Assert.Equal(2, memory.Reads); // once per client
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WithoutARefusal_DatesKeepGoingOut()
    {
        using var h = new TrackerAdapterHarness();
        var memory = new InMemoryDates();
        h.Answer(_ => TrackerAdapterHarness.Json("{}"));

        await h.NewMal(memory).WriteEntryAsync(TrackerAdapterHarness.Tokens, "2", Dated, CancellationToken.None);

        Assert.Contains("start_date", Keys(Assert.Single(h.Handler.Seen)));
        Assert.Empty(memory.Refused);
    }

    [Fact]
    [Trait("Category", "Http")]
    public async Task TheHostsClient_StoresTheRefusalInJobRuns_AndARestartedClientReadsIt()
    {
        var handler = new ScriptedHandler();
        var calls = 0;
        handler.Respond = _ => ++calls == 1 ? TrackerAdapterHarness.Status(HttpStatusCode.UnprocessableEntity) : TrackerAdapterHarness.Json("{}");
        await using var factory = new ApiTokenTestFactory(configureServices: services =>
            services.AddHttpClient(TrackerHttp.MyAnimeListClient).ConfigurePrimaryHttpMessageHandler(() => handler));

        // The client as the app registers it (the dates memory wired in).
        var mal = factory.Services.GetServices<ITrackerClient>().OfType<MyAnimeListTrackerClient>().Single();
        Assert.IsType<JobRunTrackerDatesMemory>(factory.Services.GetRequiredService<ITrackerDatesMemory>());
        await mal.WriteEntryAsync(TrackerAdapterHarness.Tokens, "2", Dated, CancellationToken.None);
        Assert.Equal(2, handler.Seen.Count);
        using (var scope = factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().JobRuns.AsNoTracking()
                .SingleAsync(r => r.Key == "tracker-dates-refused-mal");
            Assert.Equal("refused", row.LastOutcome);
        }

        // "Restart": a new client and a new memory over the same database - no dates, one request.
        var restarted = new MyAnimeListTrackerClient(factory.Services.GetRequiredService<IHttpClientFactory>(),
            factory.Services.GetRequiredService<TrackerPacing>(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MyAnimeListTrackerClient>.Instance,
            new JobRunTrackerDatesMemory(factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System));
        await restarted.WriteEntryAsync(TrackerAdapterHarness.Tokens, "2", Dated, CancellationToken.None);

        Assert.Equal(3, handler.Seen.Count);
        Assert.Equal(["status", "num_chapters_read", "num_volumes_read"], Keys(handler.Seen[2]));
    }
}
