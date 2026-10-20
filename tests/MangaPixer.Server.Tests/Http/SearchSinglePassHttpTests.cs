namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.46.0 (lane B, measured cost #10): search runs its match once for the page and the total, and enriches the results and the series-match
/// anchors in one pass. The answers stay: a node that is both a result and a series-match anchor is the SAME card in both lists (cover, read
/// rollup, favourite, (i)); the total is right on every page, on a page past the end and for no match; the read-state filter keeps the same
/// anchors. Synthetic names only.
/// </summary>
public sealed class SearchSinglePassHttpTests
{
    private const string Query = "Nimbus";

    private static async Task<(MangaPixerWebApplicationFactory Factory, HttpClient Client)> SetupAsync()
    {
        var factory = new MangaPixerWebApplicationFactory();
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var adminId = (await db.Users.FirstAsync(u => u.NormalizedUserName == "ADMIN")).Id;
        var now = DateTimeOffset.UtcNow;
        var library = new LibraryEntity { PublicId = "nimbuslib", DisplayName = "Cloud Library", RootPath = "/tmp/cloud", CreatedAt = now };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        CatalogNodeEntity Node(string id, long? parent, CatalogNodeKind kind, string name) => new()
        {
            PublicId = id,
            LibraryId = library.Id,
            ParentId = parent,
            Kind = (int)kind,
            DisplayName = name,
            RelativePath = id,
            PathKey = id,
            SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name.ToLowerInvariant(),
            Availability = 0,
            CreatedAt = now,
        };
        var saga = Node("nbSaga", null, CatalogNodeKind.Folder, "Nimbus Saga");
        var other = Node("nbOther", null, CatalogNodeKind.Folder, "Stratus Tales");
        db.CatalogNodes.AddRange(saga, other);
        await db.SaveChangesAsync();
        var one = Node("nb01", saga.Id, CatalogNodeKind.Archive, "Nimbus 01");
        var two = Node("nb02", saga.Id, CatalogNodeKind.Archive, "Nimbus 02");
        var stratus = Node("st01", other.Id, CatalogNodeKind.Archive, "Stratus 01");
        db.CatalogNodes.AddRange(one, two, stratus);
        await db.SaveChangesAsync();
        foreach (var archive in new[] { one, two, stratus })
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = archive.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 9, ByteLength = 1 });

        // Both folders are linked; the second one's record is found by its alternative title only (an anchor that is no result).
        var record = new MetadataRecordEntity { PublicId = "nbrec", Provider = "mangaupdates", ExternalId = "515151", Title = "Nimbus Saga", FetchedAt = now };
        var alt = new MetadataRecordEntity
        {
            PublicId = "strec",
            Provider = "mangaupdates",
            ExternalId = "515152",
            Title = "Stratus Tales",
            AltTitlesJson = "[\"Nimbus Side Stories\"]",
            FetchedAt = now,
        };
        db.MetadataRecords.AddRange(record, alt);
        await db.SaveChangesAsync();
        foreach (var (node, rec) in new[] { (saga, record), (other, alt) })
        {
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = node.Id,
                LibraryId = library.Id,
                State = (int)SeriesLinkState.Confirmed,
                RecordId = rec.Id,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        db.ReadMarks.Add(new ReadMarkEntity { UserId = adminId, ItemId = one.Id, MarkedAt = now, Source = "manual" });
        db.ReadMarks.Add(new ReadMarkEntity { UserId = adminId, ItemId = stratus.Id, MarkedAt = now, Source = "manual" });
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = adminId,
            ItemId = two.Id,
            ContentVersion = 1,
            EntryKey = "p2",
            Ordinal = 2,
            State = (int)ReadingState.InProgress,
            Revision = 1,
            LastMutationId = Guid.NewGuid().ToString("N"),
            UpdatedAt = now,
        });
        db.Favorites.Add(new FavoriteEntity { UserId = adminId, CatalogNodeId = saga.Id, CreatedAt = now });
        await db.SaveChangesAsync();
        return (factory, client);
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, string extra = "", string query = Query)
    {
        var response = await client.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString(query) + extra);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static Dictionary<string, string> Items(JsonElement root) =>
        root.GetProperty("items").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetRawText());

    private static Dictionary<string, JsonElement> Anchors(JsonElement root) =>
        root.TryGetProperty("seriesMatches", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().ToDictionary(e => e.GetProperty("node").GetProperty("id").GetString()!, e => e.GetProperty("node").Clone())
            : [];

    [Fact]
    public async Task AResultThatIsAlsoAnAnchor_IsTheSameCard_AndAnchorsCarryTheirReadState()
    {
        var (factory, client) = await SetupAsync();
        using var _ = factory;

        var root = await SearchAsync(client);
        var items = Items(root);
        var anchors = Anchors(root);
        Assert.Equal(["nb01", "nb02", "nbSaga"], items.Keys.Order().ToList());
        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(["nbOther", "nbSaga"], anchors.Keys.Order().ToList());

        // The same card in both lists - enriched once.
        Assert.Equal(items["nbSaga"], anchors["nbSaga"].GetRawText());
        var saga = anchors["nbSaga"];
        Assert.Equal("Reading", saga.GetProperty("readRollup").GetString());
        Assert.True(saga.GetProperty("isFavorite").GetBoolean());
        Assert.True(saga.GetProperty("hasSeriesInfo").GetBoolean());
        // An anchor that is no result gets its own read state and cover.
        Assert.Equal("Read", anchors["nbOther"].GetProperty("readRollup").GetString());
        Assert.False(string.IsNullOrEmpty(anchors["nbOther"].GetProperty("coverUrl").GetString()));
    }

    [Fact]
    public async Task TheTotal_IsRightOnEveryPage_PastTheEnd_AndForNoMatch()
    {
        var (factory, client) = await SetupAsync();
        using var _ = factory;

        var first = await SearchAsync(client, "&pageSize=2");
        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var second = await SearchAsync(client, "&pageSize=2&cursor=" + Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!));
        Assert.Equal(3, second.GetProperty("totalCount").GetInt32());
        Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.False(second.TryGetProperty("seriesMatches", out var later) && later.ValueKind == JsonValueKind.Array); // first page only

        // A page past the end (a bare SortKey cursor after every result) still counts the whole match.
        var past = await SearchAsync(client, "&pageSize=2&cursor=" + Uri.EscapeDataString("1zzzz"));
        Assert.Empty(past.GetProperty("items").EnumerateArray());
        Assert.Equal(3, past.GetProperty("totalCount").GetInt32());
        Assert.False(past.GetProperty("hasMore").GetBoolean());

        var none = await SearchAsync(client, query: "Cumulonimbus");
        Assert.Empty(none.GetProperty("items").EnumerateArray());
        Assert.Equal(0, none.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("reading", new[] { "nb02", "nbSaga" }, new[] { "nbSaga" })]
    [InlineData("read", new[] { "nb01" }, new[] { "nbOther" })]
    [InlineData("unread", new string[0], new string[0])]
    public async Task TheReadStateFilter_KeepsTheSameResultsAndAnchors(string readState, string[] results, string[] anchors)
    {
        var (factory, client) = await SetupAsync();
        using var _ = factory;

        var root = await SearchAsync(client, "&readState=" + readState);
        Assert.Equal(results.Order(), Items(root).Keys.Order());
        Assert.Equal(results.Length, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(anchors.Order(), Anchors(root).Keys.Order());
    }
}
