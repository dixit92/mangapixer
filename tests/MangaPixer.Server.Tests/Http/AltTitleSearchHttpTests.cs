namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests for alt-title search (1.26.0): series matches on page 1 only,
/// members only, Incognito and "Show series information" hiding.
/// </summary>
[Collection("HttpSerial")]
public sealed class AltTitleSearchHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;

    public AltTitleSearchHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(string pubA, string pubB)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var libs = await db.Libraries.Where(l => l.PublicId == "altlibA" || l.PublicId == "altlibB").ToListAsync();
        if (libs.Count == 2)
            return ("altlibA", "altlibB");

        var a = new LibraryEntity { PublicId = "altlibA", DisplayName = "Alt A", RootPath = "/tmp/alt-a", CreatedAt = DateTimeOffset.UtcNow };
        var b = new LibraryEntity { PublicId = "altlibB", DisplayName = "Alt B", RootPath = "/tmp/alt-b", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.AddRange(a, b);
        await db.SaveChangesAsync();

        var rec = new MetadataRecordEntity
        {
            PublicId = "altrec1", Provider = "mangaupdates", ExternalId = "424242", Title = "Zephyr Chronicle",
            AltTitlesJson = "[\"Quixotic Wind Saga\"]", FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.Add(rec);
        await db.SaveChangesAsync();

        foreach (var (lib, id) in new[] { (a, "altnodeA"), (b, "altnodeB") })
        {
            var node = new CatalogNodeEntity
            {
                PublicId = id, LibraryId = lib.Id, Kind = 0, DisplayName = "Folder " + id, RelativePath = "Folder " + id,
                PathKey = "Folder " + id, SortKey = "1folder " + id, Availability = 0, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = node.Id, LibraryId = lib.Id, State = 0, RecordId = rec.Id,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        return ("altlibA", "altlibB");
    }

    private async Task<HttpClient> ResetAsync()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        client.DefaultRequestHeaders.Remove("X-Incognito");
        await client.PutAsJsonAsync("/api/v1/reading/private-libraries", new SetPrivateLibrariesRequest { LibraryIds = [] });
        await SetHiddenAsync(null, false);
        return client;
    }

    private async Task SetHiddenAsync(string? libPublicId, bool hidden)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        foreach (var l in await db.Libraries.Where(l => l.PublicId.StartsWith("altlib")).ToListAsync())
            l.MetadataSeriesInfoHidden = hidden && l.PublicId == libPublicId;
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, string q, string extra = "")
    {
        var resp = await client.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString(q) + extra);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static List<string> MatchedNodeIds(JsonElement root) =>
        root.TryGetProperty("seriesMatches", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Select(x => x.GetProperty("node").GetProperty("id").GetString()!).ToList()
            : [];

    [Fact]
    public async Task Search_Unauthenticated_Returns401()
    {
        await SeedAsync();
        var resp = await _factory.CreateClient().GetAsync("/api/v1/search?q=Quixotic");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Search_AltTitle_ReturnsSeriesMatchesWithMatchedTitle()
    {
        await SeedAsync();
        var client = await ResetAsync();
        var root = await SearchAsync(client, "Quixotic Wind");
        Assert.Equal(["altnodeA", "altnodeB"], MatchedNodeIds(root).Order().ToList());
        Assert.Equal("Quixotic Wind Saga", root.GetProperty("seriesMatches")[0].GetProperty("matchedTitle").GetString());
        Assert.Equal(0, root.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Search_SecondPage_CarriesNoSeriesMatches()
    {
        await SeedAsync();
        var client = await ResetAsync();
        var root = await SearchAsync(client, "Quixotic Wind", "&cursor=0");
        Assert.Empty(MatchedNodeIds(root));
    }

    [Fact]
    public async Task Search_Incognito_HidesPrivateLibraryMatches()
    {
        var (_, pubB) = await SeedAsync();
        var client = await ResetAsync();
        await client.PutAsJsonAsync("/api/v1/reading/private-libraries", new SetPrivateLibrariesRequest { LibraryIds = [pubB] });
        client.DefaultRequestHeaders.Add("X-Incognito", "1");
        var root = await SearchAsync(client, "Quixotic Wind");
        Assert.Equal(["altnodeA"], MatchedNodeIds(root));
        await ResetAsync();
    }

    [Fact]
    public async Task Search_LibraryWithSeriesInfoHidden_DropsItsMatches()
    {
        var (_, pubB) = await SeedAsync();
        var client = await ResetAsync();
        await SetHiddenAsync(pubB, true);
        var root = await SearchAsync(client, "Quixotic Wind");
        Assert.Equal(["altnodeA"], MatchedNodeIds(root));
        await SetHiddenAsync(null, false);
    }
}
