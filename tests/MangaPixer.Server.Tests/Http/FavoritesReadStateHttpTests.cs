namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for the read state of <c>GET /api/v1/favorites</c> (1.48.0 lane U): a favorited folder and a
/// stack carry the same Read / Reading rollup as a browse card (a stack's covers the WHOLE folder, not only its starred members), an
/// archive favorite inside a linked series gets that series' (i) as the home Continue row does (anchored), "Show series information" off
/// still wins (per library and everywhere), and a reader sees only their own marks. Synthetic names, stored rows only.
/// </summary>
public sealed class FavoritesReadStateHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "frslib1";
    private const string HiddenLibPubId = "frslib2";
    private readonly MangaPixerWebApplicationFactory _factory;

    public FavoritesReadStateHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Library 1:
    //   frsSeries (linked series folder): frsSeries1 .. frsSeries3
    //   frsDone (plain folder): frsDone1, frsDone2
    //   frsFresh (plain folder): frsFresh1
    //   frsStackDir (plain folder): frsStackA, frsStackB, frsStackC (A and B are starred as a stack, C is not)
    //   frsEmpty (folder with no archive)
    // Library 2 (series information hidden): frsHidSeries (linked): frsHid1
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;

        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Fav Read State", RootPath = "/synthetic/frs1", CreatedAt = DateTimeOffset.UtcNow };
        var hiddenLib = new LibraryEntity
        {
            PublicId = HiddenLibPubId,
            DisplayName = "Fav Read State Hidden",
            RootPath = "/synthetic/frs2",
            CreatedAt = DateTimeOffset.UtcNow,
            MetadataSeriesInfoHidden = true,
        };
        db.Libraries.AddRange(lib, hiddenLib);
        await db.SaveChangesAsync();

        var record = new MetadataRecordEntity
        {
            PublicId = "frsrec",
            Provider = "mangaupdates",
            ExternalId = "frs1",
            Title = "Fav Read State Record",
            FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();

        async Task<CatalogNodeEntity> FolderAsync(string pub, LibraryEntity l, string name, bool linked = false)
        {
            var folder = Node(pub, l.Id, null, 0, name);
            db.CatalogNodes.Add(folder);
            await db.SaveChangesAsync();
            if (linked)
            {
                db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
                {
                    NodeId = folder.Id,
                    LibraryId = l.Id,
                    State = (int)SeriesLinkState.Confirmed,
                    RecordId = record.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            return folder;
        }

        async Task ArchivesAsync(CatalogNodeEntity folder, LibraryEntity l, params string[] pubs)
        {
            foreach (var pub in pubs)
            {
                var arc = Node(pub, l.Id, folder.Id, 1, pub);
                db.CatalogNodes.Add(arc);
                await db.SaveChangesAsync();
                db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = arc.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 4 });
            }
            await db.SaveChangesAsync();
        }

        await ArchivesAsync(await FolderAsync("frsSeries", lib, "Series Folder", linked: true), lib, "frsSeries1", "frsSeries2", "frsSeries3");
        await ArchivesAsync(await FolderAsync("frsDone", lib, "Done Folder"), lib, "frsDone1", "frsDone2");
        await ArchivesAsync(await FolderAsync("frsFresh", lib, "Fresh Folder"), lib, "frsFresh1");
        await ArchivesAsync(await FolderAsync("frsStackDir", lib, "Stack Folder"), lib, "frsStackA", "frsStackB", "frsStackC");
        await FolderAsync("frsEmpty", lib, "Empty Folder");
        await ArchivesAsync(await FolderAsync("frsHidSeries", hiddenLib, "Hidden Series Folder", linked: true), hiddenLib, "frsHid1");
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

    private async Task MarkAsync(string username, ReadingState? progress, params string[] publicIds)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var user = await db.Users.SingleAsync(u => u.NormalizedUserName == username.ToUpperInvariant());
        foreach (var pub in publicIds)
        {
            var node = await db.CatalogNodes.SingleAsync(n => n.PublicId == pub);
            if (progress is null)
            {
                db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = node.Id, MarkedAt = DateTimeOffset.UtcNow });
            }
            else
            {
                db.ReadingProgress.Add(new ReadingProgressEntity
                {
                    UserId = user.Id,
                    ItemId = node.Id,
                    ContentVersion = 1,
                    Ordinal = 2,
                    State = (int)progress.Value,
                    Revision = 1,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task ClearStateAsync(params string[] usernames)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var names = usernames.Select(u => u.ToUpperInvariant()).ToList();
        var ids = await db.Users.Where(u => names.Contains(u.NormalizedUserName)).Select(u => u.Id).ToListAsync();
        db.ReadMarks.RemoveRange(db.ReadMarks.Where(m => ids.Contains(m.UserId)));
        db.ReadingProgress.RemoveRange(db.ReadingProgress.Where(p => ids.Contains(p.UserId)));
        db.Favorites.RemoveRange(db.Favorites.Where(f => ids.Contains(f.UserId)));
        await db.SaveChangesAsync();
    }

    private static async Task StarAsync(HttpClient client, params string[] publicIds)
    {
        foreach (var pub in publicIds)
        {
            (await client.PostAsync($"/api/v1/nodes/{pub}/favorite", null)).EnsureSuccessStatusCode();
            await Task.Delay(5);
        }
    }

    private static async Task<Dictionary<string, CatalogNodeDto>> FavoritesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/favorites?pageSize=100");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(TestJson.Web);
        return page!.Items.ToDictionary(i => i.Id);
    }

    private async Task<HttpClient> AdminAsync()
    {
        await SeedAsync();
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        await ClearStateAsync("admin");
        return admin;
    }

    [Fact]
    public async Task Favorites_FolderStackAndArchive_CarryTheirReadState()
    {
        var admin = await AdminAsync();
        await MarkAsync("admin", null, "frsSeries1", "frsDone1", "frsDone2", "frsStackA");
        await MarkAsync("admin", ReadingState.InProgress, "frsSeries2");
        await StarAsync(admin, "frsSeries", "frsDone", "frsFresh", "frsEmpty", "frsStackA", "frsStackB", "frsSeries3", "frsDone1");

        var items = await FavoritesAsync(admin);

        Assert.Equal(FolderReadRollup.Reading, items["frsSeries"].ReadRollup);   // one read + one in progress of three
        Assert.Equal(FolderReadRollup.Read, items["frsDone"].ReadRollup);        // every archive read
        Assert.Equal(FolderReadRollup.Unread, items["frsFresh"].ReadRollup);     // nothing read
        Assert.Null(items["frsEmpty"].ReadRollup);                               // no archive to roll up: no badge
        // The stack stands for its folder and its rollup covers the WHOLE folder: A read, B and C unread = Reading (not 2 starred of 2).
        Assert.Equal(2, items["frsStackDir"].FavoriteStackCount);
        Assert.Equal(FolderReadRollup.Reading, items["frsStackDir"].ReadRollup);
        // Archives keep their own state and carry no rollup.
        Assert.True(items["frsDone1"].IsRead);
        Assert.Null(items["frsDone1"].ReadRollup);
        Assert.Null(items["frsSeries3"].ReadRollup);
        Assert.False(items["frsSeries3"].IsRead);
    }

    [Fact]
    public async Task Favorites_ARestrictedReader_SeesOnlyTheirOwnMarks()
    {
        var admin = await AdminAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/admin/users",
            new CreateUserRequest { Username = "frsreader", Password = "FrsReaderPass123!", IsAdmin = false });
        if (created.IsSuccessStatusCode)
        {
            var user = (await created.Content.ReadFromJsonAsync<CreateUserResponse>(TestJson.Web))!;
            (await admin.PutAsync($"/api/v1/admin/users/{user.User.Id}/grants/{LibPubId}", null)).EnsureSuccessStatusCode();
        }
        var reader = await LoginAndChangePasswordAsync("frsreader", "FrsReaderPass123!", "FrsReaderPass456!");
        await ClearStateAsync("frsreader");

        // The admin has read the whole folder; the reader has read one chapter of it.
        await MarkAsync("admin", null, "frsDone1", "frsDone2", "frsSeries1", "frsSeries2", "frsSeries3");
        await MarkAsync("frsreader", null, "frsSeries1");
        await StarAsync(admin, "frsDone", "frsSeries");
        await StarAsync(reader, "frsDone", "frsSeries");

        var adminItems = await FavoritesAsync(admin);
        Assert.Equal(FolderReadRollup.Read, adminItems["frsDone"].ReadRollup);
        Assert.Equal(FolderReadRollup.Read, adminItems["frsSeries"].ReadRollup);

        var readerItems = await FavoritesAsync(reader);
        Assert.Equal(FolderReadRollup.Unread, readerItems["frsDone"].ReadRollup);
        Assert.Equal(FolderReadRollup.Reading, readerItems["frsSeries"].ReadRollup);

        // A favorite pointing into a library the reader has no grant to (a grant revoked later) is not listed at all.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var readerRow = await db.Users.SingleAsync(u => u.NormalizedUserName == "FRSREADER");
            var hidden = await db.CatalogNodes.SingleAsync(n => n.PublicId == "frsHidSeries");
            db.Favorites.Add(new FavoriteEntity { UserId = readerRow.Id, CatalogNodeId = hidden.Id, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.DoesNotContain("frsHidSeries", (await FavoritesAsync(reader)).Keys);
        await ClearStateAsync("frsreader");
    }

    [Fact]
    public async Task Favorites_AnArchiveInsideALinkedSeries_GetsTheSeriesInfo_AsTheContinueRowDoes()
    {
        var admin = await AdminAsync();
        await StarAsync(admin, "frsSeries2", "frsSeries", "frsDone1");

        var items = await FavoritesAsync(admin);

        Assert.True(items["frsSeries2"].HasSeriesInfo);   // anchored: the linked series folder above it
        Assert.True(items["frsSeries"].HasSeriesInfo);    // the folder's own link
        Assert.False(items["frsDone1"].HasSeriesInfo);    // nothing linked above it
    }

    [Fact]
    public async Task Favorites_SeriesInfoOff_LibraryOrEverywhere_WinsOverTheAnchor()
    {
        var admin = await AdminAsync();
        await StarAsync(admin, "frsSeries2", "frsSeries", "frsHid1", "frsHidSeries");
        await MarkAsync("admin", null, "frsHid1");

        var items = await FavoritesAsync(admin);
        Assert.True(items["frsSeries2"].HasSeriesInfo);
        Assert.True(items["frsSeries"].HasSeriesInfo);
        // The library with series information off never gets the (i), even though a linked series sits right above its archive.
        Assert.False(items["frsHid1"].HasSeriesInfo);
        Assert.False(items["frsHidSeries"].HasSeriesInfo);
        // Read state is not series information: the rollup and the archive's mark stay.
        Assert.Equal(FolderReadRollup.Read, items["frsHidSeries"].ReadRollup);
        Assert.True(items["frsHid1"].IsRead);

        await SetGlobalHiddenAsync(true);
        try
        {
            var off = await FavoritesAsync(admin);
            Assert.DoesNotContain(off.Values, i => i.HasSeriesInfo);
            Assert.Equal(FolderReadRollup.Unread, off["frsSeries"].ReadRollup);
        }
        finally
        {
            await SetGlobalHiddenAsync(false);
        }

        Assert.True((await FavoritesAsync(admin))["frsSeries2"].HasSeriesInfo);
    }

    private async Task SetGlobalHiddenAsync(bool hidden)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var settings = await db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId);
        if (settings is null)
        {
            settings = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
            db.AppSettings.Add(settings);
        }
        settings.MetadataSeriesInfoHidden = hidden;
        await db.SaveChangesAsync();
    }

    private async Task<HttpClient> LoginAndChangePasswordAsync(string username, string oldPassword, string newPassword)
    {
        // The account may already have its final password (a rerun on the shared fixture).
        var direct = _factory.CreateClient();
        if ((await direct.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = newPassword })).IsSuccessStatusCode)
        {
            var directCsrf = await direct.GetFromJsonAsync<CsrfTokenDto>("/api/v1/auth/csrf");
            direct.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", directCsrf!.Token);
            return direct;
        }

        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = oldPassword })).EnsureSuccessStatusCode();
        var csrf = await client.GetFromJsonAsync<CsrfTokenDto>("/api/v1/auth/csrf");
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
        (await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest { CurrentPassword = oldPassword, NewPassword = newPassword })).EnsureSuccessStatusCode();
        var fresh = _factory.CreateClient();
        (await fresh.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = newPassword })).EnsureSuccessStatusCode();
        var freshCsrf = await fresh.GetFromJsonAsync<CsrfTokenDto>("/api/v1/auth/csrf");
        fresh.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", freshCsrf!.Token);
        return fresh;
    }
}
