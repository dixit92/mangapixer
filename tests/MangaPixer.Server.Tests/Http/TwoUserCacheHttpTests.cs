namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.48.0 lane A (access invariants, gap 2): the shared caches through HTTP with TWO users - the admin (A) warms every cache first, then
/// a restricted reader (B: granted G and P, P is B's Private library, U not granted) reads. Home stacks and their covers, the Completion
/// answers and counts, the facet counts (<c>FacetCountCache</c>) and the library item counts: B sees only G and P (only G under
/// Incognito), with B's own read state and stars - never A's. One seeded host for the class.
/// </summary>
public sealed class TwoUserCacheHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static readonly Dictionary<MangaPixerWebApplicationFactory, TwoUserWorld> Worlds = [];
    private readonly MangaPixerWebApplicationFactory _factory;

    public TwoUserCacheHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<TwoUserWorld> WorldAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (Worlds.TryGetValue(_factory, out var world))
                return world;
            var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
            return Worlds[_factory] = await TwoUserSeed.SeedAsync(_factory, admin, "tureader");
        }
        finally
        {
            SeedLock.Release();
        }
    }

    [Fact]
    public async Task HomeStacks_AndTheirCovers_WarmedByTheAdmin_ShowTheReaderOnlyTheirLibraries_WithTheirOwnReadStateAndStars()
    {
        var w = await WorldAsync();
        var asAdmin = await RecentAsync(w.Admin);
        Assert.Equal([w.G.PublicId, w.P.PublicId, w.U.PublicId], asAdmin.Libraries.Select(l => l.LibraryId).Order(StringComparer.Ordinal).ToArray());
        var adminSaga = asAdmin.Libraries.Single(l => l.LibraryId == w.G.PublicId).Stacks.Single(s => s.Id == w.G.SeriesPublicId);
        Assert.Equal("read", adminSaga.ReadState);
        Assert.True(adminSaga.IsFavorite);

        var asReader = await RecentAsync(w.Reader.Client);
        Assert.Equal([w.G.PublicId, w.P.PublicId], asReader.Libraries.Select(l => l.LibraryId).Order(StringComparer.Ordinal).ToArray());
        var readerSaga = asReader.Libraries.Single(l => l.LibraryId == w.G.PublicId).Stacks.Single(s => s.Id == w.G.SeriesPublicId);
        Assert.Equal("unread", readerSaga.ReadState); // A's read marks are A's
        Assert.False(readerSaga.IsFavorite); // and so is A's star
        Assert.Equal(adminSaga.NewCount, readerSaga.NewCount);
        Assert.Equal(adminSaga.CoverUrl, readerSaga.CoverUrl); // the cover is the node's, the same for everyone

        var incognito = await RecentAsync(w.Reader.Incognito);
        Assert.Equal([w.G.PublicId], incognito.Libraries.Select(l => l.LibraryId).ToArray());

        // No stack, cover or name of U (or of P under Incognito) anywhere in B's answers.
        var raw = await w.Reader.Client.GetStringAsync("/api/v1/home/recent-chapters");
        Assert.DoesNotContain(w.U.Word, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(w.U.SeriesPublicId, raw, StringComparison.Ordinal);
        var rawIncognito = await w.Reader.Incognito.GetStringAsync("/api/v1/home/recent-chapters");
        Assert.DoesNotContain(w.P.Word, rawIncognito, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completion_WarmedByTheAdmin_GivesTheReaderTheSameAnswersForGrantedLevels_AndNothingForOthers()
    {
        var w = await WorldAsync();
        foreach (var lib in new[] { w.G, w.U, w.P })
            Assert.Equal(1, (await BrowseAsync(w.Admin, lib, "completion=HaveItAll")).TotalCount); // warms every level's answers
        var adminRoot = await BrowseAsync(w.Admin, w.G, string.Empty);

        var readerRoot = await BrowseAsync(w.Reader.Client, w.G, string.Empty);
        Assert.Equal(adminRoot.LinkedSeries, readerRoot.LinkedSeries);
        Assert.Equal(adminRoot.CompletionCounts, readerRoot.CompletionCounts);
        Assert.Equal([w.G.SeriesPublicId], (await BrowseAsync(w.Reader.Client, w.G, "completion=HaveItAll")).Items.Select(i => i.Id).ToArray());

        foreach (var (client, lib) in new[] { (w.Reader.Client, w.U), (w.Reader.Incognito, w.P) })
        {
            foreach (var query in new[] { string.Empty, "completion=HaveItAll" })
            {
                var page = await BrowseAsync(client, lib, query);
                Assert.Empty(page.Items);
                Assert.Equal(0, page.TotalCount);
                Assert.True(page.LinkedSeries is null or 0, $"linked series {page.LinkedSeries} for {lib.PublicId}");
                Assert.Null(page.CompletionCounts);
            }
        }
        Assert.Equal(1, (await BrowseAsync(w.Reader.Client, w.P, "completion=HaveItAll")).TotalCount); // P without Incognito: B's own
    }

    [Fact]
    public async Task FacetCounts_WarmedByTheAdmin_CountOnlyTheReadersVisibleLibraries()
    {
        var w = await WorldAsync();
        // Each library holds two works carrying the shared genre: A sees 6, B sees 4 (G + P), B under Incognito 2 (G).
        var cases = new[] { (w.Admin, 6), (w.Reader.Client, 4), (w.Reader.Incognito, 2) };
        foreach (var (client, expected) in cases.Concat(cases))
        {
            var panel = await FacetQueryTestSupport.GetAsync<InfoPanelDto>(client, $"/api/v1/libraries/{w.G.PublicId}/info");
            Assert.Equal(expected, FacetQueryTestSupport.Chip(panel, w.SharedGenreId)?.Count);
            var search = await FacetQueryTestSupport.GetAsync<SearchResultsDto>(client,
                "/api/v1/search?f=" + Uri.EscapeDataString("genre:" + w.SharedGenreId) + "&pageSize=50");
            Assert.Equal(expected, search.TotalCount);
        }
        var readerView = await FacetQueryTestSupport.GetRawAsync(w.Reader.Client, "/api/v1/search?f=" + Uri.EscapeDataString("genre:" + w.SharedGenreId));
        Assert.DoesNotContain(w.U.Word, readerView, StringComparison.Ordinal);
        var incognitoView = await FacetQueryTestSupport.GetRawAsync(w.Reader.Incognito, "/api/v1/search?f=" + Uri.EscapeDataString("genre:" + w.SharedGenreId));
        Assert.DoesNotContain(w.P.Word, incognitoView, StringComparison.Ordinal);
        // U's own panel: not the reader's (404, like an unknown library), even right after the admin read it.
        await FacetQueryTestSupport.GetAsync<InfoPanelDto>(w.Admin, $"/api/v1/libraries/{w.U.PublicId}/info");
        Assert.Equal(HttpStatusCode.NotFound, await FacetQueryTestSupport.StatusAsync(w.Reader.Client, $"/api/v1/libraries/{w.U.PublicId}/info"));
    }

    [Fact]
    public async Task LibraryItemCounts_WarmedByTheAdmin_AreTheSameNumbers_ForTheReadersLibrariesOnly()
    {
        var w = await WorldAsync();
        var asAdmin = (await w.Admin.GetFromJsonAsync<List<LibraryDto>>("/api/v1/libraries", TestJson.Web))!.Where(l => l.Id.StartsWith("tu", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, asAdmin.Count);
        var asReader = (await w.Reader.Client.GetFromJsonAsync<List<LibraryDto>>("/api/v1/libraries", TestJson.Web))!;
        Assert.Equal([w.G.PublicId, w.P.PublicId], asReader.Select(l => l.Id).Order(StringComparer.Ordinal).ToArray());
        foreach (var library in asReader)
            Assert.Equal(asAdmin.Single(l => l.Id == library.Id).ItemCount, library.ItemCount);
        Assert.Equal(3, asReader.Single(l => l.Id == w.G.PublicId).ItemCount);
        var incognito = (await w.Reader.Incognito.GetFromJsonAsync<List<LibraryDto>>("/api/v1/libraries", TestJson.Web))!;
        Assert.Equal([w.G.PublicId], incognito.Select(l => l.Id).ToArray());
    }

    internal static async Task<RecentChaptersDto> RecentAsync(HttpClient client)
    {
        var dto = (await client.GetFromJsonAsync<RecentChaptersDto>("/api/v1/home/recent-chapters?perLibrary=10", TestJson.Web))!;
        // Only this world's libraries (a shared fixture may hold others).
        return dto with { Libraries = dto.Libraries.Where(l => l.LibraryId.StartsWith("tu", StringComparison.Ordinal)).ToList() };
    }

    internal sealed record BrowseItem(string Id, string DisplayName);

    internal sealed record BrowseCounts(int HaveItAll, int FinishedMissing, int UpToDate, int MissingSome, int CantTell);

    internal sealed record BrowsePage(List<BrowseItem> Items, int TotalCount, int? LinkedSeries, BrowseCounts? CompletionCounts);

    internal static async Task<BrowsePage> BrowseAsync(HttpClient client, TwoUserLibrary lib, string query)
    {
        var response = await client.GetAsync($"/api/v1/libraries/{lib.PublicId}/browse?sort=name&direction=asc&pageSize=50&{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BrowsePage>(TestJson.Web))!;
    }
}

/// <summary>One library of the two-user world: a complete linked series (two volumes, both on disk) and a plain shelf folder.</summary>
internal sealed record TwoUserLibrary(string Word, long Id, string PublicId, long SeriesId, string SeriesPublicId, string ShelfPublicId, IReadOnlyList<long> SeriesArchives);

internal sealed record TwoUserWorld(HttpClient Admin, AccessReader Reader, TwoUserLibrary G, TwoUserLibrary U, TwoUserLibrary P, string SharedGenreId);

/// <summary>
/// Seeds the two-user world (1.48.0 lane A): libraries G, U and P (synthetic words), each with a complete linked series of two recent
/// volumes and a shelf folder with one archive, two works per library carrying a shared genre; the admin has read the G series and starred
/// it; the reader is granted G and P, with P Private.
/// </summary>
internal static class TwoUserSeed
{
    public static async Task<TwoUserWorld> SeedAsync(WebApplicationFactory<Program> factory, HttpClient admin, string readerName)
    {
        var g = await LibraryAsync(factory, "tuG", "Brightvale");
        var u = await LibraryAsync(factory, "tuU", "Hollowmere");
        var p = await LibraryAsync(factory, "tuP", "Quietfen");

        var s = new FacetQueryTestSupport(factory);
        var shared = await s.ValueAsync(FacetBucket.Genres, "tu-shared", "Twofold Shared Genre");
        foreach (var lib in new[] { g, u, p })
        {
            var saga = await s.WorkAsync("tu-saga-" + lib.PublicId, lib.Word + " Saga");
            var shelf = await s.WorkAsync("tu-shelf-" + lib.PublicId, lib.Word + " Shelf");
            await s.EntryAsync(saga, lib.SeriesId, lib.Id, shared.Id);
            await s.EntryAsync(shelf, await s.WithDbAsync(db => db.CatalogNodes.Where(n => n.PublicId == lib.ShelfPublicId).Select(n => n.Id).SingleAsync()),
                lib.Id, shared.Id);
        }
        s.Bump();

        // The admin's own marks: the G series read and starred.
        await s.WithDbAsync(async db =>
        {
            var adminId = await db.Users.Where(x => x.NormalizedUserName == "ADMIN").Select(x => x.Id).SingleAsync();
            foreach (var archive in g.SeriesArchives)
                db.ReadMarks.Add(new ReadMarkEntity { UserId = adminId, ItemId = archive, MarkedAt = DateTimeOffset.UtcNow, Source = "manual" });
            db.Favorites.Add(new FavoriteEntity { UserId = adminId, CatalogNodeId = g.SeriesId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

        var reader = await AccessTestUsers.ReaderAsync(factory, admin, readerName, [g.Id, p.Id], [p.Id]);
        return new TwoUserWorld(admin, reader, g, u, p, shared.PublicId);
    }

    private static async Task<TwoUserLibrary> LibraryAsync(WebApplicationFactory<Program> factory, string publicId, string word)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var now = DateTimeOffset.UtcNow;
        var library = new LibraryEntity { PublicId = publicId, DisplayName = word + " Library", RootPath = "/synthetic/" + publicId, CreatedAt = now.AddDays(-2) };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        var series = Node(library.Id, null, CatalogNodeKind.Folder, publicId + "ser", word + " Saga", now.AddHours(-2));
        var shelf = Node(library.Id, null, CatalogNodeKind.Folder, publicId + "shf", word + " Shelf", now.AddHours(-2));
        db.CatalogNodes.AddRange(series, shelf);
        await db.SaveChangesAsync();
        var archives = new[]
        {
            Node(library.Id, series.Id, CatalogNodeKind.Archive, publicId + "v1", word + " Saga v01", now.AddHours(-1)),
            Node(library.Id, series.Id, CatalogNodeKind.Archive, publicId + "v2", word + " Saga v02", now.AddMinutes(-50)),
            Node(library.Id, shelf.Id, CatalogNodeKind.Archive, publicId + "sh1", word + " Shelf 01", now.AddDays(-1)),
        };
        db.CatalogNodes.AddRange(archives);
        await db.SaveChangesAsync();
        foreach (var archive in archives)
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = archive.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 2 });

        var record = new MetadataRecordEntity
        {
            PublicId = publicId + "rec",
            Provider = "mangaupdates",
            ExternalId = publicId + "1",
            Title = word + " Saga Record",
            Origin = (int)MetadataOrigin.Japan,
            OriginStatus = (int)MetadataOriginStatus.Complete,
            OriginVolumes = 2,
            PublishersJson = "[{\"name\":\"Synthetic Press\",\"kind\":\"english\",\"volumes\":2,\"status\":\"complete\"}]",
            FetchedAt = now,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = series.Id,
            LibraryId = library.Id,
            State = (int)SeriesLinkState.Confirmed,
            RecordId = record.Id,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return new TwoUserLibrary(word, library.Id, library.PublicId, series.Id, series.PublicId, shelf.PublicId, [archives[0].Id, archives[1].Id]);
    }

    private static CatalogNodeEntity Node(long libraryId, long? parentId, CatalogNodeKind kind, string publicId, string name, DateTimeOffset createdAt) => new()
    {
        PublicId = publicId,
        LibraryId = libraryId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = publicId,
        PathKey = publicId,
        SortKey = SortKey.ForNode(kind, name),
        Availability = (int)CatalogNodeAvailability.Available,
        CreatedAt = createdAt,
    };
}
