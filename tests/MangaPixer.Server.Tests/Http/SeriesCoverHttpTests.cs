using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Http;

/// <summary>
/// HTTP tests (WebApplicationFactory) of the SERIES cover (1.30.0, owner soak test on 1.29.1): a linked series folder with
/// volume files and a <c>Chapters/</c> subfolder shows its volume 1's cover - not chapter 1's page 1 - on its browse card,
/// on its Home "New chapters" stack, and on the Continue-reading card of one of its chapters. The decision is made by the
/// registered <see cref="CoverDecisionService"/> (the sweep is off in tests); an unlinked series keeps the folder-native rule.
/// </summary>
[Collection("HttpSerial")]
public sealed class SeriesCoverHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "sclib";
    private readonly MangaPixerWebApplicationFactory _factory;

    public SeriesCoverHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // scSeries (linked) > scV01, scV02, scChapters > scC001, scC005; scPlain (unlinked) > scPlainChapters > scPlainC001, scPlainV01.
    // In progress for the admin: scC005, scV02, scPlainC001.
    private async Task<HttpClient> SeedAsync()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return client;

        var now = DateTimeOffset.UtcNow;
        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Series Covers", RootPath = "/synthetic/sc", CreatedAt = now };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        var series = Node("scSeries", lib.Id, null, CatalogNodeKind.Folder, "Cover Series", now);
        var plain = Node("scPlain", lib.Id, null, CatalogNodeKind.Folder, "Cover Plain", now);
        db.CatalogNodes.AddRange(series, plain);
        await db.SaveChangesAsync();
        var chapters = Node("scChapters", lib.Id, series.Id, CatalogNodeKind.Folder, "Chapters", now);
        var plainChapters = Node("scPlainCh", lib.Id, plain.Id, CatalogNodeKind.Folder, "Chapters", now);
        db.CatalogNodes.AddRange(chapters, plainChapters);
        await db.SaveChangesAsync();
        var archives = new[]
        {
            Node("scV01", lib.Id, series.Id, CatalogNodeKind.Archive, "Cover Series v01", now),
            Node("scV02", lib.Id, series.Id, CatalogNodeKind.Archive, "Cover Series v02", now),
            Node("scC001", lib.Id, chapters.Id, CatalogNodeKind.Archive, "Cover Series c001", now),
            Node("scC005", lib.Id, chapters.Id, CatalogNodeKind.Archive, "Cover Series c005", now),
            Node("scPlainV01", lib.Id, plain.Id, CatalogNodeKind.Archive, "Cover Plain v01", now),
            Node("scPlainC001", lib.Id, plainChapters.Id, CatalogNodeKind.Archive, "Cover Plain c001", now),
        };
        db.CatalogNodes.AddRange(archives);
        await db.SaveChangesAsync();
        foreach (var a in archives)
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = a.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 4 });

        var record = new MetadataRecordEntity
        {
            PublicId = "screc",
            Provider = "mangaupdates",
            ExternalId = "525252",
            Title = "Synthetic Cover Series",
            FetchedAt = now,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = series.Id,
            LibraryId = lib.Id,
            State = (int)SeriesLinkState.Auto,
            RecordId = record.Id,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var adminId = await db.Users.Where(u => u.IsAdmin).Select(u => u.Id).FirstAsync();
        foreach (var pub in new[] { "scC005", "scV02", "scPlainC001" })
        {
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = adminId,
                ItemId = archives.Single(a => a.PublicId == pub).Id,
                ContentVersion = 1,
                Ordinal = 1,
                State = (int)ReadingState.InProgress,
                Revision = 1,
                UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync();

        // The automatic layer decides both series (what the background sweep does after a start).
        var decisions = scope.ServiceProvider.GetRequiredService<CoverDecisionService>();
        await decisions.DecideSubtreeAsync(series.Id, default);
        await decisions.DecideSubtreeAsync(plain.Id, default);
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

    [Fact]
    public async Task BrowseCard_AndNewChaptersStack_ShowVolume1_NotChapter1()
    {
        var client = await SeedAsync();

        var root = await client.GetFromJsonAsync<PageResponse<CatalogNodeDto>>($"/api/v1/libraries/{LibPubId}/browse?sort=name", TestJson.Web);
        var cards = root!.Items.ToDictionary(i => i.Id);
        Assert.Equal("/api/v1/items/scV01/cover?v=1", cards["scSeries"].CoverUrl);
        // Unlinked: the folder-native rule - its first archive by name, in the Chapters subfolder.
        Assert.Equal("/api/v1/items/scPlainC001/cover?v=1", cards["scPlain"].CoverUrl);

        var home = await client.GetFromJsonAsync<RecentChaptersDto>("/api/v1/home/recent-chapters", TestJson.Web);
        var stacks = home!.Libraries.Single(l => l.LibraryId == LibPubId).Stacks.ToDictionary(s => s.Id);
        Assert.Equal("/api/v1/items/scV01/cover?v=1", stacks["scSeries"].CoverUrl);
        Assert.Equal("/api/v1/items/scPlainC001/cover?v=1", stacks["scPlain"].CoverUrl);
    }

    [Fact]
    public async Task ContinueReading_AChapterCardShowsTheSeriesCover()
    {
        var client = await SeedAsync();

        var entries = (await client.GetFromJsonAsync<List<ContinueReadingEntry>>("/api/v1/reading/continue", TestJson.Web))!
            .Where(e => e.LibraryId == LibPubId).ToDictionary(e => e.ItemId);
        Assert.Equal("/api/v1/items/scV01/cover?v=1", entries["scC005"].CoverUrl);
        // A volume keeps its own cover; an unlinked chapter too.
        Assert.Equal("/api/v1/items/scV02/cover?v=1", entries["scV02"].CoverUrl);
        Assert.Equal("/api/v1/items/scPlainC001/cover?v=1", entries["scPlainC001"].CoverUrl);

        // The per-library strip (the sidebar grouping) agrees.
        var byLibrary = (await client.GetFromJsonAsync<List<ContinueReadingEntry>>($"/api/v1/reading/continue/by-library/{LibPubId}", TestJson.Web))!
            .ToDictionary(e => e.ItemId);
        Assert.Equal("/api/v1/items/scV01/cover?v=1", byLibrary["scC005"].CoverUrl);
    }
}
