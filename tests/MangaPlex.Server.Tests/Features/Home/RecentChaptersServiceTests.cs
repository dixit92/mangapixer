using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Features.Home;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace com.lifepixer.mangaplex.Tests.Server.Features.Home;

/// <summary>
/// Service-with-DB tests for the RecentChaptersService stacking rewrite (1.12.0). Uses real
/// file-backed SQLite. Verifies top-level stacking (deep archives attribute to their top-level
/// ancestor), standalone loose archives, the per-library STACK cap, NewCount, newest-activity
/// ordering, tombstone exclusion, the recency window, home-excluded libraries, the empty state,
/// and Incognito/Private exclusion.
/// </summary>
public sealed class RecentChaptersServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public RecentChaptersServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-recent-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "recent.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // Recent seeds are anchored to "now" so they fall inside the service recency window.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private async Task<(MangaPlexDbContext db, long userId, long libAId, long libBId)> SetupAsync()
    {
        var db = new MangaPlexDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var libA = new LibraryEntity { PublicId = "recLibA", DisplayName = "Alpha Library", RootPath = "/private/alpha", CreatedAt = Now };
        var libB = new LibraryEntity { PublicId = "recLibB", DisplayName = "Beta Library", RootPath = "/private/beta", CreatedAt = Now };
        db.Libraries.AddRange(libA, libB);
        await db.SaveChangesAsync();

        var user = new UserEntity
        {
            PublicId = "recUser", UserName = "admin", NormalizedUserName = "ADMIN",
            IsActive = true, IsAdmin = true, PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"), CreatedAt = Now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (db, user.Id, libA.Id, libB.Id);
    }

    private static async Task<CatalogNodeEntity> AddArchiveAsync(
        MangaPlexDbContext db, long libraryId, string publicId, string displayName,
        DateTimeOffset createdAt, long? parentId = null,
        int availability = (int)CatalogNodeAvailability.Available)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = publicId, LibraryId = libraryId, ParentId = parentId,
            Kind = (int)CatalogNodeKind.Archive, DisplayName = displayName,
            RelativePath = "/private/" + displayName, PathKey = "/private/" + displayName.ToLowerInvariant() + "-" + publicId,
            SortKey = "1" + displayName, Availability = availability, CreatedAt = createdAt,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    private static async Task<CatalogNodeEntity> AddFolderAsync(
        MangaPlexDbContext db, long libraryId, string publicId, string displayName, long? parentId = null)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = publicId, LibraryId = libraryId, ParentId = parentId,
            Kind = (int)CatalogNodeKind.Folder, DisplayName = displayName,
            RelativePath = "/private/" + displayName, PathKey = "/private/" + displayName.ToLowerInvariant() + "-" + publicId,
            SortKey = "0" + displayName, Availability = (int)CatalogNodeAvailability.Available, CreatedAt = Now,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    [Fact]
    public async Task Stacks_ByTopLevelFolder_NewCount_LatestItem_NewestFirst()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // Series A (top level) -> Volume 1 -> two archives; the newest defines the stack.
            var seriesA = await AddFolderAsync(db, libAId, "seriesA", "Series A");
            var vol1 = await AddFolderAsync(db, libAId, "vol1", "Volume 1", parentId: seriesA.Id);
            await AddArchiveAsync(db, libAId, "a_ch1", "Ch1.cbz", Now.AddHours(-5), parentId: vol1.Id);
            var newest = await AddArchiveAsync(db, libAId, "a_ch2", "Ch2.cbz", Now.AddHours(-1), parentId: vol1.Id);

            // Series B (top level) with one recent archive, older than Series A's newest.
            var seriesB = await AddFolderAsync(db, libAId, "seriesB", "Series B");
            await AddArchiveAsync(db, libAId, "b_ch1", "BCh1.cbz", Now.AddHours(-3), parentId: seriesB.Id);

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            // Two stacks (Series A, Series B), Series A first (newest activity).
            Assert.Equal(new[] { "seriesA", "seriesB" }, alpha.Stacks.Select(s => s.Id).ToArray());

            var stackA = alpha.Stacks[0];
            Assert.True(stackA.IsFolder);
            Assert.Equal("Series A", stackA.DisplayName);
            Assert.Equal(2, stackA.NewCount);                 // both chapters attributed to Series A
            Assert.Equal("a_ch2", stackA.LatestItemId);       // newest descendant archive
            Assert.Equal("Ch2.cbz", stackA.LatestItemName);
            // Compare against the DB-stored value (the binary converter is coarser than the
            // in-memory seed's sub-tick precision).
            var newestCreatedAt = await db.CatalogNodes.Where(n => n.Id == newest.Id).Select(n => n.CreatedAt).FirstAsync();
            Assert.Equal(newestCreatedAt, stackA.LatestAddedAt);
            Assert.NotNull(stackA.CoverUrl);                  // folder cover resolved

            var stackB = alpha.Stacks[1];
            Assert.Equal("seriesB", stackB.Id);
            Assert.Equal(1, stackB.NewCount);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task StandaloneLooseArchive_IsOwnStack_NotAFolder()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            var loose = await AddArchiveAsync(db, libAId, "loose1", "Loose.cbz", Now.AddHours(-2));

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            var stack = Assert.Single(alpha.Stacks);
            Assert.False(stack.IsFolder);
            Assert.Equal("loose1", stack.Id);
            Assert.Equal("loose1", stack.LatestItemId);       // Id == LatestItemId for a loose archive
            Assert.Equal("Loose.cbz", stack.DisplayName);
            Assert.Equal(1, stack.NewCount);
            Assert.Equal($"/api/v1/items/{loose.PublicId}/cover", stack.CoverUrl);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task PerLibrary_Caps_Stacks_NotArchives()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // 5 top-level folders, each with 3 recent archives (15 archives, 5 stacks).
            for (var s = 0; s < 5; s++)
            {
                var folder = await AddFolderAsync(db, libAId, "s" + s, "Series " + s);
                for (var c = 0; c < 3; c++)
                    await AddArchiveAsync(db, libAId, $"s{s}c{c}", $"Ch{c}.cbz", Now.AddHours(-(s * 10 + c)), parentId: folder.Id);
            }

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId, perLibrary: 2);

            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            // Cap is on STACKS: 2 stacks, each still reports its full NewCount of 3.
            Assert.Equal(2, alpha.Stacks.Count);
            Assert.All(alpha.Stacks, st => Assert.Equal(3, st.NewCount));
            // Newest-activity ordering: Series 0 then Series 1 (lower index = more recent seed).
            Assert.Equal(new[] { "s0", "s1" }, alpha.Stacks.Select(st => st.Id).ToArray());
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ExcludesTombstoned_And_OutsideWindow()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            var series = await AddFolderAsync(db, libAId, "seriesA", "Series A");
            await AddArchiveAsync(db, libAId, "live", "Live.cbz", Now.AddHours(-1), parentId: series.Id);
            await AddArchiveAsync(db, libAId, "tomb", "Tomb.cbz", Now.AddHours(-2), parentId: series.Id,
                availability: (int)CatalogNodeAvailability.Tombstoned);
            // An archive older than the recency window must not count.
            await AddArchiveAsync(db, libAId, "old", "Old.cbz", Now - RecentChaptersService.RecentWindow - TimeSpan.FromDays(1), parentId: series.Id);

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            var stack = Assert.Single(alpha.Stacks);
            Assert.Equal(1, stack.NewCount);                 // only the live, in-window archive
            Assert.Equal("live", stack.LatestItemId);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task EmptyState_WhenNoRecentArchives()
    {
        var (db, userId, _, _) = await SetupAsync();
        try
        {
            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            Assert.Equal(2, result.Libraries.Count);
            Assert.All(result.Libraries, g => Assert.Empty(g.Stacks));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task HomeExcludedLibraries_AreDropped()
    {
        var (db, userId, libAId, libBId) = await SetupAsync();
        try
        {
            await AddArchiveAsync(db, libAId, "a1", "A.cbz", Now.AddHours(-1));
            await AddArchiveAsync(db, libBId, "b1", "B.cbz", Now.AddHours(-1));

            db.HomeExcludedLibraries.Add(new HomeExcludedLibraryEntity { UserId = userId, LibraryId = libBId, MarkedAt = Now });
            await db.SaveChangesAsync();

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            // Beta is hidden from home entirely (no group at all).
            Assert.Single(result.Libraries);
            Assert.Equal("recLibA", result.Libraries[0].LibraryId);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task PerUserWindow_NarrowsToStoredDays()
    {
        // A 10-day-old archive is inside the 30-day default but outside a 7-day per-user window.
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            var series = await AddFolderAsync(db, libAId, "seriesA", "Series A");
            await AddArchiveAsync(db, libAId, "recentArch", "Recent.cbz", Now.AddDays(-2), parentId: series.Id);
            await AddArchiveAsync(db, libAId, "tenDayArch", "TenDay.cbz", Now.AddDays(-10), parentId: series.Id);

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

            // Default (unset) window: 30 days — both archives count.
            var defaultResult = await service.GetRecentChaptersAsync(userId);
            var defaultStack = Assert.Single(defaultResult.Libraries.First(g => g.LibraryId == "recLibA").Stacks);
            Assert.Equal(2, defaultStack.NewCount);

            // Per-user 7-day window — only the 2-day-old archive counts.
            db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = userId, HomeRecentWindowDays = 7 });
            await db.SaveChangesAsync();

            var narrowResult = await service.GetRecentChaptersAsync(userId);
            var narrowStack = Assert.Single(narrowResult.Libraries.First(g => g.LibraryId == "recLibA").Stacks);
            Assert.Equal(1, narrowStack.NewCount);
            Assert.Equal("recentArch", narrowStack.LatestItemId);
        }
        finally { await db.DisposeAsync(); }
    }

    [Theory]
    [InlineData(0, 30)]     // unset -> default
    [InlineData(-5, 1)]     // a stray negative (should not occur via the UI) clamps to the floor
    [InlineData(1, 1)]      // already at the range floor
    [InlineData(500, 365)]  // above range clamps to the ceiling
    public async Task PerUserWindow_IsClampedToSaneRange(int stored, int expectedEffectiveDays)
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // One archive just inside the expected effective window, one just outside it.
            await AddArchiveAsync(db, libAId, "inside", "Inside.cbz", Now.AddDays(-(expectedEffectiveDays - 0.5)));
            await AddArchiveAsync(db, libAId, "outside", "Outside.cbz", Now.AddDays(-(expectedEffectiveDays + 0.5)));

            db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = userId, HomeRecentWindowDays = stored });
            await db.SaveChangesAsync();

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            var stack = Assert.Single(alpha.Stacks);
            Assert.Equal("inside", stack.LatestItemId);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Incognito_ExcludesPrivateLibrary()
    {
        var (db, userId, libAId, libBId) = await SetupAsync();
        try
        {
            await AddArchiveAsync(db, libAId, "a1", "A.cbz", Now.AddHours(-1));
            await AddArchiveAsync(db, libBId, "b1", "B.cbz", Now.AddHours(-1));

            db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = userId, LibraryId = libBId, MarkedAt = Now });
            await db.SaveChangesAsync();

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

            var normal = await service.GetRecentChaptersAsync(userId, incognito: false);
            Assert.Equal(new[] { "recLibA", "recLibB" }, normal.Libraries.Select(g => g.LibraryId).ToArray());

            var incog = await service.GetRecentChaptersAsync(userId, incognito: true);
            Assert.Single(incog.Libraries);
            Assert.Equal("recLibA", incog.Libraries[0].LibraryId);
        }
        finally { await db.DisposeAsync(); }
    }
}
