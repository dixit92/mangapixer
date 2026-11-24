namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

/// <summary>
/// The per-user Home row order (1.54.0, owner decision 3) through the existing <c>reading/library-preferences</c> endpoint: the full
/// default order until the user changes it; a saved order round-trips and is normalized (unknown and repeated ids dropped, missing ids
/// appended in the default order); a PUT without the field (an older client, or any other Settings switch of an older bundle) keeps it;
/// each user has their own.
/// </summary>
public sealed class HomeRowOrderHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string Url = "/api/v1/reading/library-preferences";
    private static readonly string[] Default = ["continue", "recommended", "favorites", "recent", "libraries"];
    private readonly MangaPixerWebApplicationFactory _factory;

    public HomeRowOrderHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<JsonObject> GetAsync(HttpClient client) => JsonNode.Parse(await client.GetStringAsync(Url))!.AsObject();

    private static string[] Order(JsonObject prefs) =>
        prefs["homeRowOrder"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];

    private static async Task PutAsync(HttpClient client, JsonObject prefs, Action<JsonObject> change)
    {
        var body = prefs.DeepClone().AsObject();
        change(body);
        (await client.PutAsJsonAsync(Url, body)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task HomeRowOrder_DefaultsToTheFullOrder_RoundTrips_Normalizes_AndAnOlderClientKeepsIt()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var initial = await GetAsync(client);
        Assert.Equal(Default, Order(initial));

        await PutAsync(client, initial, b =>
        {
            b["homeRowOrder"] = new JsonArray("libraries", "favorites", "continue", "recent", "recommended");
            b["listColumns"] = 3;
        });
        var saved = await GetAsync(client);
        Assert.Equal(["libraries", "favorites", "continue", "recent", "recommended"], Order(saved));
        Assert.Equal(3, saved["listColumns"]!.GetValue<int>()); // the rest of the blob is untouched

        // Unknown and repeated ids are dropped; the missing ones follow in the default order.
        await PutAsync(client, saved, b => b["homeRowOrder"] = new JsonArray("recent", "bogus", "recent", "libraries"));
        Assert.Equal(["recent", "libraries", "continue", "recommended", "favorites"], Order(await GetAsync(client)));

        // A body without the field (null or absent) keeps the stored order.
        var current = await GetAsync(client);
        await PutAsync(client, current, b => b.Remove("homeRowOrder"));
        await PutAsync(client, current, b => b["homeRowOrder"] = null);
        (await client.PutAsJsonAsync(Url, new { viewMode = "grid", density = "comfortable", sort = "name" })).EnsureSuccessStatusCode();
        Assert.Equal(["recent", "libraries", "continue", "recommended", "favorites"], Order(await GetAsync(client)));

        // Reset: the default order (an empty list means the same).
        await PutAsync(client, current, b => b["homeRowOrder"] = new JsonArray());
        Assert.Equal(Default, Order(await GetAsync(client)));
    }

    [Fact]
    public async Task HomeRowOrder_IsPerUser_AndAReaderSetsTheirOwn()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var reader = await AccessTestUsers.ReaderAsync(_factory, admin, "home-order-reader", []);

        await PutAsync(admin, await GetAsync(admin), b => b["homeRowOrder"] = new JsonArray("favorites", "continue"));
        Assert.Equal(Default, Order(await GetAsync(reader.Client)));

        await PutAsync(reader.Client, await GetAsync(reader.Client), b => b["homeRowOrder"] = new JsonArray("libraries"));
        Assert.Equal(["libraries", "continue", "recommended", "favorites", "recent"], Order(await GetAsync(reader.Client)));
        Assert.Equal(["libraries", "continue", "recommended", "favorites", "recent"], Order(await GetAsync(reader.Incognito)));
        Assert.Equal(["favorites", "continue", "recommended", "recent", "libraries"], Order(await GetAsync(admin)));
    }
}
