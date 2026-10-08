namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

/// <summary>
/// Service-with-DB tests for stacks of stories collected in one volume (1.37.0, "tankoubon stacks"): which folders qualify (neither a
/// series nor a collection), which archive links count (Confirmed / Auto), the memoised entry list's key (a link change regroups at
/// once), and the browse cards (title, count, no volume summary, filters). Synthetic rows only.
/// </summary>
public sealed class CollectionStackEntryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public CollectionStackEntryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-collstacks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "collstacks.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext Db, LibraryEntity Library)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        return (db, await VolumeTestData.AddLibraryAsync(db));
    }

    private static string[] Kinds(FolderVolumeEntries view) =>
        view.Entries.Select(e => e.Kind switch
        {
            VolumeEntryKind.CollectionStack => "collection:" + string.Join('+', e.Collection!.Members.Select(m => m.Name)),
            VolumeEntryKind.Stack => "stack:" + e.Stack!.Key,
            VolumeEntryKind.Folder => "folder:" + e.Row!.Name,
            _ => "archive:" + e.Row!.Name,
        }).ToArray();

    private sealed record Artist(CatalogNodeEntity Folder, CatalogNodeEntity A, CatalogNodeEntity B, CatalogNodeEntity C, CatalogNodeEntity D,
        MetadataRecordEntity Tank, MetadataRecordEntity Other);

    // An artist's folder: four stories; B and D linked to one collected volume ("Tank"), C to another record alone, A unlinked.
    private static async Task<Artist> SeedArtistAsync(MangaPixerDbContext db, LibraryEntity lib, SeriesLinkState? folderState = null)
    {
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Sample Artist");
        var a = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Sample Artist - Alpha Story");
        var b = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Sample Artist - Beta Story");
        var c = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Sample Artist - Gamma Story");
        var d = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Sample Artist - Delta Story");
        var tank = await VolumeTestData.AddRecordAsync(db, title: "Synthetic Collected Volume", originVolumes: 1, status: MetadataOriginStatus.Complete);
        var other = await VolumeTestData.AddRecordAsync(db, title: "Synthetic One-shot");
        await VolumeTestData.LinkAsync(db, b, tank.Id, SeriesLinkState.Auto);
        await VolumeTestData.LinkAsync(db, d, tank.Id, SeriesLinkState.Confirmed);
        await VolumeTestData.LinkAsync(db, c, other.Id, SeriesLinkState.Auto);
        if (folderState is { } state)
            await VolumeTestData.LinkAsync(db, folder, state is SeriesLinkState.DontMatch ? null : (await VolumeTestData.AddRecordAsync(db, "Folder Record")).Id, state);
        return new Artist(folder, a, b, c, d, tank, other);
    }

    [Fact]
    public async Task AnUnlinkedFolder_StacksTheStoriesOfOneRecord_InThePlaceOfTheFirst()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var artist = await SeedArtistAsync(db, lib);

        var view = (await new VolumeEntryService(db).GetEntriesAsync(artist.Folder.Id, default))!;

        Assert.True(view.Available);
        Assert.Equal((1, 0), (view.CollectionStackCount, view.StackCount));
        Assert.Null(view.Status);
        Assert.Null(view.SeriesFolderId);
        Assert.Equal(
            ["archive:Sample Artist - Alpha Story", "collection:Sample Artist - Beta Story+Sample Artist - Delta Story", "archive:Sample Artist - Gamma Story"],
            Kinds(view));
        var key = view.Entries.Single(e => e.Kind == VolumeEntryKind.CollectionStack).Collection!.Key;
        Assert.Equal(artist.Tank.PublicId, key);
        Assert.Equal(artist.Tank.Id, view.CollectionRecords[key]);
    }

    [Theory]
    [InlineData(SeriesLinkState.NeedsReview)]
    [InlineData(SeriesLinkState.DontMatch)]
    public async Task AFolderInReviewOrDontMatch_Qualifies(SeriesLinkState state)
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var artist = await SeedArtistAsync(db, lib, state);

        var view = (await new VolumeEntryService(db).GetEntriesAsync(artist.Folder.Id, default))!;

        Assert.Equal(1, view.CollectionStackCount);
        Assert.True(view.Available);
    }

    [Theory]
    [InlineData(SeriesLinkState.Confirmed)]
    [InlineData(SeriesLinkState.Auto)]
    [InlineData(SeriesLinkState.CollectionAbout)]
    public async Task ASeriesFolderOrACollection_NeverStacksStories(SeriesLinkState state)
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var artist = await SeedArtistAsync(db, lib, state);

        var view = (await new VolumeEntryService(db).GetEntriesAsync(artist.Folder.Id, default))!;

        Assert.Equal(0, view.CollectionStackCount);
        Assert.DoesNotContain(view.Entries, e => e.Kind == VolumeEntryKind.CollectionStack);
        Assert.Empty(view.CollectionRecords);
    }

    [Theory]
    [InlineData(SeriesLinkState.Confirmed, false)]
    [InlineData(SeriesLinkState.CollectionAbout, false)]
    [InlineData(SeriesLinkState.NeedsReview, true)]
    public async Task AUnitSubfolder_FollowsTheFolderAboveIt(SeriesLinkState parentState, bool stacks)
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var parent = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Parent Work");
        await VolumeTestData.LinkAsync(db, parent, (await VolumeTestData.AddRecordAsync(db, "Parent Record")).Id, parentState);
        var unit = await VolumeTestData.AddFolderAsync(db, lib.Id, parent.Id, "Volumes");
        var one = await VolumeTestData.AddArchiveAsync(db, lib.Id, unit.Id, "Story One");
        var two = await VolumeTestData.AddArchiveAsync(db, lib.Id, unit.Id, "Story Two");
        var tank = await VolumeTestData.AddRecordAsync(db, "Synthetic Collected Volume");
        await VolumeTestData.LinkAsync(db, one, tank.Id, SeriesLinkState.Auto);
        await VolumeTestData.LinkAsync(db, two, tank.Id, SeriesLinkState.Auto);

        var view = (await new VolumeEntryService(db).GetEntriesAsync(unit.Id, default))!;

        Assert.Equal(stacks ? 1 : 0, view.CollectionStackCount);
    }

    [Theory]
    [InlineData(SeriesLinkState.NeedsReview)]
    [InlineData(SeriesLinkState.DontMatch)]
    public async Task OnlyConfirmedOrAutoArchiveLinksCount(SeriesLinkState notALink)
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Sample Artist");
        var one = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Story One");
        var two = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Story Two");
        var tank = await VolumeTestData.AddRecordAsync(db, "Synthetic Collected Volume");
        await VolumeTestData.LinkAsync(db, one, tank.Id, SeriesLinkState.Auto);
        await VolumeTestData.LinkAsync(db, two, tank.Id, notALink);

        var view = (await new VolumeEntryService(db).GetEntriesAsync(folder.Id, default))!;

        Assert.Equal(0, view.CollectionStackCount);
        Assert.False(view.Available);
    }

    [Fact]
    public async Task ALinkChange_RegroupsAtOnce_WithoutANewCatalogRevision()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Sample Artist");
        var one = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Story One");
        var two = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Story Two");
        var tank = await VolumeTestData.AddRecordAsync(db, "Synthetic Collected Volume");
        var other = await VolumeTestData.AddRecordAsync(db, "Synthetic Other Volume");
        await VolumeTestData.LinkAsync(db, one, tank.Id, SeriesLinkState.Auto);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new VolumeEntryService(db, cache);

        var first = (await service.GetEntriesAsync(folder.Id, default))!;
        Assert.False(first.Available);
        Assert.Same(first, await service.GetEntriesAsync(folder.Id, default));

        // The second story is linked to the same record: the stack appears on the next read (no 5-minute wait, no scan).
        await VolumeTestData.LinkAsync(db, two, tank.Id, SeriesLinkState.Auto);
        var second = (await service.GetEntriesAsync(folder.Id, default))!;
        Assert.Equal(1, second.CollectionStackCount);
        Assert.Same(second, await service.GetEntriesAsync(folder.Id, default));

        // Re-pointed to another record: the stack goes.
        var link = await db.NodeSeriesLinks.SingleAsync(l => l.NodeId == two.Id);
        link.RecordId = other.Id;
        await db.SaveChangesAsync();
        Assert.Equal(0, (await service.GetEntriesAsync(folder.Id, default))!.CollectionStackCount);

        // Back, then removed.
        link.RecordId = tank.Id;
        await db.SaveChangesAsync();
        Assert.Equal(1, (await service.GetEntriesAsync(folder.Id, default))!.CollectionStackCount);
        db.NodeSeriesLinks.Remove(link);
        await db.SaveChangesAsync();
        Assert.Equal(0, (await service.GetEntriesAsync(folder.Id, default))!.CollectionStackCount);
    }

    [Fact]
    public async Task Browse_ShowsOneStackCard_TitledByTheRecord_WithoutAVolumeSummary_AndFolderOrderInFolders()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var artist = await SeedArtistAsync(db, lib);
        var user = await VolumeTestData.AddUserAsync(db);
        var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

        var page = await browse.BrowseAsync(user.Id, lib.Id, artist.Folder.Id, null);

        Assert.Equal(3, page.TotalCount);
        var stack = Assert.Single(page.Items, n => n.Kind == CatalogNodeKind.VolumeStack);
        Assert.Equal($"cs.{artist.Folder.PublicId}.{artist.Tank.PublicId}", stack.Id);
        Assert.Equal("Synthetic Collected Volume", stack.DisplayName);
        Assert.Null(stack.VolumeStack);
        Assert.Equal((artist.Tank.PublicId, "Synthetic Collected Volume", 2),
            (stack.CollectionStack!.Key, stack.CollectionStack.Title, stack.CollectionStack.StoryCount));
        Assert.Equal(artist.Folder.PublicId, stack.ParentId);
        Assert.StartsWith($"/api/v1/items/{artist.B.PublicId}/cover", stack.CoverUrl); // no stored poster: the first story's cover
        Assert.Equal(FolderReadRollup.Unread, stack.ReadRollup);
        Assert.Equal(["Sample Artist - Alpha Story", "Synthetic Collected Volume", "Sample Artist - Gamma Story"], page.Items.Select(n => n.DisplayName));

        var flat = await browse.BrowseAsync(user.Id, lib.Id, artist.Folder.Id, null, group: "flat");
        Assert.Equal(4, flat.TotalCount);
        Assert.All(flat.Items, n => Assert.Equal(CatalogNodeKind.Archive, n.Kind));

        // Read filters work inside the view: one story read -> the stack is "Reading"; the starred story stars the stack.
        db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = artist.D.Id, MarkedAt = DateTimeOffset.UtcNow });
        db.Favorites.Add(new FavoriteEntity { UserId = user.Id, CatalogNodeId = artist.B.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var reading = await browse.BrowseAsync(user.Id, lib.Id, artist.Folder.Id, null, readState: BrowseReadStateFilter.Reading);
        var only = Assert.Single(reading.Items);
        Assert.Equal((stack.Id, FolderReadRollup.Reading, true), (only.Id, only.ReadRollup, only.IsFavorite));
        var favorites = await browse.BrowseAsync(user.Id, lib.Id, artist.Folder.Id, null, favoritesOnly: true);
        Assert.Equal([stack.Id], favorites.Items.Select(n => n.Id));
    }
}
