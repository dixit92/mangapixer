namespace com.lifepixer.mangaplex.Tests.Server.Features.Reading;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Reading;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Features.Reading;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Integration tests for ReadingStateService.
/// Verifies revisioned/idempotent progress, bookmarks, preferences,
/// continue-reading, completed/reread semantics, and stale-manifest mapping.
/// </summary>
public sealed class ReadingStateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public ReadingStateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-reading-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "reading.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPlexDbContext db, long userId, long libraryId, long nodeId, long itemId)> SetupAsync()
    {
        var db = new MangaPlexDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test",
            RootPath = "/private/test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        var user = new UserEntity
        {
            PublicId = OpaqueId.Encode(2),
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

        var node = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(100),
            LibraryId = library.Id,
            ParentId = null,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = "Test Archive",
            RelativePath = "/private/test.cbz",
            PathKey = "/private/test.cbz",
            SortKey = "1Test",
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();

        var item = new ArchiveItemEntity
        {
            NodeId = node.Id,
            ArchiveFormat = 0,
            ByteLength = 1024,
            ModificationTicks = 0,
            ContentVersion = 1,
            AnalysisState = 0,
            PageCount = 10,
        };
        db.ArchiveItems.Add(item);
        await db.SaveChangesAsync();

        return (db, user.Id, library.Id, node.Id, node.Id);
    }

    [Fact]
    public async Task UpdateProgress_CreatesNewProgress()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            var result = await service.UpdateProgressAsync(userId, itemId, pageIndex: 3,
                expectedContentVersion: 1, mutationId: 1);

            Assert.Equal(UpdateStatus.Success, result.Status);
            Assert.False(result.AlreadyApplied);
            Assert.Equal(1, result.Revision);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateProgress_IdempotentWithSameMutationId()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            await service.UpdateProgressAsync(userId, itemId, 3, 1, mutationId: 100);
            var result = await service.UpdateProgressAsync(userId, itemId, 3, 1, mutationId: 100);

            Assert.Equal(UpdateStatus.Success, result.Status);
            Assert.True(result.AlreadyApplied);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateProgress_RejectsStaleContentVersion()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            var result = await service.UpdateProgressAsync(userId, itemId, 3,
                expectedContentVersion: 999, mutationId: 1);

            Assert.Equal(UpdateStatus.StaleContent, result.Status);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateProgress_MarksCompletedAtLastPage()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            var result = await service.UpdateProgressAsync(userId, itemId, pageIndex: 9,
                expectedContentVersion: 1, mutationId: 1);

            Assert.Equal(UpdateStatus.Success, result.Status);

            var progress = await service.GetProgressAsync(userId, itemId);
            Assert.Equal(ReadingState.Completed, progress!.State);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateProgress_BackwardReadingDoesNotUncomplete()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            // Complete
            await service.UpdateProgressAsync(userId, itemId, 9, 1, mutationId: 1);
            // Go back
            await service.UpdateProgressAsync(userId, itemId, 3, 1, mutationId: 2);

            var progress = await service.GetProgressAsync(userId, itemId);
            // Should still be completed
            Assert.Equal(ReadingState.Completed, progress!.State);
            Assert.Equal(3, progress.PageIndex);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ResetProgress_RemovesProgress()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            await service.UpdateProgressAsync(userId, itemId, 5, 1, mutationId: 1);
            await service.ResetProgressAsync(userId, itemId);

            var progress = await service.GetProgressAsync(userId, itemId);
            Assert.Null(progress);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task GetProgress_MarksStaleWhenContentVersionChanges()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            await service.UpdateProgressAsync(userId, itemId, 5, 1, mutationId: 1);

            // Bump content version
            var item = await db.ArchiveItems.FirstAsync(a => a.NodeId == itemId);
            item.ContentVersion = 2;
            await db.SaveChangesAsync();

            var progress = await service.GetProgressAsync(userId, itemId);
            Assert.True(progress!.IsStale);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task TwoUsers_DoNotOverwriteEachOther()
    {
        var (db, userId, libraryId, _, itemId) = await SetupAsync();
        try
        {
            var user2 = new UserEntity
            {
                PublicId = OpaqueId.Encode(3),
                UserName = "user2",
                NormalizedUserName = "USER2",
                IsActive = true,
                IsAdmin = true,
                PasswordHash = "hash",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user2);
            await db.SaveChangesAsync();

            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            await service.UpdateProgressAsync(userId, itemId, 3, 1, mutationId: 1);
            await service.UpdateProgressAsync(user2.Id, itemId, 7, 1, mutationId: 1);

            var p1 = await service.GetProgressAsync(userId, itemId);
            var p2 = await service.GetProgressAsync(user2.Id, itemId);

            Assert.Equal(3, p1!.PageIndex);
            Assert.Equal(7, p2!.PageIndex);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task GetContinueReading_ReturnsInProgressItems()
    {
        var (db, userId, libraryId, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            await service.UpdateProgressAsync(userId, itemId, 5, 1, mutationId: 1);

            var result = await service.GetContinueReadingAsync(userId);
            Assert.Single(result);
            Assert.Equal("Test Archive", result[0].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task AddBookmark_CreatesAndReturnsBookmark()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            var bookmarkId = await service.AddBookmarkAsync(userId, itemId, 3, 0.5, "My Bookmark");
            Assert.NotNull(bookmarkId);

            var bookmarks = await service.GetBookmarksAsync(userId, itemId);
            Assert.Single(bookmarks);
            Assert.Equal("My Bookmark", bookmarks[0].Label);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task RemoveBookmark_DeletesBookmark()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            var bookmarkId = await service.AddBookmarkAsync(userId, itemId, 3, 0.5, "Test");
            var removed = await service.RemoveBookmarkAsync(userId, bookmarkId!);

            Assert.True(removed);
            var bookmarks = await service.GetBookmarksAsync(userId, itemId);
            Assert.Empty(bookmarks);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task GetPreferences_ReturnsDefaultsWhenNoneSet()
    {
        var (db, userId, _, _, _) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            var prefs = await service.GetPreferencesAsync(userId);
            Assert.Equal(ReaderMode.PagedLtr, prefs.DefaultReaderMode);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SetPreferences_CreatesAndUpdates()
    {
        var (db, userId, _, _, _) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            await service.SetPreferencesAsync(userId, new UserPreferencesDto
            {
                DefaultReaderMode = ReaderMode.VerticalWebtoon,
                PreferDoubleSpread = false,
                ReducedMotion = true,
            });

            var prefs = await service.GetPreferencesAsync(userId);
            Assert.Equal(ReaderMode.VerticalWebtoon, prefs.DefaultReaderMode);
            Assert.True(prefs.ReducedMotion);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateProgress_UnauthorizedUser_ReturnsUnauthorized()
    {
        var (db, _, _, _, itemId) = await SetupAsync();
        try
        {
            var reader = new UserEntity
            {
                PublicId = OpaqueId.Encode(99),
                UserName = "reader",
                NormalizedUserName = "READER",
                IsActive = true,
                IsAdmin = false,
                PasswordHash = "hash",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(reader);
            await db.SaveChangesAsync();

            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            var result = await service.UpdateProgressAsync(reader.Id, itemId, 3, 1, mutationId: 1);
            Assert.Equal(UpdateStatus.Unauthorized, result.Status);
        }
        finally { await db.DisposeAsync(); }
    }
}
