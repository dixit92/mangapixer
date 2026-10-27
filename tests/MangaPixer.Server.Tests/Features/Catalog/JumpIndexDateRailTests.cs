namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.49.0 lane J (J4, owner decision 14 b): under the date sorts the rail is a row of time chips, and every chip's cursor is browse's own date
/// cursor - handed to browse with the same sort it lands on the chip's first row and the next <c>Count</c> rows are the chip's rows. Synthetic
/// rows with ties on the timestamp (the id breaks them), folders with and without a newest descendant, and a fixed clock.
/// </summary>
public sealed class JumpIndexDateRailTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public JumpIndexDateRailTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-daterail-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "daterail.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<(MangaPixerDbContext Db, long UserId, long LibraryId)> SeedAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var library = await VolumeTestData.AddLibraryAsync(db);
        var user = await VolumeTestData.AddUserAsync(db);

        // Archives: ages in hours; two pairs share a timestamp. Letters-only names (no number reads as a chapter).
        var ages = new[] { 0, 2, 2, 30, 60, 192, 480, 480, 1000, 4800, 9600, 19200 };
        for (var i = 0; i < ages.Length; i++)
        {
            var node = await VolumeTestData.AddArchiveAsync(db, library.Id, null, $"Story {(char)('A' + i)}");
            node.CreatedAt = s_now.AddHours(-ages[i]);
        }
        // Folders: created at different times; their newest descendant (recentlyUpdated) is set for two of three.
        var folderAges = new[] { 1, 240, 12000 };
        for (var i = 0; i < folderAges.Length; i++)
        {
            var folder = await VolumeTestData.AddFolderAsync(db, library.Id, null, $"Series {(char)('A' + i)}");
            folder.CreatedAt = s_now.AddHours(-folderAges[i]);
            folder.LatestDescendantAddedAt = i switch { 0 => s_now.AddHours(-1), 1 => s_now.AddHours(-2400), _ => null };
        }
        await db.SaveChangesAsync();
        return (db, user.Id, library.Id);
    }

    /// <summary>The ids of the whole listing in the browse order of <paramref name="sort"/>, read page by page.</summary>
    private static async Task<List<string>> ListingAsync(CatalogBrowseService browse, long userId, long libraryId, string sort)
    {
        var ids = new List<string>();
        string? cursor = null;
        while (true)
        {
            var page = await browse.BrowseAsync(userId, libraryId, null, cursor, pageSize: 4, sort: sort);
            ids.AddRange(page.Items.Select(i => i.Id));
            if (!page.HasMore)
                return ids;
            cursor = page.NextCursor;
        }
    }

    [Theory]
    [InlineData("recentlyAdded")]
    [InlineData("recentlyUpdated")]
    public async Task EveryChipsCursor_LandsBrowseOnItsFirstRow_AndItsCountIsItsRows(string sort)
    {
        var (db, userId, libraryId) = await SeedAsync();
        await using var dbScope = db;
        var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
        var service = new JumpIndexService(db, browse: browse, time: new FixedTime(s_now));

        var rail = await service.GetJumpIndexAsync(userId, libraryId, null, false, null, false, LevelFilter.None, sort, 0);
        var listing = await ListingAsync(browse, userId, libraryId, sort);

        Assert.Equal(JumpIndexBuilder.Dates, rail.Mode);
        Assert.Equal("desc", rail.Direction);
        Assert.True(rail.Buckets.Count >= 5, "expected several time chips, got " + string.Join(",", rail.Buckets.Select(b => b.Label)));
        Assert.Equal(listing.Count, rail.Buckets.Sum(b => b.Count));

        var position = 0;
        foreach (var bucket in rail.Buckets)
        {
            Assert.Equal(listing[position], bucket.FirstNodeId);
            var page = await browse.BrowseAsync(userId, libraryId, null, bucket.FirstCursor, pageSize: bucket.Count, sort: sort);
            Assert.Equal(listing.Skip(position).Take(bucket.Count), page.Items.Select(i => i.Id));
            position += bucket.Count;
        }
        Assert.Null(rail.Buckets[0].FirstCursor);
        Assert.Contains(rail.Buckets, b => b.Label == "Today");
    }

    [Fact]
    public async Task RecentlyAdded_FoldersAndFilesHaveTheirOwnChips_AndRecentlyUpdatedInterleaves()
    {
        var (db, userId, libraryId) = await SeedAsync();
        await using var dbScope = db;
        var service = new JumpIndexService(db, time: new FixedTime(s_now));

        var added = await service.GetJumpIndexAsync(userId, libraryId, null, false, null, false, LevelFilter.None, "recentlyAdded", 0);
        Assert.Equal(["Today", "This month", "2025", "Today", "This week", "This month", "This year", "2025", "2024"],
            added.Buckets.Select(b => b.Label));
        Assert.Equal(Enumerable.Repeat("folders", 3).Concat(Enumerable.Repeat("files", 6)), added.Buckets.Select(b => b.Group));

        var updated = await service.GetJumpIndexAsync(userId, libraryId, null, false, null, false, LevelFilter.None, "recentlyUpdated", 0);
        // One interleaved list: no groups. The folder without a newest descendant closes the rail as "Other".
        Assert.All(updated.Buckets, b => Assert.Null(b.Group));
        Assert.Equal("Other", updated.Buckets[^1].Label);
        Assert.Equal(1, updated.Buckets[^1].Count);
    }

    [Fact]
    public async Task TheDateRail_FollowsTheFilter_AndRecentlyReadAndNameSortAreNotDateRails()
    {
        var (db, userId, libraryId) = await SeedAsync();
        await using var dbScope = db;
        var service = new JumpIndexService(db, time: new FixedTime(s_now));

        // Favorites only with nothing starred: an empty level, an empty rail.
        var none = await service.GetJumpIndexAsync(userId, libraryId, null, false, null, false,
            new LevelFilter(FavoritesOnly: true), "recentlyAdded", 0);
        Assert.Empty(none.Buckets);

        // recentlyRead has no rail; an unknown sort is the name sort (letters).
        var read = await service.GetJumpIndexAsync(userId, libraryId, null, false, null, false, LevelFilter.None, "recentlyRead", 0);
        Assert.Empty(read.Buckets);
        var name = await service.GetJumpIndexAsync(userId, libraryId, null, false, null, false, LevelFilter.None, "bogus", 0);
        Assert.Equal(JumpIndexBuilder.Letters, name.Mode);
        Assert.NotEmpty(name.Buckets);
    }

    [Fact]
    public async Task TheViewersOffset_MovesTodayAcrossMidnight()
    {
        var (db, userId, libraryId) = await SeedAsync();
        await using var dbScope = db;
        var service = new JumpIndexService(db, time: new FixedTime(s_now));

        // The archives at 0 h and 2 h are 12:00 and 10:00 UTC on the 28th: today in UTC, but for a viewer at UTC-11 (01:00 on the 28th) the
        // 10:00 one was still the evening of the 27th - so one chip's count changes.
        var utc = await service.GetJumpIndexAsync(userId, libraryId, null, false, null, false, LevelFilter.None, "recentlyAdded", 0);
        var west = await service.GetJumpIndexAsync(userId, libraryId, null, false, null, false, LevelFilter.None, "recentlyAdded", -11 * 60);
        Assert.NotEqual(utc.Buckets.Select(b => b.Count), west.Buckets.Select(b => b.Count));
    }
}
