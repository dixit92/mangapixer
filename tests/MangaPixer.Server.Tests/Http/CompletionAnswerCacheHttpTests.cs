namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP test (WebApplicationFactory) for 1.46.0 lane C: in the running app the Completion filter keeps a level's answers between first pages
/// (the registered memory cache), and an admin's declared-fact edit through the API is served by the very next first page - the cache is
/// keyed on what the answers read, not on time. Synthetic names; its own library and app.
/// </summary>
public sealed class CompletionAnswerCacheHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "cachelib1";
    private readonly MangaPixerWebApplicationFactory _factory;

    public CompletionAnswerCacheHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Item(string Id);

    private sealed record Page(List<Item> Items, int TotalCount);

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;
        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Completion Cache Http", RootPath = "/synthetic/cc1", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        async Task SeriesAsync(string pub, string name)
        {
            var folder = Node(pub, lib.Id, null, 0, name);
            db.CatalogNodes.Add(folder);
            await db.SaveChangesAsync();
            foreach (var v in new[] { "v01", "v02" })
            {
                var arc = Node($"{pub}-{v}", lib.Id, folder.Id, 1, $"{name} {v}");
                db.CatalogNodes.Add(arc);
                await db.SaveChangesAsync();
                db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = arc.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 2 });
            }
            var record = new MetadataRecordEntity
            {
                PublicId = pub + "rec",
                Provider = "mangaupdates",
                ExternalId = pub + "1",
                Title = name + " Record",
                Origin = (int)MetadataOrigin.Japan,
                OriginStatus = (int)MetadataOriginStatus.Complete,
                OriginVolumes = 2,
                PublishersJson = """[{"name":"Synthetic Press","kind":"english","volumes":2,"status":"complete"}]""",
                FetchedAt = DateTimeOffset.UtcNow,
            };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = folder.Id,
                LibraryId = lib.Id,
                State = (int)SeriesLinkState.Confirmed,
                RecordId = record.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await SeriesAsync("ccAlpha", "Cache Alpha");
        await SeriesAsync("ccBeta", "Cache Beta");
    }

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, int kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == 0 ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<List<string>> KeptAsync(HttpClient client, string answer)
    {
        var response = await client.GetAsync($"/api/v1/libraries/{LibPubId}/browse?sort=name&direction=asc&pageSize=50&completion={answer}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<Page>(TestJson.Web))!;
        Assert.Equal(page.Items.Count, page.TotalCount);
        return page.Items.Select(i => i.Id).ToList();
    }

    [Fact]
    public async Task ADeclaredFactEditThroughTheApi_IsServedByTheNextFirstPage()
    {
        await SeedAsync();
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        Assert.Equal(["ccAlpha", "ccBeta"], await KeptAsync(admin, "HaveItAll"));
        Assert.Equal(["ccAlpha", "ccBeta"], await KeptAsync(admin, "HaveItAll"));

        // Track completion off for Alpha: it has no answer any more, at once.
        var off = await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/ccAlpha/declared/edition", new SetDeclaredEditionRequest { Tracking = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(["ccBeta"], await KeptAsync(admin, "HaveItAll"));

        // Beta's edition has three volumes: it is missing one, at once.
        var three = await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/ccBeta/declared/edition", new SetDeclaredEditionRequest { VolumeTotal = 3 });
        Assert.Equal(HttpStatusCode.OK, three.StatusCode);
        Assert.Empty(await KeptAsync(admin, "HaveItAll"));
        Assert.Equal(["ccBeta"], await KeptAsync(admin, "FinishedMissing"));
    }
}
