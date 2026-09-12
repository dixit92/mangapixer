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

    /// <summary>
    /// Builds a small tree for bulk read-mark tests:
    /// <c>folder F → archive A1</c> and <c>folder F → subfolder SF → archive A2</c>.
    /// Returns the folder F's id and both archive ids.
    /// </summary>
    private async Task<(MangaPlexDbContext db, long userId, long folderId, List<long> archiveIds)> SetupFolderTreeAsync()
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

        long seq = 200;
        CatalogNodeEntity Node(int kind, long? parentId, string name) => new()
        {
            PublicId = OpaqueId.Encode(seq++),
            LibraryId = library.Id,
            ParentId = parentId,
            Kind = kind,
            DisplayName = name,
            RelativePath = "/private/" + name,
            PathKey = "/private/" + name,
            SortKey = name,
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var folder = Node((int)CatalogNodeKind.Folder, null, "F");
        db.CatalogNodes.Add(folder);
        await db.SaveChangesAsync();

        var subFolder = Node((int)CatalogNodeKind.Folder, folder.Id, "SF");
        var a1 = Node((int)CatalogNodeKind.Archive, folder.Id, "A1");
        db.CatalogNodes.AddRange(subFolder, a1);
        await db.SaveChangesAsync();

        var a2 = Node((int)CatalogNodeKind.Archive, subFolder.Id, "A2");
        db.CatalogNodes.Add(a2);
        await db.SaveChangesAsync();

        // Analysis rows so the archives look like real items (10 pages each).
        foreach (var archive in new[] { a1, a2 })
        {
            db.ArchiveItems.Add(new ArchiveItemEntity
            {
                NodeId = archive.Id,
                ArchiveFormat = 0,
                ByteLength = 1024,
                ModificationTicks = 0,
                ContentVersion = 1,
                AnalysisState = 0,
                PageCount = 10,
            });
        }
        await db.SaveChangesAsync();

        return (db, user.Id, folder.Id, new List<long> { a1.Id, a2.Id });
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
                expectedContentVersion: 1, mutationId: "mut-1");

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

            await service.UpdateProgressAsync(userId, itemId, 3, 1, mutationId: "mut-100");
            var result = await service.UpdateProgressAsync(userId, itemId, 3, 1, mutationId: "mut-100");

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
                expectedContentVersion: 999, mutationId: "mut-1");

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
                expectedContentVersion: 1, mutationId: "mut-1");

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
            await service.UpdateProgressAsync(userId, itemId, 9, 1, mutationId: "mut-1");
            // Go back
            await service.UpdateProgressAsync(userId, itemId, 3, 1, mutationId: "mut-2");

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

            await service.UpdateProgressAsync(userId, itemId, 5, 1, mutationId: "mut-1");
            await service.ResetProgressAsync(userId, itemId);

            // After reset, GetProgress returns Unread state (not null)
            // (audit defect D14/D32 — eliminates browser 404 for unread items)
            var progress = await service.GetProgressAsync(userId, itemId);
            Assert.NotNull(progress);
            Assert.Equal(ReadingState.Unread, progress!.State);
            Assert.Equal(0, progress.PageIndex);
            Assert.Equal(0, progress.Revision);
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

            await service.UpdateProgressAsync(userId, itemId, 5, 1, mutationId: "mut-1");

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

            await service.UpdateProgressAsync(userId, itemId, 3, 1, mutationId: "mut-1");
            await service.UpdateProgressAsync(user2.Id, itemId, 7, 1, mutationId: "mut-1");

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

            await service.UpdateProgressAsync(userId, itemId, 5, 1, mutationId: "mut-1");

            var result = await service.GetContinueReadingAsync(userId);
            Assert.Single(result);
            Assert.Equal("Test Archive", result[0].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task DismissFromContinue_HidesItem_UntilForwardProgress()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            await service.UpdateProgressAsync(userId, itemId, 5, 1, mutationId: "m1");
            Assert.Single(await service.GetContinueReadingAsync(userId));

            // Dismiss removes it from the strip without marking it read.
            Assert.True(await service.DismissFromContinueAsync(userId, itemId));
            Assert.Empty(await service.GetContinueReadingAsync(userId));
            Assert.False(await service.IsReadAsync(userId, itemId));

            // Making forward progress again brings it back.
            await service.UpdateProgressAsync(userId, itemId, 6, 1, mutationId: "m2");
            Assert.Single(await service.GetContinueReadingAsync(userId));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ContinueReading_ExcludesReadMarkedItems()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            await service.UpdateProgressAsync(userId, itemId, 5, 1, mutationId: "m1");
            Assert.Single(await service.GetContinueReadingAsync(userId));

            // Marking it read auto-hides it from the strip (owner decision 2026-09-09).
            await service.SetItemReadAsync(userId, itemId, read: true);
            Assert.Empty(await service.GetContinueReadingAsync(userId));
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

    // --- Sticky read-marks (1.2.0) ---

    [Fact]
    public async Task SetItemRead_ManualMark_RecordsCompletedAtLastPage_AndOpensAtStart()
    {
        // Rule 3 (1.9.0): a manual mark-read is treated as fully read — it records
        // progress Completed at the last page so the universal open-position rule
        // reopens the title at page 1 and it drops out of continue-reading.
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            // Not read initially.
            Assert.False(await service.IsReadAsync(userId, itemId));

            // Mark read without ever opening the reader (no prior progress).
            Assert.True(await service.SetItemReadAsync(userId, itemId, read: true));
            Assert.True(await service.IsReadAsync(userId, itemId));

            // Progress is now Completed at the last page (index 9 of a 10-page item),
            // and the open-position rule resolves to page 1.
            var progress = await service.GetProgressAsync(userId, itemId);
            Assert.Equal(ReadingState.Completed, progress!.State);
            Assert.Equal(9, progress.PageIndex);
            Assert.Equal(0, progress.OpenPageIndex);

            // Clearing it is a full reset (rule 1): mark gone, progress gone.
            Assert.True(await service.SetItemReadAsync(userId, itemId, read: false));
            Assert.False(await service.IsReadAsync(userId, itemId));
            var afterClear = await service.GetProgressAsync(userId, itemId);
            Assert.Equal(ReadingState.Unread, afterClear!.State);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SetItemRead_IsIdempotent()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            await service.SetItemReadAsync(userId, itemId, read: true);
            await service.SetItemReadAsync(userId, itemId, read: true);

            // Exactly one mark row despite two set calls (unique index enforced).
            var count = await db.ReadMarks.CountAsync(m => m.UserId == userId && m.ItemId == itemId);
            Assert.Equal(1, count);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Completion_AutoSetsReadMark()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            Assert.False(await service.IsReadAsync(userId, itemId));

            // Reaching the last page (index 9 of a 10-page item) completes it.
            await service.UpdateProgressAsync(userId, itemId, 9, 1, mutationId: "mut-1");

            Assert.True(await service.IsReadAsync(userId, itemId));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ReadMark_IsSticky_AcrossBackwardNavigation()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            // Complete (auto-marks read), then navigate back to page 0.
            await service.UpdateProgressAsync(userId, itemId, 9, 1, mutationId: "mut-1");
            await service.UpdateProgressAsync(userId, itemId, 0, 1, mutationId: "mut-2");

            // Sticky: still read even though we re-read from the start.
            Assert.True(await service.IsReadAsync(userId, itemId));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ClearReadMark_IsFullReset_WipesProgress()
    {
        // Rule 1 (1.9.0): clearing a read-mark is a deliberate full reset — it wipes
        // BOTH the mark and the reading position, so the item returns to Unread and
        // reopens at page 1 (the same outcome folder "unread" already produced).
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            await service.UpdateProgressAsync(userId, itemId, 9, 1, mutationId: "mut-1");
            Assert.True(await service.IsReadAsync(userId, itemId));

            await service.SetItemReadAsync(userId, itemId, read: false);

            Assert.False(await service.IsReadAsync(userId, itemId));
            // Progress row removed -> Unread, reopens at page 1.
            var progress = await service.GetProgressAsync(userId, itemId);
            Assert.Equal(ReadingState.Unread, progress!.State);
            Assert.Equal(0, progress.PageIndex);
            Assert.Equal(0, progress.OpenPageIndex);
            Assert.False(await db.ReadingProgress.AnyAsync(p => p.UserId == userId && p.ItemId == itemId));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SetItemRead_UnauthorizedUser_ReturnsFalse()
    {
        var (db, _, _, _, itemId) = await SetupAsync();
        try
        {
            var reader = new UserEntity
            {
                PublicId = OpaqueId.Encode(98),
                UserName = "reader2",
                NormalizedUserName = "READER2",
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

            Assert.False(await service.SetItemReadAsync(reader.Id, itemId, read: true));
            Assert.False(await service.IsReadAsync(reader.Id, itemId));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SetFolderRead_MarksAllDescendantArchivesRecursively()
    {
        var (db, userId, folderId, archiveIds) = await SetupFolderTreeAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            var result = await service.SetFolderReadAsync(userId, folderId, read: true);
            Assert.NotNull(result);
            Assert.Equal(archiveIds.Count, result!.Total);
            Assert.Equal(archiveIds.Count, result.Affected);

            foreach (var id in archiveIds)
                Assert.True(await service.IsReadAsync(userId, id));

            // Idempotent: a second bulk-set affects nothing.
            var again = await service.SetFolderReadAsync(userId, folderId, read: true);
            Assert.Equal(0, again!.Affected);

            // Bulk clear removes them all.
            var cleared = await service.SetFolderReadAsync(userId, folderId, read: false);
            Assert.Equal(archiveIds.Count, cleared!.Affected);
            foreach (var id in archiveIds)
                Assert.False(await service.IsReadAsync(userId, id));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SetFolderUnread_ResetsInProgressDescendantsToUnread()
    {
        var (db, userId, folderId, archiveIds) = await SetupFolderTreeAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            // Put both descendant archives into InProgress (reading) state with no
            // read-mark — the exact scenario the owner reported: marking the folder
            // unread left these "reading" because clearing a nonexistent read-mark
            // was a no-op and reading_progress kept State = InProgress.
            foreach (var id in archiveIds)
            {
                await service.UpdateProgressAsync(userId, id, pageIndex: 3,
                    expectedContentVersion: 1, mutationId: "mut-" + id);
            }

            // Sanity: both are InProgress before the folder-unread.
            foreach (var id in archiveIds)
            {
                var p = await service.GetProgressAsync(userId, id);
                Assert.Equal(ReadingState.InProgress, p!.State);
            }

            // Mark the folder unread (bulk clear).
            var cleared = await service.SetFolderReadAsync(userId, folderId, read: false);
            Assert.NotNull(cleared);
            Assert.Equal(archiveIds.Count, cleared!.Affected);

            // Both archives should now be Unread — progress rows deleted.
            foreach (var id in archiveIds)
            {
                Assert.False(await service.IsReadAsync(userId, id));
                var p = await service.GetProgressAsync(userId, id);
                Assert.Equal(ReadingState.Unread, p!.State);
                Assert.Equal(0, p.PageIndex);
            }

            // No reading_progress rows should remain for these archives.
            var remaining = await db.ReadingProgress
                .Where(p => p.UserId == userId && archiveIds.Contains(p.ItemId))
                .ToListAsync();
            Assert.Empty(remaining);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SetFolderUnread_ResetsMixedReadAndInProgressDescendants()
    {
        var (db, userId, folderId, archiveIds) = await SetupFolderTreeAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            var a1 = archiveIds[0];
            var a2 = archiveIds[1];

            // A1: read-mark only (Completed via manual mark, no progress row).
            await service.SetItemReadAsync(userId, a1, read: true);

            // A2: InProgress with progress row, no read-mark.
            await service.UpdateProgressAsync(userId, a2, pageIndex: 5,
                expectedContentVersion: 1, mutationId: "mut-a2");

            // Mark the folder unread — should clear A1's read-mark AND reset A2's progress.
            var cleared = await service.SetFolderReadAsync(userId, folderId, read: false);
            Assert.NotNull(cleared);
            Assert.Equal(2, cleared!.Affected); // both had state to clear

            Assert.False(await service.IsReadAsync(userId, a1));
            Assert.False(await service.IsReadAsync(userId, a2));

            var p1 = await service.GetProgressAsync(userId, a1);
            Assert.Equal(ReadingState.Unread, p1!.State);

            var p2 = await service.GetProgressAsync(userId, a2);
            Assert.Equal(ReadingState.Unread, p2!.State);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SetFolderRead_OnArchiveNode_ReturnsNull()
    {
        var (db, userId, _, _, itemId) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            // itemId is an archive, not a folder.
            var result = await service.SetFolderReadAsync(userId, itemId, read: true);
            Assert.Null(result);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task LibraryPreferences_ReturnsDefaults_ThenPersists_WithoutClobberingReaderPrefs()
    {
        var (db, userId, _, _, _) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            // Set reader prefs first, then library prefs — they share one row but
            // must not overwrite each other.
            await service.SetPreferencesAsync(userId, new UserPreferencesDto
            {
                DefaultReaderMode = ReaderMode.VerticalWebtoon,
                ReducedMotion = true,
            });

            var def = await service.GetLibraryPreferencesAsync(userId);
            Assert.Equal("grid", def.ViewMode);
            Assert.Equal("comfortable", def.Density);
            Assert.Equal("name", def.Sort);
            Assert.Equal("", def.Direction); // unset (1.5.0) — sort-specific default applies downstream
            Assert.Equal("", def.CardSize); // unset (1.6.0) — frontend derives from legacy ViewMode/Density
            Assert.Equal(0, def.LibraryPageSize); // unset (1.8.0) — frontend applies its default (50)

            await service.SetLibraryPreferencesAsync(userId, new LibraryViewPreferencesDto
            {
                ViewMode = "card",
                Density = "compact",
                Sort = "recentlyAdded",
                Direction = "desc",
                CardSize = "168",
                LibraryPageSize = 100,
            });

            var lib = await service.GetLibraryPreferencesAsync(userId);
            Assert.Equal("card", lib.ViewMode);
            Assert.Equal("compact", lib.Density);
            Assert.Equal("recentlyAdded", lib.Sort);
            Assert.Equal("desc", lib.Direction);
            Assert.Equal("168", lib.CardSize); // 1.6.0 card size round-trips
            Assert.Equal(100, lib.LibraryPageSize); // 1.8.0 page size round-trips

            // Reader prefs survived the library-prefs write.
            var reader = await service.GetPreferencesAsync(userId);
            Assert.Equal(ReaderMode.VerticalWebtoon, reader.DefaultReaderMode);
            Assert.True(reader.ReducedMotion);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task LibraryPreferences_Direction_RoundTrips()
    {
        var (db, userId, _, _, _) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);

            await service.SetLibraryPreferencesAsync(userId, new LibraryViewPreferencesDto
            {
                Sort = "name",
                Direction = "asc",
            });
            Assert.Equal("asc", (await service.GetLibraryPreferencesAsync(userId)).Direction);

            await service.SetLibraryPreferencesAsync(userId, new LibraryViewPreferencesDto
            {
                Sort = "recentlyRead",
                Direction = "desc",
            });
            Assert.Equal("desc", (await service.GetLibraryPreferencesAsync(userId)).Direction);

            // Clearing back to "" (unset) round-trips too.
            await service.SetLibraryPreferencesAsync(userId, new LibraryViewPreferencesDto
            {
                Sort = "recentlyRead",
                Direction = "",
            });
            Assert.Equal("", (await service.GetLibraryPreferencesAsync(userId)).Direction);
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

            var result = await service.UpdateProgressAsync(reader.Id, itemId, 3, 1, mutationId: "mut-1");
            Assert.Equal(UpdateStatus.Unauthorized, result.Status);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateProgress_ConcurrentFirstWrites_DoNotRaceOnUniqueIndex()
    {
        // Regression: several "first writes" for the same (user, item) arriving at once
        // each read no existing row and tried to INSERT, so the losers hit the
        // reading_progress (UserId, ItemId) unique index and threw DbUpdateException - an
        // intermittent 500 in production. The upsert now recovers a concurrent insert by
        // re-applying the write as an update. Separate contexts share the file DB; the
        // connection's 30s busy timeout serializes writers, so a loser hits the unique
        // constraint (SQLite error 19) rather than SQLITE_BUSY.
        var (seedDb, userId, _, _, itemId) = await SetupAsync();
        await seedDb.DisposeAsync();

        const int writers = 8;
        var tasks = Enumerable.Range(0, writers).Select(async i =>
        {
            var db = new MangaPlexDbContext(_options);
            try
            {
                var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
                return await service.UpdateProgressAsync(userId, itemId, pageIndex: i,
                    expectedContentVersion: 1, mutationId: $"mut-{i}");
            }
            finally { await db.DisposeAsync(); }
        });

        var results = await Task.WhenAll(tasks);

        // No writer threw; every write reports success (losers as recovered updates).
        Assert.All(results, r => Assert.Equal(UpdateStatus.Success, r.Status));

        // Exactly one progress row exists for (user, item) - no duplicate slipped through.
        await using var verify = new MangaPlexDbContext(_options);
        var count = await verify.ReadingProgress.CountAsync(p => p.UserId == userId && p.ItemId == itemId);
        Assert.Equal(1, count);
    }
}
