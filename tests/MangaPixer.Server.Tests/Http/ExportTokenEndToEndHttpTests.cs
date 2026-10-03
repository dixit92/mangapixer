namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The two halves of the MangaList API together (1.33.0): a personal access token (lane T) reads the metadata export (lane E) the
/// way MangaList will - libraries, a full page, the 409 that asks for a full sync - and stops working when revoked; a write with the
/// token is refused; the per-token ceiling answers 429 with <c>Retry-After</c>. Synthetic names only.
/// </summary>
[Trait("Category", "Http")]
public sealed class ExportTokenEndToEndHttpTests
{
    private const string LibPubId = "e2elib1";

    private static async Task SeedAsync(ApiTokenTestFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var now = DateTimeOffset.UtcNow;
        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Token Export", RootPath = "/synthetic/e2e-root", CreatedAt = now };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        for (var i = 1; i <= 2; i++)
        {
            var folder = new CatalogNodeEntity
            {
                PublicId = $"e2eSeries{i}",
                LibraryId = lib.Id,
                Kind = 0,
                DisplayName = $"Token Series {i}",
                RelativePath = $"rel/e2eSeries{i}",
                PathKey = $"rel/e2eSeries{i}",
                SortKey = $"0Token Series {i}",
                CreatedAt = now,
            };
            db.CatalogNodes.Add(folder);
            var record = new MetadataRecordEntity
            {
                PublicId = $"e2erec{i}",
                Provider = "mangaupdates",
                ExternalId = $"90{i}",
                Title = $"Token Series {i}",
                OriginStatus = (int)MetadataOriginStatus.Ongoing,
                FetchedAt = now,
            };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = folder.Id,
                LibraryId = lib.Id,
                State = (int)SeriesLinkState.Confirmed,
                RecordId = record.Id,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task AToken_ReadsTheExport_AsMangaListWill_AndStopsWhenRevoked()
    {
        await using var factory = new ApiTokenTestFactory();
        await SeedAsync(factory);
        var created = await factory.CreateTokenAsync("MangaList");
        var mangaList = factory.BearerClient(created.Secret);

        var libraries = await OkAsync(await mangaList.GetAsync("/api/v1/export/libraries"));
        Assert.Contains(libraries.GetProperty("libraries").EnumerateArray(), l => l.GetProperty("id").GetString() == LibPubId);

        var page = await OkAsync(await mangaList.GetAsync($"/api/v1/export/metadata?library={LibPubId}"));
        Assert.Equal(1, page.GetProperty("schemaVersion").GetInt32());
        Assert.True(page.TryGetProperty("serverTime", out _));
        var trails = page.GetProperty("items").EnumerateArray()
            .Select(i => string.Join("/", i.GetProperty("trail").EnumerateArray().Select(t => t.GetString())))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["Token Series 1", "Token Series 2"], trails);

        var tooOld = await mangaList.GetAsync($"/api/v1/export/metadata?library={LibPubId}&updatedSince=2001-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.Conflict, tooOld.StatusCode);
        Assert.Contains("fullSyncRequired", await tooOld.Content.ReadAsStringAsync());

        // A token never writes: the export has no other method (routing answers 405 before authentication; the token scheme
        // also refuses any method but GET / HEAD), and the admin API is out of its reach.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await mangaList.PostAsync($"/api/v1/export/metadata?library={LibPubId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mangaList.GetAsync("/api/v1/admin/tokens")).StatusCode);

        var admin = await factory.AdminAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/admin/tokens/{created.Token.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mangaList.GetAsync($"/api/v1/export/metadata?library={LibPubId}")).StatusCode);
    }

    [Fact]
    public async Task ThePerTokenCeiling_Answers429WithRetryAfter_OnTheExport()
    {
        await using var factory = new ApiTokenTestFactory(new Dictionary<string, string?>
        {
            ["MangaPixer:Security:ApiTokens:RequestsPerMinute"] = "3",
        });
        await SeedAsync(factory);
        var mangaList = factory.BearerClient((await factory.CreateTokenAsync("MangaList")).Secret);

        for (var i = 0; i < 3; i++)
            await OkAsync(await mangaList.GetAsync($"/api/v1/export/metadata?library={LibPubId}"));
        var limited = await mangaList.GetAsync($"/api/v1/export/metadata?library={LibPubId}");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null, "429 without Retry-After");

        // The admin's own cookie is never limited by a token's ceiling.
        var admin = await factory.AdminAsync();
        await OkAsync(await admin.GetAsync($"/api/v1/export/metadata?library={LibPubId}"));
    }
}
