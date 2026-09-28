namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory, the PRODUCTION matcher core from DI) of the provider-author half of the
/// artist-folder rule (1.28.0): "Match this library now" estimates an unbracketed author folder archive by
/// archive once a record by that author is linked in the library, and the
/// <c>Metadata:AutoMatch:ProviderAuthorFolders</c> switch (options) restores the 1.27.0 count. No request is sent.
/// </summary>
[Trait("Category", "Http")]
[Collection("HttpSerial")]
public sealed class MetadataAuthorFolderHttpTests
{
    private const string LibPub = "palib1";

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, CatalogNodeKind kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Author Lib", RootPath = "/synthetic/pa", CreatedAt = DateTimeOffset.UtcNow, MetadataEnabled = true };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var linked = Node("paLinked", lib.Id, null, CatalogNodeKind.Folder, "Linked Saga");
        var author = Node("paAuthor", lib.Id, null, CatalogNodeKind.Folder, "Given Family");
        db.CatalogNodes.AddRange(linked, author);
        await db.SaveChangesAsync();
        db.CatalogNodes.AddRange(
            Node("paA1", lib.Id, author.Id, CatalogNodeKind.Archive, "Given Family - Alpha Story"),
            Node("paA2", lib.Id, author.Id, CatalogNodeKind.Archive, "Given Family - Beta Tale"),
            Node("paA3", lib.Id, author.Id, CatalogNodeKind.Archive, "Given Family - Gamma Saga"));
        var record = new MetadataRecordEntity
        {
            PublicId = "rpa1",
            Provider = "mangaupdates",
            ExternalId = "90001",
            Title = "Linked Saga",
            CreatorsJson = """[{"name":"Given Family","role":"author"}]""",
            FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity { NodeId = linked.Id, LibraryId = lib.Id, State = (int)SeriesLinkState.Confirmed, RecordId = record.Id, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    private static async Task<int> EstimateAsync(MetadataNetworkWebApplicationFactory factory)
    {
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = true,
            AcceptedAutoConsentVersion = MetadataAutoConsent.CurrentVersion,
        })).EnsureSuccessStatusCode();
        var response = await admin.GetAsync($"/api/v1/admin/metadata/libraries/{LibPub}/match/estimate");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var estimate = (await response.Content.ReadFromJsonAsync<MetadataMatchEstimateDto>(TestJson.Web))!;
        Assert.Equal(0, factory.Handler.CallCount); // the author set is local: nothing is sent
        return estimate.Candidates;
    }

    [Fact]
    public async Task Estimate_AnAuthorFolderOfALinkedRecord_IsMatchedArchiveByArchive()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);

        Assert.Equal(3, await EstimateAsync(factory)); // three archive works instead of one review-only folder
    }

    [Fact]
    public async Task Estimate_WithTheSwitchOff_IsThe127Count()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true,
            configureServices: s => s.AddSingleton(new MetadataAutoMatchOptions { WorkerEnabled = false, ProviderAuthorFolders = false }));

        Assert.Equal(1, await EstimateAsync(factory)); // the folder, review only
    }
}
