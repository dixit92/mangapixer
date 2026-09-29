namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The 1.29.0 cover seam through the public surface: <see cref="ICoverResolver"/> is registered as the pass-through
/// <see cref="FileCoverResolver"/>, and the card covers of browse (folders and archives) and Home "New chapters" come from
/// WHATEVER resolver the container holds - so the cover layer replaces one registration, not the call sites.
/// </summary>
[Collection("HttpSerial")]
public sealed class CoverResolverSeamHttpTests
{
    private const string LibPub = "seamlib1";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Tags every URL of the pass-through resolver, so a test sees which resolver produced a cover.</summary>
    private sealed class TaggingResolver(MangaPixerDbContext db) : ICoverResolver
    {
        public async Task<IReadOnlyDictionary<long, string>> ResolveUrlsAsync(IReadOnlyCollection<CoverTarget> targets, CancellationToken ct)
            => (await new FileCoverResolver(db).ResolveUrlsAsync(targets, ct)).ToDictionary(kv => kv.Key, kv => kv.Value + "?seam=1");
    }

    private static async Task<(string LibraryId, string SeriesId, string ArchiveId)> SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Seam Lib", RootPath = "/synthetic/seam", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var series = Node("seamSeries", lib.Id, null, 0, "Series");
        db.CatalogNodes.Add(series);
        await db.SaveChangesAsync();
        var second = Node("seamArc2", lib.Id, series.Id, 1, "Series v02");
        var first = Node("seamArc1", lib.Id, series.Id, 1, "Series v01");
        db.CatalogNodes.AddRange(second, first);
        await db.SaveChangesAsync();
        return (LibPub, series.PublicId, first.PublicId);
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

    [Fact]
    public void TheContainer_HoldsThePassThroughResolver()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        using var scope = factory.Services.CreateScope();
        Assert.IsType<FileCoverResolver>(scope.ServiceProvider.GetRequiredService<ICoverResolver>());
    }

    [Fact]
    public async Task BrowseAndHome_TakeTheirCardCovers_FromTheRegisteredResolver()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true,
            configureServices: s => s.AddScoped<ICoverResolver, TaggingResolver>());
        var (libraryId, seriesId, firstArchiveId) = await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var root = await admin.GetFromJsonAsync<PageResponse<CatalogNodeDto>>($"/api/v1/libraries/{libraryId}/browse?sort=name", JsonOptions);
        // The folder's cover: its first archive by sort key (v01, although v02 was added first), via the resolver.
        Assert.Equal($"/api/v1/items/{firstArchiveId}/cover?seam=1", root!.Items.Single(i => i.Id == seriesId).CoverUrl);

        var inside = await admin.GetFromJsonAsync<PageResponse<CatalogNodeDto>>(
            $"/api/v1/libraries/{libraryId}/browse?sort=name&parentId={seriesId}", JsonOptions);
        Assert.All(inside!.Items, i => Assert.Equal($"/api/v1/items/{i.Id}/cover?seam=1", i.CoverUrl));

        var home = await admin.GetFromJsonAsync<RecentChaptersDto>("/api/v1/home/recent-chapters", JsonOptions);
        var stack = home!.Libraries.Single(g => g.LibraryId == libraryId).Stacks.Single(s => s.Id == seriesId);
        Assert.Equal($"/api/v1/items/{firstArchiveId}/cover?seam=1", stack.CoverUrl);
    }
}
