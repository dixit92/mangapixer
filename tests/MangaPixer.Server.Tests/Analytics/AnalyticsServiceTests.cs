namespace com.lifepixer.mangapixer.Tests.Server.Analytics;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Analytics;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for AnalyticsService (1.22.0 lane E): overview
/// aggregation, per-user engagement counts, tombstone handling, and the
/// owner-decided private-library exclusion.
/// </summary>
public sealed class AnalyticsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public AnalyticsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-analytics-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<MangaPixerDbContext> OpenDbAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        return db;
    }

    private static LibraryEntity NewLibrary(long id, string name) => new()
    {
        Id = id,
        PublicId = OpaqueId.Encode(id),
        DisplayName = name,
        RootPath = $"/private/{name}",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static UserEntity NewUser(long id, string name, bool isAdmin = false) => new()
    {
        Id = id,
        PublicId = OpaqueId.Encode(id),
        UserName = name,
        NormalizedUserName = name.ToUpperInvariant(),
        IsActive = true,
        IsAdmin = isAdmin,
        PasswordHash = "hash",
        SecurityStamp = "stamp",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static CatalogNodeEntity NewArchiveNode(long id, long libraryId, int availability = 0) => new()
    {
        Id = id,
        PublicId = OpaqueId.Encode(id),
        LibraryId = libraryId,
        Kind = (int)CatalogNodeKind.Archive,
        DisplayName = $"item-{id}",
        RelativePath = $"item-{id}.cbz",
        PathKey = $"item-{id}.cbz",
        SortKey = $"item-{id}",
        Availability = availability,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task GetOverview_AggregatesCountsAndExcludesTombstonesFromDiagnostics()
    {
        await using var db = await OpenDbAsync();
        db.Libraries.Add(NewLibrary(1, "lib1"));
        db.Users.Add(NewUser(1, "admin", isAdmin: true));
        db.CatalogNodes.Add(NewArchiveNode(1, 1));
        db.CatalogNodes.Add(NewArchiveNode(2, 1, availability: 5)); // tombstoned
        db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = 1, AnalysisState = 0 });
        db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = 2, AnalysisState = 0 });
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = 1,
            ItemId = 1,
            State = 2,
            EntryKey = "e",
            LastMutationId = "m1",
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        db.Favorites.Add(new FavoriteEntity { UserId = 1, CatalogNodeId = 1, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var service = new AnalyticsService(db, new DiagnosticsService(db));
        var overview = await service.GetOverviewAsync();

        Assert.Equal(1, overview.LibraryCount);
        Assert.Equal(2, overview.TotalNodeCount);
        Assert.Equal(1, overview.TombstonedNodeCount);
        Assert.Equal(1, overview.UserCount);
        Assert.Equal(1, overview.AdminCount);
        Assert.Equal(1, overview.CompletedItemCount);
        Assert.Equal(0, overview.InProgressItemCount);
        Assert.Equal(1, overview.FavoriteCount);
    }

    [Fact]
    public async Task GetUserAnalytics_ReturnsPerUserCountsIncludingAdminsOwnRow()
    {
        await using var db = await OpenDbAsync();
        db.Libraries.Add(NewLibrary(1, "lib1"));
        db.Users.Add(NewUser(1, "admin", isAdmin: true));
        db.Users.Add(NewUser(2, "reader"));
        db.CatalogNodes.Add(NewArchiveNode(1, 1));
        db.CatalogNodes.Add(NewArchiveNode(2, 1));

        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = 1,
            ItemId = 1,
            State = 2,
            EntryKey = "e",
            LastMutationId = "m1",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = 2,
            ItemId = 2,
            State = 1,
            EntryKey = "e",
            LastMutationId = "m2",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Bookmarks.Add(new BookmarkEntity
        {
            UserId = 2,
            ItemId = 2,
            EntryKey = "e",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Favorites.Add(new FavoriteEntity { UserId = 2, CatalogNodeId = 1, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var service = new AnalyticsService(db, new DiagnosticsService(db));
        var rows = await service.GetUserAnalyticsAsync();

        Assert.Equal(2, rows.Count);

        var adminRow = Assert.Single(rows, r => r.Username == "admin");
        Assert.Equal(1, adminRow.ChaptersCompleted);
        Assert.Equal(0, adminRow.ChaptersInProgress);
        Assert.NotNull(adminRow.LastReadingActivityAt);

        var readerRow = Assert.Single(rows, r => r.Username == "reader");
        Assert.Equal(0, readerRow.ChaptersCompleted);
        Assert.Equal(1, readerRow.ChaptersInProgress);
        Assert.Equal(1, readerRow.BookmarkCount);
        Assert.Equal(1, readerRow.FavoriteCount);
    }

    [Fact]
    public async Task GetUserAnalytics_ExcludesActivityInUsersOwnPrivateLibrary()
    {
        await using var db = await OpenDbAsync();
        db.Libraries.Add(NewLibrary(1, "public-lib"));
        db.Libraries.Add(NewLibrary(2, "private-lib"));
        db.Users.Add(NewUser(1, "reader"));
        db.CatalogNodes.Add(NewArchiveNode(1, 1));
        db.CatalogNodes.Add(NewArchiveNode(2, 2));

        // Progress in the public library — visible.
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = 1,
            ItemId = 1,
            State = 2,
            EntryKey = "e",
            LastMutationId = "m1",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        });
        // Progress in the library the user marked Private — must be excluded.
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = 1,
            ItemId = 2,
            State = 2,
            EntryKey = "e",
            LastMutationId = "m2",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Bookmarks.Add(new BookmarkEntity
        {
            UserId = 1,
            ItemId = 2,
            EntryKey = "e",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Favorites.Add(new FavoriteEntity { UserId = 1, CatalogNodeId = 2, CreatedAt = DateTimeOffset.UtcNow });
        db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = 1, LibraryId = 2, MarkedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var service = new AnalyticsService(db, new DiagnosticsService(db));
        var rows = await service.GetUserAnalyticsAsync();

        var row = Assert.Single(rows);
        Assert.Equal(1, row.ChaptersCompleted); // only the public-library completion counts
        Assert.Equal(0, row.BookmarkCount);
        Assert.Equal(0, row.FavoriteCount);
        Assert.NotNull(row.LastReadingActivityAt);
        Assert.Equal(DateTimeOffset.UtcNow.AddMinutes(-10), row.LastReadingActivityAt!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetUserAnalytics_UserWithNoActivity_ReturnsZeroCountsAndNullTimestamp()
    {
        await using var db = await OpenDbAsync();
        db.Users.Add(NewUser(1, "quiet"));
        await db.SaveChangesAsync();

        var service = new AnalyticsService(db, new DiagnosticsService(db));
        var rows = await service.GetUserAnalyticsAsync();

        var row = Assert.Single(rows);
        Assert.Equal(0, row.ChaptersCompleted);
        Assert.Equal(0, row.ChaptersInProgress);
        Assert.Equal(0, row.BookmarkCount);
        Assert.Equal(0, row.FavoriteCount);
        Assert.Null(row.LastReadingActivityAt);
    }
}
