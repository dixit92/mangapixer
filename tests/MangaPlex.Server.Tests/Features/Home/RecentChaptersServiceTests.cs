using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Features.Home;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace com.lifepixer.mangaplex.Tests.Server.Features.Home;

/// <summary>
/// Service-with-DB tests for RecentChaptersService (1.11.0 Lane C). Uses real
/// file-backed SQLite. Verifies per-library grouping, the per-library cap,
/// newest-first ordering, tombstone exclusion, the empty state, and that
/// Incognito/Private visibility excludes a Private library's items.
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

    private async Task<(MangaPlexDbContext db, long userId, long libAId, long libBId)> SetupAsync()
    {
        var db = new MangaPlexDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var libA = new LibraryEntity
        {
            PublicId = "recLibA",
            DisplayName = "Alpha Library",
            RootPath = "/private/alpha",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var libB = new LibraryEntity
        {
            PublicId = "recLibB",
            DisplayName = "Beta Library",
            RootPath = "/private/beta",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.AddRange(libA, libB);
        await db.SaveChangesAsync();

        var user = new UserEntity
        {
            PublicId = "recUser",
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (db, user.Id, libA.Id, libB.Id);
    }

    private static async Task<CatalogNodeEntity> AddArchiveAsync(
        MangaPlexDbContext db,
        long libraryId,
        string publicId,
        string displayName,
        DateTimeOffset createdAt,
        long? parentId = null,
        int availability = (int)CatalogNodeAvailability.Available)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = publicId,
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = displayName,
            RelativePath = "/private/" + displayName,
            PathKey = "/private/" + displayName.ToLowerInvariant(),
            SortKey = "1" + displayName,
            Availability = availability,
            CreatedAt = createdAt,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    private static async Task<CatalogNodeEntity> AddFolderAsync(
        MangaPlexDbContext db,
        long libraryId,
        string publicId,
        string displayName)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = publicId,
            LibraryId = libraryId,
            Kind = (int)CatalogNodeKind.Folder,
            DisplayName = displayName,
            RelativePath = "/private/" + displayName,
            PathKey = "/private/" + displayName.ToLowerInvariant(),
            SortKey = "0" + displayName,
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    [Fact]
    public async Task GetRecentChapters_GroupsByLibrary_NewestFirst_CapsPerLibrary()
    {
        var (db, userId, libAId, libBId) = await SetupAsync();
        try
        {
            var baseTime = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            // Alpha: three archives, deliberately inserted out of recency order.
            await AddArchiveAsync(db, libAId, "a1", "Old.cbz", baseTime);
            await AddArchiveAsync(db, libAId, "a3", "Newest.cbz", baseTime.AddHours(2));
            await AddArchiveAsync(db, libAId, "a2", "Mid.cbz", baseTime.AddHours(1));
            // Beta: one archive, older than Alpha's newest.
            await AddArchiveAsync(db, libBId, "b1", "BetaCh.cbz", baseTime.AddHours(3));

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId, perLibrary: 2);

            // Two library groups, ordered by display name (Alpha before Beta).
            Assert.Equal(new[] { "recLibA", "recLibB" }, result.Libraries.Select(g => g.LibraryId).ToArray());
            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            var beta = result.Libraries.First(g => g.LibraryId == "recLibB");

            // Cap honored: Alpha has 3 archives but only 2 returned.
            Assert.Equal(2, alpha.Items.Count);
            // Newest first: Newest.cbz then Mid.cbz (Old.cbz capped off).
            Assert.Equal(new[] { "Newest.cbz", "Mid.cbz" }, alpha.Items.Select(i => i.DisplayName).ToArray());
            Assert.Equal(new[] { "a3", "a2" }, alpha.Items.Select(i => i.ItemId).ToArray());

            Assert.Single(beta.Items);
            Assert.Equal("BetaCh.cbz", beta.Items[0].DisplayName);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetRecentChapters_ExcludesTombstonedAndFolders_AndCarriesSeries()
    {
        var (db, userId, libAId, libBId) = await SetupAsync();
        try
        {
            var t = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            var series = await AddFolderAsync(db, libAId, "seriesA", "Series A");
            await AddArchiveAsync(db, libAId, "live", "Live.cbz", t.AddHours(1), parentId: series.Id);
            await AddArchiveAsync(db, libAId, "tomb", "Tomb.cbz", t.AddHours(2),
                availability: (int)CatalogNodeAvailability.Tombstoned);

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            // Only the live archive; the tombstoned one and the folder are excluded.
            Assert.Single(alpha.Items);
            var entry = alpha.Items[0];
            Assert.Equal("Live.cbz", entry.DisplayName);
            // Series name comes from the immediate parent folder.
            Assert.Equal("seriesA", entry.ParentId);
            Assert.Equal("Series A", entry.SeriesName);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetRecentChapters_EmptyState_WhenNoArchives()
    {
        var (db, userId, libAId, libBId) = await SetupAsync();
        try
        {
            // No archives seeded. Both libraries appear with empty items.
            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            Assert.Equal(2, result.Libraries.Count);
            Assert.All(result.Libraries, g => Assert.Empty(g.Items));
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetRecentChapters_Incognito_ExcludesPrivateLibraryItems()
    {
        var (db, userId, libAId, libBId) = await SetupAsync();
        try
        {
            var t = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            await AddArchiveAsync(db, libAId, "a1", "AlphaCh.cbz", t);
            await AddArchiveAsync(db, libBId, "b1", "BetaCh.cbz", t);

            // Mark Beta as Private for this user.
            db.PrivateLibraries.Add(new PrivateLibraryEntity
            {
                UserId = userId,
                LibraryId = libBId,
                MarkedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

            // Without incognito: both libraries visible.
            var normal = await service.GetRecentChaptersAsync(userId, incognito: false);
            Assert.Equal(new[] { "recLibA", "recLibB" }, normal.Libraries.Select(g => g.LibraryId).ToArray());

            // With incognito: Beta (Private) is excluded entirely - no group, no items.
            var incog = await service.GetRecentChaptersAsync(userId, incognito: true);
            Assert.Single(incog.Libraries);
            Assert.Equal("recLibA", incog.Libraries[0].LibraryId);
            Assert.DoesNotContain(incog.Libraries, g => g.LibraryId == "recLibB");
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetRecentChapters_ClampsPerLibraryCap()
    {
        var (db, userId, libAId, libBId) = await SetupAsync();
        try
        {
            var t = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            // Seed more than MaxPerLibrary so the clamps are observable: an
            // above-max request is clamped to MaxPerLibrary (50), and a below-1
            // request falls back to the default (12) - both below the 15 seeded.
            for (var i = 0; i < 15; i++)
                await AddArchiveAsync(db, libAId, "a" + i, "Ch" + i + ".cbz", t.AddHours(i));

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

            // Above-max cap is clamped to MaxPerLibrary (50) -> all 15 returned.
            var clampedHigh = await service.GetRecentChaptersAsync(userId, perLibrary: 999);
            Assert.Equal(15, clampedHigh.Libraries.First(g => g.LibraryId == "recLibA").Items.Count);

            // Below-1 falls back to the default (12) -> only 12 returned.
            var clampedLow = await service.GetRecentChaptersAsync(userId, perLibrary: 0);
            Assert.Equal(RecentChaptersService.DefaultPerLibrary,
                clampedLow.Libraries.First(g => g.LibraryId == "recLibA").Items.Count);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }
}
