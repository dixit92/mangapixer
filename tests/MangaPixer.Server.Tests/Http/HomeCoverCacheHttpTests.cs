namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Home;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.47.0 lane S: <c>GET /api/v1/home/recent-chapters</c> takes its stack covers from the host's <see cref="HomeCoverCache"/> (the DI
/// singleton): the second answer reuses them, and an admin's cover choice made through <c>PUT / DELETE /api/v1/nodes/{id}/cover-choice</c>
/// is in the very next answer (the cover-layer version moved).
/// </summary>
public sealed class HomeCoverCacheHttpTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    [Fact]
    public async Task TheRow_ReusesTheKeptCovers_AndShowsAnAdminCoverChoiceAtOnce()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        var cache = factory.Services.GetRequiredService<HomeCoverCache>();
        var tag = Guid.NewGuid().ToString("N")[..6];
        string folderId = "hcc-f-" + tag, v1 = "hcc-1-" + tag, v2 = "hcc-2-" + tag;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var library = new LibraryEntity { PublicId = "hcc-" + tag, DisplayName = "Covers " + tag, RootPath = "/synthetic/hcc", CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            CatalogNodeEntity Node(string pub, long? parent, int kind, string name) => new()
            {
                PublicId = pub, LibraryId = library.Id, ParentId = parent, Kind = kind, DisplayName = name, RelativePath = pub, PathKey = pub,
                SortKey = (kind == 0 ? "0" : "1") + name, CreatedAt = DateTimeOffset.UtcNow,
            };
            var folder = Node(folderId, null, 0, "Series");
            db.CatalogNodes.Add(folder);
            await db.SaveChangesAsync();
            var first = Node(v1, folder.Id, 1, "Series v01");
            var second = Node(v2, folder.Id, 1, "Series v02");
            db.CatalogNodes.AddRange(first, second);
            await db.SaveChangesAsync();
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = first.Id, ContentVersion = 1, PageCount = 1 });
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = second.Id, ContentVersion = 1, PageCount = 1 });
            await db.SaveChangesAsync();
        }

        async Task<string?> CoverAsync()
        {
            var dto = await client.GetFromJsonAsync<RecentChaptersDto>("/api/v1/home/recent-chapters", JsonOptions);
            return dto!.Libraries.Single(l => l.LibraryId == "hcc-" + tag).Stacks.Single(s => s.Id == folderId).CoverUrl;
        }

        var automatic = await CoverAsync();
        Assert.Equal($"/api/v1/items/{v1}/cover?v=1", automatic);
        var hits = cache.Hits;
        Assert.Equal(automatic, await CoverAsync());
        Assert.True(cache.Hits > hits, "the second answer reused the kept cover");

        var put = await client.PutAsJsonAsync($"/api/v1/nodes/{folderId}/cover-choice",
            new CoverChoiceRequest { Mode = CoverMode.Archive, ArchiveId = v2 }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var chosen = await CoverAsync();
        Assert.NotEqual(automatic, chosen);
        Assert.StartsWith($"/api/v1/nodes/{folderId}/cover?v=", chosen, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/v1/nodes/{folderId}/cover-choice")).StatusCode);
        Assert.Equal(automatic, await CoverAsync());
    }
}
