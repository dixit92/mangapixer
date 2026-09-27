namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using System.Text.Json;
using System.Threading.RateLimiting;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// The production retrieval loop (<see cref="AutoMatchLookup"/>) with the REAL matcher core over scripted
/// provider answers (1.27.0): which hits are fetched, when page 2 of the same text is read, and the per-work
/// search bound. Synthetic titles only; no network.
/// </summary>
public sealed class AutoMatchRetrievalTests : IAsyncLifetime
{
    private MetadataTestDb _db = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        using var net = new GatewayHarness(_db);
        await net.EnableAsync();
        var row = await _db.Db.AppSettings.FirstAsync(s => s.Id == AppSettingsEntity.SingletonId);
        row.MetadataAutoMatchEnabled = true;
        row.MetadataAutoConsentVersion = MetadataAutoConsent.CurrentVersion;
        row.MetadataAutoConsentAt = net.Time.GetUtcNow();
        await _db.Db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static readonly MetadataRateLimitOptions s_unpaced = new()
    {
        AutomaticInterval = TimeSpan.Zero,
        Api = new TokenBucketRateLimiterOptions { TokenLimit = 1000, TokensPerPeriod = 1000, ReplenishmentPeriod = TimeSpan.FromMilliseconds(1), QueueLimit = 0, AutoReplenishment = true },
    };

    private static MatchQuery Query(params string[] names) => new(
        names.Select((n, i) => new QueryVariant(n, i == 0 ? QueryVariantKind.Primary : QueryVariantKind.EnglishTitle)).ToList(),
        new MatchContext(WorkClass.Series, 10, 10, 0, null, null, false, []));

