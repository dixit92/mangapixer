namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Trackers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

/// <summary>
/// Service-with-DB tests for the browse filter by series completion (1.44.0 lane C): the 60-second memo of a level's answers (the first
/// page recomputes, a following page reuses, an old memo or a changed level recomputes - on a shifted clock, not the wall clock), and the
/// filter's combination with Favorites only, Show (read state) and the recency sorts. Synthetic rows.
/// </summary>
public sealed class CatalogBrowseCompletionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public CatalogBrowseCompletionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-completion-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "completion.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private sealed record Seeded(MangaPixerDbContext Db, LibraryEntity Library, UserEntity User, CatalogNodeEntity First, CatalogNodeEntity Second,
        CatalogNodeEntity Running);

    /// <summary>Two finished series held whole ("Have it all") and one running series missing a released volume, at the library root.</summary>
    private async Task<Seeded> SeedAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var lib = await VolumeTestData.AddLibraryAsync(db);
        var user = await VolumeTestData.AddUserAsync(db);

        async Task<CatalogNodeEntity> SeriesAsync(string name, MetadataOriginStatus status, int volumes, int english, int held)
        {
            var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, name);
            for (var v = 1; v <= held; v++)
                await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, $"{name} v{v:00}");
            var record = await VolumeTestData.AddRecordAsync(db, name + " Record", volumes, status, english);
            await VolumeTestData.LinkAsync(db, folder, record.Id);
            return folder;
        }

        var first = await SeriesAsync("Synthetic Alpha", MetadataOriginStatus.Complete, 3, 3, 3);
        var second = await SeriesAsync("Synthetic Beta", MetadataOriginStatus.Complete, 2, 2, 2);
        var running = await SeriesAsync("Synthetic Gamma", MetadataOriginStatus.Ongoing, 8, 4, 2);
        return new Seeded(db, lib, user, first, second, running);
    }

    private static CatalogBrowseService Browse(MangaPixerDbContext db, IMemoryCache? cache = null, TimeProvider? time = null) =>
        new(db, new LibraryAuthorizationService(db), cache: cache, time: time);

    private static async Task TombstoneLastArchiveAsync(MangaPixerDbContext db, CatalogNodeEntity folder, bool tombstoned = true)
    {
        var last = await db.CatalogNodes.Where(n => n.ParentId == folder.Id).OrderByDescending(n => n.SortKey).FirstAsync();
        last.Availability = (int)(tombstoned ? CatalogNodeAvailability.Tombstoned : CatalogNodeAvailability.Available);
        await db.SaveChangesAsync();
        // 1.46.0 lane C: as the scan does after writing nodes (LibraryScanCoordinator, phase 4) - the level's answers are kept by revision.
        await db.Libraries.Where(l => l.Id == folder.LibraryId)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.CatalogRevision, l => l.CatalogRevision + 1));
    }

    [Fact]
    public async Task TheFollowingPages_ReuseTheLevelsAnswers_ForSixtySeconds_TheFirstPageAlwaysRecomputes()
    {
        var s = await SeedAsync();
        using var _ = s.Db;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new ShiftableTimeProvider();
        var browse = Browse(s.Db, cache, clock);

        var first = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, null, pageSize: 1, completion: SeriesAnswer.HaveItAll);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(s.First.PublicId, Assert.Single(first.Items).Id);

        // The second series loses a volume: a following page still lists it (the set the first page started with) ...
        await TombstoneLastArchiveAsync(s.Db, s.Second);
        var next = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, first.NextCursor, pageSize: 1, completion: SeriesAnswer.HaveItAll);
        Assert.Equal(2, next.TotalCount);
        Assert.Equal(s.Second.PublicId, Assert.Single(next.Items).Id);

        // ... a first page recomputes at once.
        var fresh = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, null, pageSize: 1, completion: SeriesAnswer.HaveItAll);
        Assert.Equal(1, fresh.TotalCount);
        Assert.False(fresh.HasMore);

        // Back whole, a first page sees it; lost again, a following page reuses the answers only while they are under 60 s old.
        await TombstoneLastArchiveAsync(s.Db, s.Second, tombstoned: false);
        var again = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, null, pageSize: 1, completion: SeriesAnswer.HaveItAll);
        Assert.Equal(2, again.TotalCount);
        await TombstoneLastArchiveAsync(s.Db, s.Second);
        clock.Advance(CatalogBrowseService.CompletionMemoAge + TimeSpan.FromSeconds(1));
        var late = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, again.NextCursor, pageSize: 1, completion: SeriesAnswer.HaveItAll);
        Assert.Equal(1, late.TotalCount);
        Assert.Empty(late.Items);
    }

    [Fact]
    public async Task AFollowingPage_Recomputes_WhenTheLevelsLinkedSeriesChanged()
    {
        var s = await SeedAsync();
        using var _ = s.Db;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var browse = Browse(s.Db, cache, new ShiftableTimeProvider());

        var first = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, null, pageSize: 1, completion: SeriesAnswer.HaveItAll);
        Assert.Equal(2, first.TotalCount);
        var third = await VolumeTestData.AddFolderAsync(s.Db, s.Library.Id, null, "Synthetic Delta");
        await VolumeTestData.AddArchiveAsync(s.Db, s.Library.Id, third.Id, "Synthetic Delta v01");
        await VolumeTestData.LinkAsync(s.Db, third, (await VolumeTestData.AddRecordAsync(s.Db, "Delta Record", 1, MetadataOriginStatus.Complete, 1)).Id,
            SeriesLinkState.Auto);

        var next = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, first.NextCursor, pageSize: 5, completion: SeriesAnswer.HaveItAll);
        Assert.Equal(3, next.TotalCount);
        Assert.Equal(4, next.LinkedSeries);
        Assert.Equal([s.Second.PublicId, third.PublicId], next.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task WithoutAMemo_EveryPageAnswersTheLevel_AndPagesStayHonest()
    {
        var s = await SeedAsync();
        using var _ = s.Db;
        var browse = Browse(s.Db);

        var first = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, null, pageSize: 1, completion: SeriesAnswer.HaveItAll);
        await TombstoneLastArchiveAsync(s.Db, s.Second);
        var next = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, first.NextCursor, pageSize: 1, completion: SeriesAnswer.HaveItAll);
        Assert.Equal(1, next.TotalCount);
        Assert.Empty(next.Items);
    }

    [Fact]
    public async Task Completion_AndsWithFavoritesOnly_AndShow_AndCountsWhatTheOtherFiltersKept()
    {
        var s = await SeedAsync();
        using var _ = s.Db;
        var browse = Browse(s.Db);
        s.Db.Favorites.Add(new FavoriteEntity { UserId = s.User.Id, CatalogNodeId = s.Second.Id, CreatedAt = DateTimeOffset.UtcNow });
        s.Db.Favorites.Add(new FavoriteEntity { UserId = s.User.Id, CatalogNodeId = s.Running.Id, CreatedAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync();

        var starred = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, null, favoritesOnly: true, completion: SeriesAnswer.HaveItAll);
        Assert.Equal(s.Second.PublicId, Assert.Single(starred.Items).Id);
        Assert.Equal(1, starred.TotalCount);
        Assert.Equal((1, 1), (starred.CompletionCounts!.HaveItAll, starred.CompletionCounts.MissingSome));
        Assert.Equal(3, starred.LinkedSeries); // the level, before any filter

        // Show Read: one archive of the first series read is not "Read" for the folder; mark them all and it is.
        var firstArchives = await s.Db.CatalogNodes.Where(n => n.ParentId == s.First.Id).Select(n => n.Id).ToListAsync();
        foreach (var id in firstArchives)
            s.Db.ReadMarks.Add(new ReadMarkEntity { UserId = s.User.Id, ItemId = id, MarkedAt = DateTimeOffset.UtcNow });
        await s.Db.SaveChangesAsync();
        var read = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, null, readState: BrowseReadStateFilter.Read, completion: SeriesAnswer.HaveItAll);
        Assert.Equal(s.First.PublicId, Assert.Single(read.Items).Id);
        Assert.Equal(1, read.CompletionCounts!.HaveItAll);
    }

    [Theory]
    [InlineData("recentlyAdded")]
    [InlineData("recentlyUpdated")]
    [InlineData("recentlyRead")]
    [InlineData("name")]
    public async Task Completion_PagesHonestly_UnderEverySort(string sort)
    {
        var s = await SeedAsync();
        using var _ = s.Db;
        var browse = Browse(s.Db);

        var seen = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 4; i++)
        {
            var page = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, cursor, pageSize: 1, sort: sort, completion: SeriesAnswer.HaveItAll);
            Assert.Equal(2, page.TotalCount);
            seen.AddRange(page.Items.Select(n => n.Id));
            if (!page.HasMore)
                break;
            cursor = page.NextCursor;
        }
        Assert.Equal([s.First.PublicId, s.Second.PublicId], seen.Order());
    }
}
