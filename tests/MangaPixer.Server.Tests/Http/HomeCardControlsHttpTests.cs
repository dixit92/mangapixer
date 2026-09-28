using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Http;

/// <summary>
/// HTTP tests (WebApplicationFactory) for the Home card controls (1.28.0): the star and the
/// (i) flag on <c>GET /api/v1/reading/continue</c> and <c>GET /api/v1/home/recent-chapters</c>.
/// A Continue-reading card is one archive: its (i) follows the series-info anchor rule (an
/// archive without ComicInfo inside a linked series folder gets it). "Show series information"
/// off clears the flag; starring through the API sets the star.
/// </summary>
[Collection("HttpSerial")]
public sealed class HomeCardControlsHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "hcclib";
    private readonly MangaPixerWebApplicationFactory _factory;

    public HomeCardControlsHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // hccSeries (folder linked to a web record) > hccCh1 (archive, no ComicInfo);
    // hccPlain (folder, no info) > hccPlain1 (archive). Both archives are in progress for the admin.
    private async Task<HttpClient> SeedAsync()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return client;

        var now = DateTimeOffset.UtcNow;
        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Home Cards", RootPath = "/synthetic/hcc", CreatedAt = now };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        var series = Node("hccSeries", lib.Id, null, CatalogNodeKind.Folder, "Card Series", now);
        var plain = Node("hccPlain", lib.Id, null, CatalogNodeKind.Folder, "Card Plain", now);
        db.CatalogNodes.AddRange(series, plain);
        await db.SaveChangesAsync();
        var ch1 = Node("hccCh1", lib.Id, series.Id, CatalogNodeKind.Archive, "Card Series 01", now);
        var plain1 = Node("hccPlain1", lib.Id, plain.Id, CatalogNodeKind.Archive, "Card Plain 01", now);
        db.CatalogNodes.AddRange(ch1, plain1);
        await db.SaveChangesAsync();

        var record = new MetadataRecordEntity
        {
            PublicId = "hccrec",
            Provider = "mangaupdates",
            ExternalId = "515151",
            Title = "Synthetic Card Title",
            FetchedAt = now,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = series.Id,
            LibraryId = lib.Id,
            State = (int)SeriesLinkState.Confirmed,
            RecordId = record.Id,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var adminId = await db.Users.Where(u => u.IsAdmin).Select(u => u.Id).FirstAsync();
        foreach (var archive in new[] { ch1, plain1 })
        {
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = archive.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 4 });
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = adminId,
                ItemId = archive.Id,
                ContentVersion = 1,
                Ordinal = 1,
                State = (int)ReadingState.InProgress,
                Revision = 1,
                UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync();
        return client;
    }

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, CatalogNodeKind kind, string name, DateTimeOffset now) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name,
        CreatedAt = now,
    };

    private static async Task<Dictionary<string, ContinueReadingEntry>> ContinueAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ContinueReadingEntry>>("/api/v1/reading/continue", TestJson.Web))!
            .Where(e => e.LibraryId == LibPubId)
            .ToDictionary(e => e.ItemId);

    private static async Task<Dictionary<string, RecentChapterStack>> StacksAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<RecentChaptersDto>("/api/v1/home/recent-chapters", TestJson.Web))!
            .Libraries.Single(l => l.LibraryId == LibPubId)
            .Stacks.ToDictionary(s => s.Id);

    [Fact]
    public async Task ContinueReading_CarriesTheStar_AndTheAnchoredSeriesInfoFlag()
    {
        var client = await SeedAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/nodes/hccCh1/favorite", null)).StatusCode);
        try
        {
            var entries = await ContinueAsync(client);
            Assert.True(entries["hccCh1"].HasSeriesInfo);   // inherits its series folder's link
            Assert.True(entries["hccCh1"].IsFavorite);
            Assert.False(entries["hccPlain1"].HasSeriesInfo);
            Assert.False(entries["hccPlain1"].IsFavorite);

            // The same answer the (i) panel gets for that archive.
            var info = await client.GetFromJsonAsync<SeriesInfoDto>("/api/v1/nodes/hccCh1/series-info", TestJson.Web);
            Assert.Equal(SeriesInfoState.Web, info!.State);
            Assert.Equal("hccSeries", info.AnchorNodeId);

            (await client.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPubId}",
                new UpdateMetadataLibraryRequest { ShowSeriesInfo = false })).EnsureSuccessStatusCode();
            Assert.False((await ContinueAsync(client))["hccCh1"].HasSeriesInfo);
        }
        finally
        {
            (await client.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPubId}",
                new UpdateMetadataLibraryRequest { ShowSeriesInfo = true })).EnsureSuccessStatusCode();
            await client.DeleteAsync("/api/v1/nodes/hccCh1/favorite");
        }
    }

    [Fact]
    public async Task RecentChapters_CarriesTheStar_AndTheSeriesInfoFlag()
    {
        var client = await SeedAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/nodes/hccPlain/favorite", null)).StatusCode);
        try
        {
            var stacks = await StacksAsync(client);
            Assert.True(stacks["hccSeries"].HasSeriesInfo);
            Assert.False(stacks["hccSeries"].IsFavorite);
            Assert.False(stacks["hccPlain"].HasSeriesInfo);
            Assert.True(stacks["hccPlain"].IsFavorite);

            (await client.PutAsJsonAsync("/api/v1/admin/metadata/settings",
                new UpdateMetadataSettingsRequest { ShowSeriesInfo = false })).EnsureSuccessStatusCode();
            Assert.False((await StacksAsync(client))["hccSeries"].HasSeriesInfo);
        }
        finally
        {
            (await client.PutAsJsonAsync("/api/v1/admin/metadata/settings",
                new UpdateMetadataSettingsRequest { ShowSeriesInfo = true })).EnsureSuccessStatusCode();
            await client.DeleteAsync("/api/v1/nodes/hccPlain/favorite");
        }
    }
}