    /// <summary>Answers searches from (text, page) -> hits and GETs from id -> JSON; records every request.</summary>
    private async Task<(WorkLookupResult Result, List<(string Text, int Page)> Searches, List<long> Gets)> RunAsync(
        MatchQuery query, Dictionary<(string, int), (MuJson.Hit[] Hits, int Total)> pages, Dictionary<long, string> records)
    {
        using var net = new GatewayHarness(_db, s_unpaced);
        var searches = new List<(string, int)>();
        var gets = new List<long>();
        net.Handler.Respond = request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var text = body.RootElement.GetProperty("search").GetString()!;
                var page = body.RootElement.GetProperty("page").GetInt32();
                searches.Add((text, page));
                if (!pages.TryGetValue((text, page), out var p))
                    return ScriptedHandler.Json("{\"total_hits\":0,\"results\":[]}");
                var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(MuJson.Search(p.Hits))!;
                return ScriptedHandler.Json(JsonSerializer.Serialize(new { total_hits = p.Total, results = json["results"] }));
            }
            var id = long.Parse(request.RequestUri!.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
            gets.Add(id);
            return records.TryGetValue(id, out var r)
                ? ScriptedHandler.Json(r)
                : ScriptedHandler.Json("{\"reason\":\"not found\"}", System.Net.HttpStatusCode.NotFound);
        };
        var lookup = new AutoMatchLookup(_db.Db, net.Gateway(), new MatchQueryPlanner(), new MatchScorer());
        var classification = new WorkClassification(WorkClass.Series, MatchLevel.Folder, [], []);
        var result = await lookup.SearchAndScoreAsync(query, classification, _db.LibraryId, MatchThresholds.Default, false,
            MetadataCallContext.Automatic(), CancellationToken.None);
        return (result, searches, gets);
    }

    private static MuJson.Hit[] Filler(int n, long firstId) =>
        Enumerable.Range(0, n).Select(i => new MuJson.Hit(firstId + i, $"Unrelated Filler Name {i}")).ToArray();

    [Fact]
    public async Task PageTwo_IsRead_WhenPageOneHasNothingAtTheReviewFloor_AndFindsTheRecord()
    {
        var pages = new Dictionary<(string, int), (MuJson.Hit[], int)>
        {
            [("Sprout Garden", 1)] = (Filler(10, 100), 25),
            [("Sprout Garden", 2)] = ([new MuJson.Hit(7, "Sprout Garden"), .. Filler(9, 200)], 25),
        };
        var (result, searches, gets) = await RunAsync(Query("Sprout Garden"), pages,
            new() { [7] = MuJson.Get(7, "Sprout Garden", status: "10 Volumes (Complete)") });

        Assert.Equal([("Sprout Garden", 1), ("Sprout Garden", 2)], searches);
        Assert.Equal([7L], gets); // page 1's hits were below the floor: no GET spent on them
        Assert.Equal(MatchBand.Auto, result.Outcome.Band);
        Assert.Equal("7", result.Outcome.Ranked[0].Candidate.ExternalId);
    }

    [Fact]
    public async Task PageTwo_IsRead_WhenPageOneTiesAtTheTop()
    {
        var pages = new Dictionary<(string, int), (MuJson.Hit[], int)>
        {
            [("Sprout", 1)] = ([new MuJson.Hit(1, "Sprout (OTHER Person)"), new MuJson.Hit(2, "Sprout (THIRD Person)"), .. Filler(8, 100)], 40),
            [("Sprout", 2)] = ([new MuJson.Hit(3, "Sprout (FAMILY Given)"), .. Filler(9, 200)], 40),
        };
        var (result, searches, _) = await RunAsync(Query("Sprout"), pages, new()
        {
            [1] = MuJson.Get(1, "Sprout (OTHER Person)"),
            [2] = MuJson.Get(2, "Sprout (THIRD Person)"),
            [3] = MuJson.Get(3, "Sprout (FAMILY Given)"),
        });

        Assert.Equal([("Sprout", 1), ("Sprout", 2)], searches);
        Assert.Contains(result.Outcome.Ranked, r => r.Candidate.ExternalId == "3");
        Assert.Equal(MatchBand.NeedsReview, result.Outcome.Band); // still tied: the owner leaves this to the admin
    }

    [Fact]
    public async Task PageTwo_IsNotRead_WhenPageOneIsClear_OrHoldsEveryHit()
    {
        var clear = new Dictionary<(string, int), (MuJson.Hit[], int)>
        {
            [("Sprout Garden", 1)] = ([new MuJson.Hit(7, "Sprout Garden"), .. Filler(9, 100)], 500),
        };
        var (r1, s1, _) = await RunAsync(Query("Sprout Garden"), clear, new() { [7] = MuJson.Get(7, "Sprout Garden", status: "10 Volumes (Complete)") });
        Assert.Equal([("Sprout Garden", 1)], s1);
        Assert.Equal(MatchBand.Auto, r1.Outcome.Band);

        var shortPage = new Dictionary<(string, int), (MuJson.Hit[], int)> { [("Sprout Garden", 1)] = (Filler(4, 100), 4) };
        var (_, s2, g2) = await RunAsync(Query("Sprout Garden"), shortPage, []);
        Assert.Equal([("Sprout Garden", 1)], s2);
        Assert.Empty(g2);
    }

    [Fact]
    public async Task PageTwoReads_ComeAfterEveryVariant_AndStayInsideThePerWorkSearchBound()
    {
        // Four variants, each with a full page 1 of junk and more hits: page 1 of every variant is sent first,
        // and no page 2 fits in the bound.
        var names = new[] { "Name One Here", "Name Two Here", "Name Three Here", "Name Four Here", "Name Five Here" };
        var pages = names.ToDictionary(n => (n, 1), n => (Filler(10, 100), 50));
        var (result, searches, gets) = await RunAsync(Query(names), pages, []);

        Assert.Equal(AutoMatchPolicy.MaxSearchesPerWork, searches.Count);
        Assert.All(searches, s => Assert.Equal(1, s.Page));
        Assert.Empty(gets);
        Assert.Equal(MatchBand.Unmatched, result.Outcome.Band);

        // Two variants: both page 1s, then page 2 of the first while nothing is usable.
        var (_, two, _) = await RunAsync(Query(names[0], names[1]), pages, []);
        Assert.Equal([(names[0], 1), (names[1], 1), (names[0], 2), (names[1], 2)], two);
    }
}
