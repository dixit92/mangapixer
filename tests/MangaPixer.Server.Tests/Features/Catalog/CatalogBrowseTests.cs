namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Integration tests for CatalogBrowseService.
/// Uses real file-backed SQLite with FTS5 trigram.
/// </summary>
public sealed class CatalogBrowseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public CatalogBrowseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-browse-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "browse.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext db, long userId, long libraryId)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        // Create a library
        var library = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test Library",
            RootPath = "/private/test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        // Create an admin user
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

        return (db, user.Id, library.Id);
    }

    private static async Task<CatalogNodeEntity> AddNodeAsync(
        MangaPixerDbContext db,
        long libraryId,
        long? parentId,
        CatalogNodeKind kind,
        string displayName,
        string sortKey,
        DateTimeOffset? createdAt = null)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = (int)kind,
            DisplayName = displayName,
            RelativePath = "/private/" + displayName,
            PathKey = "/private/" + displayName.ToLowerInvariant(),
            SortKey = sortKey,
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    /// <summary>
    /// Creates a reading-progress row for a user+item (simulates "recently read").
    /// </summary>
    private static async Task AddProgressAsync(
        MangaPixerDbContext db,
        long userId,
        long itemId,
        DateTimeOffset updatedAt,
        int state = 1)
    {
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = userId,
            ItemId = itemId,
            ContentVersion = 1,
            EntryKey = "entry1",
            Ordinal = 0,
            State = state,
            Revision = 1,
            LastMutationId = Guid.NewGuid().ToString("N"),
            UpdatedAt = updatedAt,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Browse_RootChildren_ReturnsFoldersAndArchives()
    {
        var (db, userId, libraryId) = await SetupAsync();

        try
        {
            // Add root children: two folders and two archives
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder A", "0A");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder B", "0B");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive 1", "1A1");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive 2", "1A2");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            Assert.Equal(4, result.Items.Count);
            // Folders first (sort key prefix 0), then archives (prefix 1)
            Assert.Equal(CatalogNodeKind.Folder, result.Items[0].Kind);
            Assert.Equal(CatalogNodeKind.Folder, result.Items[1].Kind);
            Assert.Equal(CatalogNodeKind.Archive, result.Items[2].Kind);
            Assert.Equal(CatalogNodeKind.Archive, result.Items[3].Kind);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task Browse_ExcludesTombstonedNodes()
    {
        var (db, userId, libraryId) = await SetupAsync();

        try
        {
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Active", "1Active");
            var tombstoned = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Gone", "1Gone");
            tombstoned.Availability = (int)CatalogNodeAvailability.Tombstoned;
            await db.SaveChangesAsync();

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            Assert.Single(result.Items);
            Assert.Equal("Active", result.Items[0].DisplayName);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task Browse_UnauthorizedUser_ReturnsEmpty()
    {
        var (db, userId, libraryId) = await SetupAsync();

        try
        {
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Test", "1Test");

            // Create a non-admin user without grant
            var reader = new UserEntity
            {
                PublicId = OpaqueId.Encode(Random.Shared.NextInt64(10, long.MaxValue)),
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

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(reader.Id, libraryId, parentId: null, cursor: null);

            Assert.Empty(result.Items);
            Assert.Equal(0, result.TotalCount);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task Browse_AdminUserWithGrant_ReturnsAllItems()
    {
        var (db, userId, libraryId) = await SetupAsync();

        try
        {
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Item 1", "1I1");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Item 2", "1I2");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            Assert.Equal(2, result.Items.Count);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetBreadcrumbs_ReturnsTrailFromRootToNode()
    {
        var (db, userId, libraryId) = await SetupAsync();

        try
        {
            var root = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            var child = await AddNodeAsync(db, libraryId, root.Id, CatalogNodeKind.Folder, "Volume 1", "0V1");
            var archive = await AddNodeAsync(db, libraryId, child.Id, CatalogNodeKind.Archive, "Chapter 1", "1C1");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var breadcrumbs = await service.GetBreadcrumbsAsync(userId, archive.Id);

            Assert.NotNull(breadcrumbs);
            Assert.Equal(2, breadcrumbs!.Trail.Count);
            Assert.Equal("Series", breadcrumbs.Trail[0].DisplayName);
            Assert.Equal("Volume 1", breadcrumbs.Trail[1].DisplayName);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetBreadcrumbs_UnauthorizedUser_ReturnsNull()
    {
        var (db, userId, libraryId) = await SetupAsync();

        try
        {
            var node = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Test", "1T");

            // Create unauthorized reader
            var reader = new UserEntity
            {
                PublicId = OpaqueId.Encode(Random.Shared.NextInt64(10, long.MaxValue)),
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

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var breadcrumbs = await service.GetBreadcrumbsAsync(reader.Id, node.Id);

            Assert.Null(breadcrumbs);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetNeighbors_ReturnsPreviousAndNextInSameFolder()
    {
        var (db, userId, libraryId) = await SetupAsync();

        try
        {
            var parent = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            var a = await AddNodeAsync(db, libraryId, parent.Id, CatalogNodeKind.Archive, "Chapter 1", "1C1");
            var b = await AddNodeAsync(db, libraryId, parent.Id, CatalogNodeKind.Archive, "Chapter 2", "1C2");
            var c = await AddNodeAsync(db, libraryId, parent.Id, CatalogNodeKind.Archive, "Chapter 3", "1C3");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

            // Middle item has both neighbors
            var middle = await service.GetNeighborsAsync(userId, b.Id);
            Assert.NotNull(middle);
            Assert.Equal("Chapter 1", middle!.Previous!.DisplayName);
            Assert.Equal("Chapter 3", middle.Next!.DisplayName);

            // First item has no previous
            var first = await service.GetNeighborsAsync(userId, a.Id);
            Assert.Null(first!.Previous);
            Assert.Equal("Chapter 2", first.Next!.DisplayName);

            // Last item has no next
            var last = await service.GetNeighborsAsync(userId, c.Id);
            Assert.Equal("Chapter 2", last!.Previous!.DisplayName);
            Assert.Null(last.Next);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetNode_ReturnsNodeByPublicId()
    {
        var (db, userId, libraryId) = await SetupAsync();

        try
        {
            var node = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Test Archive", "1TA");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.GetNodeAsync(userId, node.PublicId);

            Assert.NotNull(result);
            Assert.Equal("Test Archive", result!.DisplayName);
            Assert.Equal(CatalogNodeKind.Archive, result.Kind);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetNode_UnauthorizedUser_ReturnsNull()
    {
        var (db, userId, libraryId) = await SetupAsync();

        try
        {
            var node = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Test", "1T");

            var reader = new UserEntity
            {
                PublicId = OpaqueId.Encode(Random.Shared.NextInt64(10, long.MaxValue)),
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

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.GetNodeAsync(reader.Id, node.PublicId);

            Assert.Null(result);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmptyResults()
    {
        var (db, userId, libraryId) = await SetupAsync();

        try
        {
            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.SearchAsync(userId, "");

            Assert.Empty(result.Items);
            Assert.Equal(0, result.TotalCount);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task Search_UnauthorizedUser_ReturnsEmptyResults()
    {
        var (db, userId, libraryId) = await SetupAsync();

        try
        {
            // Create a non-admin user without grant
            var reader = new UserEntity
            {
                PublicId = OpaqueId.Encode(Random.Shared.NextInt64(10, long.MaxValue)),
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

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.SearchAsync(reader.Id, "test");

            Assert.Empty(result.Items);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    // --- Sort tests (view-mode sort follow-up) ---

    [Fact]
    public async Task Search_FolderWithDescendantArchive_ResolvesCover()
    {
        // Contract guard for the 1.8.1 search folder-cover fix: the search
        // endpoint must return a coverUrl for a folder that has a readable
        // descendant archive (ResolveFolderCoversAsync, parity with browse).
        // The frontend search component renders this coverUrl; without it a
        // folder result shows only the generic folder icon.
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var folder = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Failure Frame", "0Failure Frame");
            await AddNodeAsync(db, libraryId, folder.Id, CatalogNodeKind.Archive, "Failure Frame Vol 1", "1Failure Frame Vol 1");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.SearchAsync(userId, "Failure");

            var folderNode = result.Items.Single(n => n.Kind == CatalogNodeKind.Folder);
            Assert.NotNull(folderNode.CoverUrl);
            Assert.Equal($"/api/v1/items/{result.Items.Single(n => n.Kind == CatalogNodeKind.Archive).Id}/cover", folderNode.CoverUrl);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyAdded_ReturnsCorrectOrder()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            // Folders first by CreatedAt DESC, then archives by CreatedAt DESC.
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder Old", "0FO", t0);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder New", "0FN", t2);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Old", "1AO", t0);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Mid", "1AM", t1);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive New", "1AN", t2);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null, sort: "recentlyAdded");

            Assert.Equal(5, result.Items.Count);
            // Folders first (newest first), then archives (newest first)
            Assert.Equal("Folder New", result.Items[0].DisplayName);
            Assert.Equal("Folder Old", result.Items[1].DisplayName);
            Assert.Equal("Archive New", result.Items[2].DisplayName);
            Assert.Equal("Archive Mid", result.Items[3].DisplayName);
            Assert.Equal("Archive Old", result.Items[4].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyAdded_PagesCorrectlyAcrossBoundary()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            // 7 archives with distinct CreatedAt values (newest first in output).
            for (int i = 0; i < 7; i++)
            {
                await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive,
                    $"Archive {i}", $"1A{i}", baseTime.AddDays(-i));
            }

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var allNames = new List<string>();
            string? cursor = null;
            bool hasMore;

            // Page through with pageSize=3
            do
            {
                var page = await service.BrowseAsync(userId, libraryId, parentId: null,
                    cursor: cursor, pageSize: 3, sort: "recentlyAdded");
                allNames.AddRange(page.Items.Select(n => n.DisplayName));
                cursor = page.NextCursor;
                hasMore = page.HasMore;
            } while (hasMore);

            // All 7 items, no duplicates, correct order (newest first).
            Assert.Equal(7, allNames.Count);
            Assert.Equal(7, allNames.Distinct().Count());
            for (int i = 0; i < 7; i++)
                Assert.Equal($"Archive {i}", allNames[i]);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyRead_PureRecency_InterleavesByActivity()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            // A folder with no reading activity anywhere in its subtree.
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder A", "0FA", t0);

            // Archives with progress.
            var arch1 = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Read Old", "1ARO", t0);
            var arch2 = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Read New", "1ARN", t0);
            await AddProgressAsync(db, userId, arch1.Id, t1);
            await AddProgressAsync(db, userId, arch2.Id, t2);

            // Archives with no activity.
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Unread B", "1AUB", t0);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Unread A", "1AUA", t0);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null, sort: "recentlyRead");

            Assert.Equal(5, result.Items.Count);
            // Pure recency: read items by activity (newest first), then no-activity items by
            // SortKey - regardless of folder/archive kind. The unread folder falls to the
            // no-activity group and sorts by its SortKey ("0FA" < "1AUA" < "1AUB").
            Assert.Equal("Archive Read New", result.Items[0].DisplayName);
            Assert.Equal("Archive Read Old", result.Items[1].DisplayName);
            Assert.Equal("Folder A", result.Items[2].DisplayName);
            Assert.Equal("Archive Unread A", result.Items[3].DisplayName);
            Assert.Equal("Archive Unread B", result.Items[4].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyRead_FolderRanksByDescendantActivity()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            var t3 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            // Three series folders, none with direct progress. Each holds a chapter; Series Z
            // nests the chapter under a Season sub-folder to exercise recursion (>1 level).
            var seriesX = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series X", "0SX", t1);
            var seriesY = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series Y", "0SY", t1);
            var seriesZ = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series Z", "0SZ", t1);
            var seasonZ = await AddNodeAsync(db, libraryId, seriesZ.Id, CatalogNodeKind.Folder, "Season 1", "0S1", t1);

            var chX = await AddNodeAsync(db, libraryId, seriesX.Id, CatalogNodeKind.Archive, "X Ch 1", "1C1", t1);
            var chY = await AddNodeAsync(db, libraryId, seriesY.Id, CatalogNodeKind.Archive, "Y Ch 1", "1C1", t1);
            var chZ = await AddNodeAsync(db, libraryId, seasonZ.Id, CatalogNodeKind.Archive, "Z Ch 1", "1C1", t1);

            // X read at t2 (progress); Y read at t1 (progress, oldest); Z marked read at t3
            // (a read-mark, newest) - read-marks count toward recency.
            await AddProgressAsync(db, userId, chX.Id, t2);
            await AddProgressAsync(db, userId, chY.Id, t1);
            db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = chZ.Id, MarkedAt = t3, Source = "manual" });
            await db.SaveChangesAsync();

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null, sort: "recentlyRead");

            // Series ranked by their descendant's most recent read activity: Z (t3 read-mark,
            // nested two levels) > X (t2 progress) > Y (t1 progress).
            Assert.Equal(3, result.Items.Count);
            Assert.Equal("Series Z", result.Items[0].DisplayName);
            Assert.Equal("Series X", result.Items[1].DisplayName);
            Assert.Equal("Series Y", result.Items[2].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyRead_PagesCorrectlyAcrossSegments()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            // 1 folder, 2 archives with progress, 3 archives without progress = 6 total.
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder", "0F", t0);
            var arch1 = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Read1", "1R1", t0);
            var arch2 = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Read2", "1R2", t0);
            await AddProgressAsync(db, userId, arch1.Id, t1);
            await AddProgressAsync(db, userId, arch2.Id, t2);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Unread3", "1U3", t0);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Unread1", "1U1", t0);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Unread2", "1U2", t0);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var allNames = new List<string>();
            string? cursor = null;
            bool hasMore;

            // Page through with pageSize=2 (crosses all 3 segments).
            do
            {
                var page = await service.BrowseAsync(userId, libraryId, parentId: null,
                    cursor: cursor, pageSize: 2, sort: "recentlyRead");
                allNames.AddRange(page.Items.Select(n => n.DisplayName));
                cursor = page.NextCursor;
                hasMore = page.HasMore;
            } while (hasMore);

            // Pure recency across the paging boundary: read items by activity (newest first),
            // then no-activity items by SortKey. The unread folder ("0F") sorts into the
            // no-activity group ahead of the unread archives ("1U*"), not to the front.
            // Expected: Read2, Read1, Folder, Unread1, Unread2, Unread3
            Assert.Equal(6, allNames.Count);
            Assert.Equal(6, allNames.Distinct().Count());
            Assert.Equal("Read2", allNames[0]);
            Assert.Equal("Read1", allNames[1]);
            Assert.Equal("Folder", allNames[2]);
            Assert.Equal("Unread1", allNames[3]);
            Assert.Equal("Unread2", allNames[4]);
            Assert.Equal("Unread3", allNames[5]);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NameSort_UnchangedBehavior()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder B", "0B");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder A", "0A");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Z", "1Z");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive A", "1A");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null, sort: "name");

            Assert.Equal(4, result.Items.Count);
            // Folders first (by SortKey), then archives (by SortKey).
            Assert.Equal("Folder A", result.Items[0].DisplayName);
            Assert.Equal("Folder B", result.Items[1].DisplayName);
            Assert.Equal("Archive A", result.Items[2].DisplayName);
            Assert.Equal("Archive Z", result.Items[3].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Sort direction (1.5.0): every sort honors ascending/descending ---

    [Fact]
    public async Task Browse_NameSort_Descending_ReversesOrder()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder B", "0B");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder A", "0A");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Z", "1Z");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive A", "1A");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                sort: "name", direction: SortDirection.Descending);

            // Full reversal of the SortKey ordering (archives, "1*", sort before
            // folders, "0*", once the whole key is reversed) — same semantics as
            // Name-descending had pre-1.5.0, just now reachable via the parameter.
            Assert.Equal(4, result.Items.Count);
            Assert.Equal("Archive Z", result.Items[0].DisplayName);
            Assert.Equal("Archive A", result.Items[1].DisplayName);
            Assert.Equal("Folder B", result.Items[2].DisplayName);
            Assert.Equal("Folder A", result.Items[3].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NameSort_Descending_PagesCorrectlyAcrossBoundary()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            for (int i = 0; i < 7; i++)
                await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, $"Archive {i}", $"1A{i:D2}");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var allNames = new List<string>();
            string? cursor = null;
            bool hasMore;

            do
            {
                var page = await service.BrowseAsync(userId, libraryId, parentId: null,
                    cursor: cursor, pageSize: 3, sort: "name", direction: SortDirection.Descending);
                allNames.AddRange(page.Items.Select(n => n.DisplayName));
                cursor = page.NextCursor;
                hasMore = page.HasMore;
            } while (hasMore);

            Assert.Equal(7, allNames.Count);
            Assert.Equal(7, allNames.Distinct().Count());
            for (int i = 0; i < 7; i++)
                Assert.Equal($"Archive {6 - i}", allNames[i]);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyAdded_IgnoresAscendingDirection()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder Old", "0FO", t0);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder New", "0FN", t2);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Old", "1AO", t0);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Mid", "1AM", t1);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive New", "1AN", t2);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            // 1.10.4: recency sorts are inherently newest-first; an Ascending direction is IGNORED
            // and yields the same order as Descending (an ascending "Recently added" would show the
            // OLDEST first, contradicting the label).
            var asc = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                sort: "recentlyAdded", direction: SortDirection.Ascending);
            var desc = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                sort: "recentlyAdded", direction: SortDirection.Descending);

            Assert.Equal(
                desc.Items.Select(i => i.DisplayName).ToList(),
                asc.Items.Select(i => i.DisplayName).ToList());
            // Newest-added precedes oldest-added regardless of the requested direction.
            var names = asc.Items.Select(i => i.DisplayName).ToList();
            Assert.True(names.IndexOf("Archive New") < names.IndexOf("Archive Old"));
            Assert.True(names.IndexOf("Folder New") < names.IndexOf("Folder Old"));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyAdded_Ascending_PagesLikeDescending()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            for (int i = 0; i < 7; i++)
            {
                await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive,
                    $"Archive {i}", $"1A{i}", baseTime.AddDays(-i));
            }

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

            // 1.10.4: an Ascending request for a recency sort is ignored. Paginating with
            // Ascending must produce the SAME newest-first sequence as a single Descending
            // fetch - and correct paging (no gaps/dupes) is preserved across the page boundary.
            var expected = (await service.BrowseAsync(userId, libraryId, parentId: null,
                cursor: null, pageSize: 100, sort: "recentlyAdded", direction: SortDirection.Descending))
                .Items.Select(n => n.DisplayName).ToList();

            var allNames = new List<string>();
            string? cursor = null;
            bool hasMore;

            do
            {
                var page = await service.BrowseAsync(userId, libraryId, parentId: null,
                    cursor: cursor, pageSize: 3, sort: "recentlyAdded", direction: SortDirection.Ascending);
                allNames.AddRange(page.Items.Select(n => n.DisplayName));
                cursor = page.NextCursor;
                hasMore = page.HasMore;
            } while (hasMore);

            Assert.Equal(7, allNames.Count);
            Assert.Equal(7, allNames.Distinct().Count());
            Assert.Equal(expected, allNames);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyRead_IgnoresAscendingDirection()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder A", "0FA", t0);
            var arch1 = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Read Old", "1ARO", t0);
            var arch2 = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Read New", "1ARN", t0);
            await AddProgressAsync(db, userId, arch1.Id, t1);
            await AddProgressAsync(db, userId, arch2.Id, t2);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Unread B", "1AUB", t0);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Unread A", "1AUA", t0);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            // 1.10.4: "Recently read" is inherently most-recent-first; an Ascending direction is
            // IGNORED and yields the same order as Descending (the most-recently-read archive stays
            // at the top).
            var asc = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                sort: "recentlyRead", direction: SortDirection.Ascending);
            var desc = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                sort: "recentlyRead", direction: SortDirection.Descending);

            Assert.Equal(
                desc.Items.Select(i => i.DisplayName).ToList(),
                asc.Items.Select(i => i.DisplayName).ToList());
            // Most-recently-read precedes the older read regardless of the requested direction.
            var names = asc.Items.Select(i => i.DisplayName).ToList();
            Assert.True(names.IndexOf("Archive Read New") < names.IndexOf("Archive Read Old"));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyRead_Ascending_PagesLikeDescending()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Folder", "0F", t0);
            var arch1 = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Read1", "1R1", t0);
            var arch2 = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Read2", "1R2", t0);
            await AddProgressAsync(db, userId, arch1.Id, t1);
            await AddProgressAsync(db, userId, arch2.Id, t2);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Unread3", "1U3", t0);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Unread1", "1U1", t0);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Unread2", "1U2", t0);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

            // 1.10.4: an Ascending request for "recently read" is ignored. Paginating with
            // Ascending must produce the SAME order as a single Descending fetch - including
            // correct paging (no gaps/dupes) across the activity/no-activity segment boundary.
            var expected = (await service.BrowseAsync(userId, libraryId, parentId: null,
                cursor: null, pageSize: 100, sort: "recentlyRead", direction: SortDirection.Descending))
                .Items.Select(n => n.DisplayName).ToList();

            var allNames = new List<string>();
            string? cursor = null;
            bool hasMore;

            do
            {
                var page = await service.BrowseAsync(userId, libraryId, parentId: null,
                    cursor: cursor, pageSize: 2, sort: "recentlyRead", direction: SortDirection.Ascending);
                allNames.AddRange(page.Items.Select(n => n.DisplayName));
                cursor = page.NextCursor;
                hasMore = page.HasMore;
            } while (hasMore);

            Assert.Equal(6, allNames.Count);
            Assert.Equal(6, allNames.Distinct().Count());
            Assert.Equal(expected, allNames);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyAdded_NoExplicitDirection_DefaultsDescending()
    {
        // BrowseAsync callers that don't pass `direction` (e.g. pre-1.5.0 call
        // sites, or a request with neither a query param nor a stored preference)
        // must keep getting the pre-1.5.0 descending default for non-name sorts.
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive Old", "1AO", t0);
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive New", "1AN", t1);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null, sort: "recentlyAdded");

            Assert.Equal("Archive New", result.Items[0].DisplayName);
            Assert.Equal("Archive Old", result.Items[1].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_InvalidSort_FallsBackToName()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive B", "1B");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive A", "1A");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null, sort: "bogus");

            // Falls back to name sort (by SortKey).
            Assert.Equal(2, result.Items.Count);
            Assert.Equal("Archive A", result.Items[0].DisplayName);
            Assert.Equal("Archive B", result.Items[1].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_MalformedCursor_StartsFromBeginning()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive A", "1A");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "Archive B", "1B");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

            // Malformed cursor for recentlyAdded (garbage after prefix).
            var result = await service.BrowseAsync(userId, libraryId, parentId: null,
                cursor: "a:garbage", sort: "recentlyAdded");
            Assert.Equal(2, result.Items.Count);

            // Malformed cursor for recentlyRead.
            result = await service.BrowseAsync(userId, libraryId, parentId: null,
                cursor: "r:garbage", sort: "recentlyRead");
            Assert.Equal(2, result.Items.Count);

            // Cursor from a different sort (name cursor with recentlyAdded sort).
            result = await service.BrowseAsync(userId, libraryId, parentId: null,
                cursor: "1SomeSortKey", sort: "recentlyAdded");
            Assert.Equal(2, result.Items.Count);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Folder cover resolution (1.3.1: subfolder recursion) ---

    [Fact]
    public async Task Browse_FolderWithDirectArchiveChild_ResolvesCover()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var folder = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            var archive = await AddNodeAsync(db, libraryId, folder.Id, CatalogNodeKind.Archive, "Chapter 1", "1C1");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            var folderNode = result.Items.Single(n => n.Kind == CatalogNodeKind.Folder);
            Assert.NotNull(folderNode.CoverUrl);
            Assert.Equal($"/api/v1/items/{archive.PublicId}/cover", folderNode.CoverUrl);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_SubfolderOnlyFolder_ResolvesCoverFromDeepFirstArchive()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Root folder contains only subfolders (no direct archive children).
            var root = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Root", "0R");
            var subA = await AddNodeAsync(db, libraryId, root.Id, CatalogNodeKind.Folder, "SubA", "0A");
            var subB = await AddNodeAsync(db, libraryId, root.Id, CatalogNodeKind.Folder, "SubB", "0B");

            // SubA has a deep archive; SubB has a shallower archive with a LATER sort key.
            // The first descendant archive by SortKey (ordinal) should win regardless of depth.
            var deepFirst = await AddNodeAsync(db, libraryId, subA.Id, CatalogNodeKind.Archive, "Deep First", "1AA");
            await AddNodeAsync(db, libraryId, subB.Id, CatalogNodeKind.Archive, "Shallow Later", "1BB");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            var folderNode = result.Items.Single(n => n.Kind == CatalogNodeKind.Folder);
            Assert.NotNull(folderNode.CoverUrl);
            // "Deep First" (SortKey 1AA) sorts before "Shallow Later" (1BB), so it wins
            // even though it is nested deeper.
            Assert.Equal($"/api/v1/items/{deepFirst.PublicId}/cover", folderNode.CoverUrl);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_EmptyFolder_HasNoCover()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Empty", "0E");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            var folderNode = result.Items.Single(n => n.Kind == CatalogNodeKind.Folder);
            Assert.Null(folderNode.CoverUrl);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_FolderWithOnlyTombstonedDescendants_HasNoCover()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var root = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Root", "0R");
            var sub = await AddNodeAsync(db, libraryId, root.Id, CatalogNodeKind.Folder, "Sub", "0S");
            var tombstoned = await AddNodeAsync(db, libraryId, sub.Id, CatalogNodeKind.Archive, "Gone", "1G");
            tombstoned.Availability = (int)CatalogNodeAvailability.Tombstoned;
            await db.SaveChangesAsync();

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            var folderNode = result.Items.Single(n => n.Kind == CatalogNodeKind.Folder);
            Assert.Null(folderNode.CoverUrl);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_FolderCover_PicksFirstArchiveByNameSortKeyOrder()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Webtoon pattern: a cover-only @000.cbz archive that sorts first by
            // name/sort-key order, followed by chapter archives. The folder cover
            // should come from @000.cbz's first page (1.6.1 confirmation).
            var rootKey = SortKey.ForLibraryRoot();
            var folderKey = SortKey.ForNode(CatalogNodeKind.Folder, "Series", rootKey);
            var folder = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", folderKey);

            // Add chapters first (by insertion order) to ensure the cover is picked
            // by sort-key order, not by insertion or ID order.
            await AddNodeAsync(db, libraryId, folder.Id, CatalogNodeKind.Archive, "Chapter 002.cbz",
                SortKey.ForNode(CatalogNodeKind.Archive, "Chapter 002.cbz", folderKey));
            await AddNodeAsync(db, libraryId, folder.Id, CatalogNodeKind.Archive, "Chapter 001.cbz",
                SortKey.ForNode(CatalogNodeKind.Archive, "Chapter 001.cbz", folderKey));
            var coverArchive = await AddNodeAsync(db, libraryId, folder.Id, CatalogNodeKind.Archive, "@000.cbz",
                SortKey.ForNode(CatalogNodeKind.Archive, "@000.cbz", folderKey));

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            var folderNode = result.Items.Single(n => n.Kind == CatalogNodeKind.Folder);
            Assert.NotNull(folderNode.CoverUrl);
            // @000.cbz sorts before Chapter 001.cbz in natural/name order
            // (@ = ASCII 64 < C = ASCII 67 ordinally), so its first page is the cover.
            Assert.Equal($"/api/v1/items/{coverArchive.PublicId}/cover", folderNode.CoverUrl);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Folder read rollup (1.6.0: derived Read / Reading / Unread over descendants) ---

    private static async Task AddReadMarkAsync(MangaPixerDbContext db, long userId, long itemId)
    {
        db.ReadMarks.Add(new ReadMarkEntity
        {
            UserId = userId,
            ItemId = itemId,
            MarkedAt = DateTimeOffset.UtcNow,
            Source = "manual",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Browse_FolderRollup_AllDescendantsRead_IsRead()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Nested: Series/Vol1/{Ch1, Ch2}, Series/Ch3 - every readable archive read.
            var series = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            var vol1 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Folder, "Vol 1", "0V1");
            var ch1 = await AddNodeAsync(db, libraryId, vol1.Id, CatalogNodeKind.Archive, "Ch 1", "1C1");
            var ch2 = await AddNodeAsync(db, libraryId, vol1.Id, CatalogNodeKind.Archive, "Ch 2", "1C2");
            var ch3 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 3", "1C3");
            await AddReadMarkAsync(db, userId, ch1.Id);
            await AddReadMarkAsync(db, userId, ch2.Id);
            await AddReadMarkAsync(db, userId, ch3.Id);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var root = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);
            Assert.Equal(FolderReadRollup.Read, root.Items.Single().ReadRollup);

            // The nested volume folder rolls up independently when browsing into Series.
            var inside = await service.BrowseAsync(userId, libraryId, parentId: series.Id, cursor: null);
            var volNode = inside.Items.Single(n => n.Kind == CatalogNodeKind.Folder);
            Assert.Equal(FolderReadRollup.Read, volNode.ReadRollup);
            // Archives never carry a rollup - they carry IsRead instead.
            var archiveNode = inside.Items.Single(n => n.Kind == CatalogNodeKind.Archive);
            Assert.Null(archiveNode.ReadRollup);
            Assert.True(archiveNode.IsRead);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_FolderRollup_SomeReadOrInProgress_IsReading()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Folder A: one of two read (partial by read-marks only).
            var a = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "A", "0A");
            var a1 = await AddNodeAsync(db, libraryId, a.Id, CatalogNodeKind.Archive, "A1", "1A1");
            await AddNodeAsync(db, libraryId, a.Id, CatalogNodeKind.Archive, "A2", "1A2");
            await AddReadMarkAsync(db, userId, a1.Id);

            // Folder B: none read, one in progress deep in a subfolder (partial by progress).
            var b = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "B", "0B");
            var bSub = await AddNodeAsync(db, libraryId, b.Id, CatalogNodeKind.Folder, "BSub", "0BS");
            var b1 = await AddNodeAsync(db, libraryId, bSub.Id, CatalogNodeKind.Archive, "B1", "1B1");
            await AddNodeAsync(db, libraryId, b.Id, CatalogNodeKind.Archive, "B2", "1B2");
            await AddProgressAsync(db, userId, b1.Id, DateTimeOffset.UtcNow, state: (int)ReadingState.InProgress);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            Assert.Equal(FolderReadRollup.Reading, result.Items.Single(n => n.DisplayName == "A").ReadRollup);
            Assert.Equal(FolderReadRollup.Reading, result.Items.Single(n => n.DisplayName == "B").ReadRollup);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_FolderRollup_NothingReadOrInProgress_IsUnread()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var f = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Fresh", "0F");
            var f1 = await AddNodeAsync(db, libraryId, f.Id, CatalogNodeKind.Archive, "F1", "1F1");
            await AddNodeAsync(db, libraryId, f.Id, CatalogNodeKind.Archive, "F2", "1F2");
            // A Completed progress row WITHOUT a read-mark does not count as read (the
            // archive card would show neither badge), so the folder stays Unread - the
            // rollup composes from exactly the signals the cards display.
            await AddProgressAsync(db, userId, f1.Id, DateTimeOffset.UtcNow, state: (int)ReadingState.Completed);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            Assert.Equal(FolderReadRollup.Unread, result.Items.Single().ReadRollup);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_FolderRollup_EmptyOrTombstonedOnly_IsNull()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Empty", "0E");
            var gone = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "GoneOnly", "0G");
            var tomb = await AddNodeAsync(db, libraryId, gone.Id, CatalogNodeKind.Archive, "Gone", "1G");
            tomb.Availability = (int)CatalogNodeAvailability.Tombstoned;
            await db.SaveChangesAsync();
            // A read-mark on a tombstoned archive must not make the folder "Read".
            await AddReadMarkAsync(db, userId, tomb.Id);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            Assert.Equal(2, result.Items.Count);
            Assert.All(result.Items, n => Assert.Null(n.ReadRollup));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_FolderRollup_IsPerUser()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var other = new UserEntity
            {
                PublicId = OpaqueId.Encode(3),
                UserName = "other",
                NormalizedUserName = "OTHER",
                IsActive = true,
                IsAdmin = true,
                PasswordHash = "hash",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(other);
            await db.SaveChangesAsync();

            var f = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Shared", "0S");
            var f1 = await AddNodeAsync(db, libraryId, f.Id, CatalogNodeKind.Archive, "S1", "1S1");
            await AddReadMarkAsync(db, userId, f1.Id);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var mine = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);
            var theirs = await service.BrowseAsync(other.Id, libraryId, parentId: null, cursor: null);

            Assert.Equal(FolderReadRollup.Read, mine.Items.Single().ReadRollup);
            Assert.Equal(FolderReadRollup.Unread, theirs.Items.Single().ReadRollup);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_FolderRollup_PopulatedAcrossAllSortModes()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var f = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "F", "0F");
            var f1 = await AddNodeAsync(db, libraryId, f.Id, CatalogNodeKind.Archive, "F1", "1F1");
            await AddReadMarkAsync(db, userId, f1.Id);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            foreach (var sort in new[] { "name", "recentlyAdded", "recentlyRead" })
            {
                var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null, sort: sort);
                Assert.Equal(FolderReadRollup.Read, result.Items.Single().ReadRollup);
            }
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Next-unread "Continue" row (1.7.0: pinned next-to-read descendant archive) ---

    [Fact]
    public async Task Browse_NextUnread_FirstUnreadArchiveBySortKey_WhenNothingRead()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var series = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 2", "1Ch2");
            var ch1 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 1", "1Ch1");
            await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 3", "1Ch3");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: series.Id, cursor: null);

            // Nothing read -> first UNREAD by SortKey (ordinal): "1Ch1" -> "Ch 1".
            Assert.NotNull(result.NextUnread);
            Assert.Equal(ch1.PublicId, result.NextUnread!.Id);
            Assert.Equal("Ch 1", result.NextUnread.DisplayName);
            Assert.Equal(CatalogNodeKind.Archive, result.NextUnread.Kind);
            Assert.Equal($"/api/v1/items/{ch1.PublicId}/cover", result.NextUnread.CoverUrl);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NextUnread_ResumesInProgressArchive_EvenWhenNotFirstBySort()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var series = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 1", "1Ch1");
            var ch2 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 2", "1Ch2");
            await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 3", "1Ch3");

            // Ch 2 (sorts after Ch 1) is in progress -> it wins over the first-by-sort unread.
            await AddProgressAsync(db, userId, ch2.Id, DateTimeOffset.UtcNow, state: (int)ReadingState.InProgress);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: series.Id, cursor: null);

            Assert.NotNull(result.NextUnread);
            Assert.Equal(ch2.PublicId, result.NextUnread!.Id);
            Assert.Equal(ReadingState.InProgress, result.NextUnread.ReadingState);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NextUnread_ResumesMostRecentlyUpdatedInProgressArchive()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var series = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            var ch1 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 1", "1Ch1");
            var ch2 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 2", "1Ch2");

            // Both in progress; Ch 1 was updated more recently -> it wins.
            await AddProgressAsync(db, userId, ch2.Id, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), state: (int)ReadingState.InProgress);
            await AddProgressAsync(db, userId, ch1.Id, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), state: (int)ReadingState.InProgress);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: series.Id, cursor: null);

            Assert.NotNull(result.NextUnread);
            Assert.Equal(ch1.PublicId, result.NextUnread!.Id);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NextUnread_SkipsReadArchives_AndPicksFirstUnread()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var series = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            var ch1 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 1", "1Ch1");
            var ch2 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 2", "1Ch2");
            await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 3", "1Ch3");

            // Ch 1 read -> skipped; Ch 2 is the first unread by sort.
            await AddReadMarkAsync(db, userId, ch1.Id);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: series.Id, cursor: null);

            Assert.NotNull(result.NextUnread);
            Assert.Equal(ch2.PublicId, result.NextUnread!.Id);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NextUnread_NullWhenEveryDescendantRead()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var series = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            var vol = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Folder, "Vol 1", "0V1");
            var ch1 = await AddNodeAsync(db, libraryId, vol.Id, CatalogNodeKind.Archive, "Ch 1", "1Ch1");
            var ch2 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 2", "1Ch2");
            await AddReadMarkAsync(db, userId, ch1.Id);
            await AddReadMarkAsync(db, userId, ch2.Id);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: series.Id, cursor: null);

            Assert.Null(result.NextUnread);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NextUnread_RecursesIntoSubfolders()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Series/Vol 1/Ch 1 (read), Series/Vol 2/Ch 2 (unread, deeper) -> Continue = Ch 2.
            var series = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            var vol1 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Folder, "Vol 1", "0V1");
            var vol2 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Folder, "Vol 2", "0V2");
            var ch1 = await AddNodeAsync(db, libraryId, vol1.Id, CatalogNodeKind.Archive, "Ch 1", "1Ch1");
            var ch2 = await AddNodeAsync(db, libraryId, vol2.Id, CatalogNodeKind.Archive, "Ch 2", "1Ch2");
            await AddReadMarkAsync(db, userId, ch1.Id);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: series.Id, cursor: null);

            Assert.NotNull(result.NextUnread);
            Assert.Equal(ch2.PublicId, result.NextUnread!.Id);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NextUnread_RootBrowsesWholeLibrary()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Two top-level folders; the only unread archive lives in the second.
            var a = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "A", "0A");
            var b = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "B", "0B");
            var a1 = await AddNodeAsync(db, libraryId, a.Id, CatalogNodeKind.Archive, "A1", "1A1");
            var b1 = await AddNodeAsync(db, libraryId, b.Id, CatalogNodeKind.Archive, "B1", "1B1");
            await AddReadMarkAsync(db, userId, a1.Id);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            // Browsing the library root -> descendants span both folders.
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null);

            Assert.NotNull(result.NextUnread);
            Assert.Equal(b1.PublicId, result.NextUnread!.Id);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NextUnread_NullForFolderWithNoReadableDescendants()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var empty = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Empty", "0E");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: empty.Id, cursor: null);

            Assert.Null(result.NextUnread);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NextUnread_IgnoresTombstonedArchives()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var series = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            var gone = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 0", "1Ch0");
            gone.Availability = (int)CatalogNodeAvailability.Tombstoned;
            await db.SaveChangesAsync();
            var ch1 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 1", "1Ch1");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: series.Id, cursor: null);

            Assert.NotNull(result.NextUnread);
            Assert.Equal(ch1.PublicId, result.NextUnread!.Id);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NextUnread_IsPerUser()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var other = new UserEntity
            {
                PublicId = OpaqueId.Encode(3),
                UserName = "other",
                NormalizedUserName = "OTHER",
                IsActive = true,
                IsAdmin = true,
                PasswordHash = "hash",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(other);
            await db.SaveChangesAsync();

            var series = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            var ch1 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 1", "1Ch1");
            var ch2 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 2", "1Ch2");

            // The primary user read Ch 1; the other user read nothing.
            await AddReadMarkAsync(db, userId, ch1.Id);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var mine = await service.BrowseAsync(userId, libraryId, parentId: series.Id, cursor: null);
            var theirs = await service.BrowseAsync(other.Id, libraryId, parentId: series.Id, cursor: null);

            Assert.Equal(ch2.PublicId, mine.NextUnread!.Id);
            Assert.Equal(ch1.PublicId, theirs.NextUnread!.Id);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_NextUnread_ReadMarkBeatsStaleInProgress_ResumeIsNotRead()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var series = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0S");
            var ch1 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 1", "1Ch1");
            var ch2 = await AddNodeAsync(db, libraryId, series.Id, CatalogNodeKind.Archive, "Ch 2", "1Ch2");

            // Ch 1 has BOTH a read-mark and stale in-progress progress: it is "read"
            // (read-mark wins, matching the rollup), so Continue skips to Ch 2.
            await AddReadMarkAsync(db, userId, ch1.Id);
            await AddProgressAsync(db, userId, ch1.Id, DateTimeOffset.UtcNow, state: (int)ReadingState.InProgress);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: series.Id, cursor: null);

            Assert.Equal(ch2.PublicId, result.NextUnread!.Id);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Read-state filter (1.10.0 archives; 1.11.0 folders by descendant rollup) ---

    private sealed record ReadStateFixture(
        long ParentId,
        string ReadArchiveId, string ReadingArchiveId, string UnreadPlainId, string UnreadCompletedId,
        string SubEmptyId, string SubReadId, string SubReadingId, string SubUnreadId);

    /// <summary>
    /// Seeds one parent folder "Series" holding four archives (one of each read state)
    /// plus four subfolders, one per folder rollup class, so the filter tests can assert
    /// BOTH which archives survive AND that folders are filtered by their descendant read
    /// rollup (1.11.0). Archive states: Read = read-mark; Reading = in-progress progress,
    /// no mark; Unread(plain) = no signal; Unread(completed) = a Completed progress row
    /// with no mark (the card shows no badge, so it counts as Unread). Subfolders:
    /// SubEmpty (no archives = null rollup), SubRead (one read archive = Read), SubReading
    /// (one in-progress archive = Reading), SubUnread (one plain archive = Unread).
    /// </summary>
    private async Task<ReadStateFixture> SeedReadStateFixtureAsync(
        MangaPixerDbContext db, long userId, long libraryId)
    {
        var parent = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Series", "0Series");
        var readArch = await AddNodeAsync(db, libraryId, parent.Id, CatalogNodeKind.Archive, "Ch Read", "1a Read");
        var readingArch = await AddNodeAsync(db, libraryId, parent.Id, CatalogNodeKind.Archive, "Ch Reading", "1b Reading");
        var unreadPlain = await AddNodeAsync(db, libraryId, parent.Id, CatalogNodeKind.Archive, "Ch Unread", "1c Unread");
        var unreadCompleted = await AddNodeAsync(db, libraryId, parent.Id, CatalogNodeKind.Archive, "Ch Completed", "1d Completed");

        var subEmpty = await AddNodeAsync(db, libraryId, parent.Id, CatalogNodeKind.Folder, "Sub Empty", "0a Empty");
        var subRead = await AddNodeAsync(db, libraryId, parent.Id, CatalogNodeKind.Folder, "Sub Read", "0b Read");
        var subReading = await AddNodeAsync(db, libraryId, parent.Id, CatalogNodeKind.Folder, "Sub Reading", "0c Reading");
        var subUnread = await AddNodeAsync(db, libraryId, parent.Id, CatalogNodeKind.Folder, "Sub Unread", "0d Unread");
        var subReadCh = await AddNodeAsync(db, libraryId, subRead.Id, CatalogNodeKind.Archive, "SR Ch", "1SR");
        var subReadingCh = await AddNodeAsync(db, libraryId, subReading.Id, CatalogNodeKind.Archive, "SG Ch", "1SG");
        await AddNodeAsync(db, libraryId, subUnread.Id, CatalogNodeKind.Archive, "SU Ch", "1SU");

        await AddReadMarkAsync(db, userId, readArch.Id);
        await AddProgressAsync(db, userId, readingArch.Id, DateTimeOffset.UtcNow, state: (int)ReadingState.InProgress);
        await AddProgressAsync(db, userId, unreadCompleted.Id, DateTimeOffset.UtcNow, state: (int)ReadingState.Completed);
        await AddReadMarkAsync(db, userId, subReadCh.Id);
        await AddProgressAsync(db, userId, subReadingCh.Id, DateTimeOffset.UtcNow, state: (int)ReadingState.InProgress);

        return new ReadStateFixture(
            parent.Id, readArch.PublicId, readingArch.PublicId, unreadPlain.PublicId, unreadCompleted.PublicId,
            subEmpty.PublicId, subRead.PublicId, subReading.PublicId, subUnread.PublicId);
    }

    [Fact]
    public async Task Browse_ReadStateFilter_Read_KeepsReadArchivesAndFullyReadFolders()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var f = await SeedReadStateFixtureAsync(db, userId, libraryId);
            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: f.ParentId, cursor: null,
                readState: BrowseReadStateFilter.Read);

            var ids = result.Items.Select(n => n.Id).ToHashSet();
            Assert.Contains(f.ReadArchiveId, ids);
            Assert.Contains(f.SubReadId, ids); // folder whose descendants are all read
            // Folders are NOW filtered by rollup: the Reading/Unread/empty subfolders are hidden.
            Assert.DoesNotContain(f.SubReadingId, ids);
            Assert.DoesNotContain(f.SubUnreadId, ids);
            Assert.DoesNotContain(f.SubEmptyId, ids);
            Assert.DoesNotContain(f.ReadingArchiveId, ids);
            Assert.DoesNotContain(f.UnreadPlainId, ids);
            Assert.DoesNotContain(f.UnreadCompletedId, ids);
            // TotalCount reflects the filter (1 read archive + 1 all-read folder), proving
            // it is applied to the base query before counting.
            Assert.Equal(2, result.TotalCount);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_ReadStateFilter_Reading_KeepsInProgressArchivesAndReadingFolders()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var f = await SeedReadStateFixtureAsync(db, userId, libraryId);
            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: f.ParentId, cursor: null,
                readState: BrowseReadStateFilter.Reading);

            var ids = result.Items.Select(n => n.Id).ToHashSet();
            Assert.Contains(f.ReadingArchiveId, ids);
            Assert.Contains(f.SubReadingId, ids);
            Assert.DoesNotContain(f.SubReadId, ids);
            Assert.DoesNotContain(f.SubUnreadId, ids);
            Assert.DoesNotContain(f.SubEmptyId, ids);
            Assert.DoesNotContain(f.ReadArchiveId, ids);
            Assert.DoesNotContain(f.UnreadPlainId, ids);
            Assert.DoesNotContain(f.UnreadCompletedId, ids);
            Assert.Equal(2, result.TotalCount);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_ReadStateFilter_Unread_KeepsUnreadArchivesUnreadFoldersAndEmptyFolders()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var f = await SeedReadStateFixtureAsync(db, userId, libraryId);
            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: f.ParentId, cursor: null,
                readState: BrowseReadStateFilter.Unread);

            var ids = result.Items.Select(n => n.Id).ToHashSet();
            // Unread archives: the plain unread AND the completed (no mark) archive.
            Assert.Contains(f.UnreadPlainId, ids);
            Assert.Contains(f.UnreadCompletedId, ids);
            // Unread folders: the all-unread subfolder AND the empty subfolder (no activity
            // = not "read"; hideEmpty is the separate axis that drops empties).
            Assert.Contains(f.SubUnreadId, ids);
            Assert.Contains(f.SubEmptyId, ids);
            Assert.DoesNotContain(f.ReadArchiveId, ids);
            Assert.DoesNotContain(f.ReadingArchiveId, ids);
            Assert.DoesNotContain(f.SubReadId, ids);
            Assert.DoesNotContain(f.SubReadingId, ids);
            Assert.Equal(4, result.TotalCount);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_ReadStateFilter_All_ReturnsEverything()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var f = await SeedReadStateFixtureAsync(db, userId, libraryId);
            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: f.ParentId, cursor: null,
                readState: BrowseReadStateFilter.All);

            // 4 archives + 4 subfolders, unfiltered.
            Assert.Equal(8, result.TotalCount);
            Assert.Equal(8, result.Items.Count);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_ReadStateFilter_AllFoldersLevel_MeaningfullyHidesFolders()
    {
        // The owner's "filters do not work" report: at a library root that is ALL series
        // folders, the read-state filter used to keep every folder (a no-op). It must now
        // hide folders whose descendant rollup does not match.
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var readSeries = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Read Series", "0a");
            var unreadSeries = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Unread Series", "0b");
            var readCh = await AddNodeAsync(db, libraryId, readSeries.Id, CatalogNodeKind.Archive, "R Ch", "1r");
            await AddNodeAsync(db, libraryId, unreadSeries.Id, CatalogNodeKind.Archive, "U Ch", "1u");
            await AddReadMarkAsync(db, userId, readCh.Id);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var read = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                readState: BrowseReadStateFilter.Read);

            Assert.Equal(1, read.TotalCount);
            Assert.Equal(readSeries.PublicId, read.Items.Single().Id);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_ReadStateFilter_IsPerUser()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var f = await SeedReadStateFixtureAsync(db, userId, libraryId);
            // A second user with no read activity: every archive/folder is Unread for them.
            var other = new UserEntity
            {
                PublicId = OpaqueId.Encode(Random.Shared.NextInt64(10, long.MaxValue)),
                UserName = "other",
                NormalizedUserName = "OTHER",
                IsActive = true,
                IsAdmin = true,
                PasswordHash = "hash",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(other);
            await db.SaveChangesAsync();

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var read = await service.BrowseAsync(other.Id, libraryId, parentId: f.ParentId, cursor: null,
                readState: BrowseReadStateFilter.Read);
            // No read activity for `other`: no archive is read and no folder rolls up to Read.
            Assert.Equal(0, read.TotalCount);
            Assert.Empty(read.Items);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_ReadStateFilter_PagesCorrectlyAcrossBoundary()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // 5 read archives + 3 unread archives at root; the Unread filter must page
            // through exactly the 3 unread ones with no duplicates.
            for (int i = 0; i < 5; i++)
            {
                var a = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, $"Read {i}", $"1r{i}");
                await AddReadMarkAsync(db, userId, a.Id);
            }
            for (int i = 0; i < 3; i++)
                await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, $"Unread {i}", $"1u{i}");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var names = new List<string>();
            string? cursor = null;
            bool hasMore;
            do
            {
                var page = await service.BrowseAsync(userId, libraryId, parentId: null,
                    cursor: cursor, pageSize: 2, readState: BrowseReadStateFilter.Unread);
                names.AddRange(page.Items.Select(n => n.DisplayName));
                cursor = page.NextCursor;
                hasMore = page.HasMore;
            } while (hasMore);

            Assert.Equal(3, names.Count);
            Assert.Equal(3, names.Distinct().Count());
            Assert.All(names, n => Assert.StartsWith("Unread", n));
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Hide-empty filter (1.11.0) and composition with read state ---

    [Fact]
    public async Task Browse_HideEmpty_DropsFoldersWithNoDescendantArchives()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var f = await SeedReadStateFixtureAsync(db, userId, libraryId);
            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: f.ParentId, cursor: null,
                hideEmpty: true);

            var ids = result.Items.Select(n => n.Id).ToHashSet();
            Assert.DoesNotContain(f.SubEmptyId, ids); // the only empty subtree is dropped
            // Every archive and every non-empty folder is kept: 4 archives + 3 folders.
            Assert.Contains(f.SubReadId, ids);
            Assert.Contains(f.SubReadingId, ids);
            Assert.Contains(f.SubUnreadId, ids);
            Assert.Contains(f.ReadArchiveId, ids);
            Assert.Equal(7, result.TotalCount);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_HideEmpty_NestedEmptySubtree_IsAlsoEmpty()
    {
        // A folder whose only descendants are further empty folders has no archive in its
        // subtree, so it is "empty" and must be dropped by hideEmpty.
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var outer = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Outer", "0outer");
            await AddNodeAsync(db, libraryId, outer.Id, CatalogNodeKind.Folder, "Inner", "0inner");
            var withArch = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "WithArch", "0with");
            var deep = await AddNodeAsync(db, libraryId, withArch.Id, CatalogNodeKind.Folder, "Deep", "0deep");
            await AddNodeAsync(db, libraryId, deep.Id, CatalogNodeKind.Archive, "Deep Ch", "1deep");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null, hideEmpty: true);

            var ids = result.Items.Select(n => n.Id).ToHashSet();
            Assert.DoesNotContain(outer.PublicId, ids);      // only empty descendants
            Assert.Contains(withArch.PublicId, ids);         // archive nested two levels down
            Assert.Equal(1, result.TotalCount);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_HideEmpty_ComposesWithReadStateUnread()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var f = await SeedReadStateFixtureAsync(db, userId, libraryId);
            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: f.ParentId, cursor: null,
                readState: BrowseReadStateFilter.Unread, hideEmpty: true);

            var ids = result.Items.Select(n => n.Id).ToHashSet();
            // Unread keeps {unreadPlain, unreadCompleted, SubUnread, SubEmpty}; hideEmpty
            // then removes SubEmpty, leaving 3.
            Assert.Contains(f.UnreadPlainId, ids);
            Assert.Contains(f.UnreadCompletedId, ids);
            Assert.Contains(f.SubUnreadId, ids);
            Assert.DoesNotContain(f.SubEmptyId, ids);
            Assert.Equal(3, result.TotalCount);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_HideEmpty_ComposesWithReadStateRead()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var f = await SeedReadStateFixtureAsync(db, userId, libraryId);
            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: f.ParentId, cursor: null,
                readState: BrowseReadStateFilter.Read, hideEmpty: true);

            // Read already excludes the empty folder (null rollup), so hideEmpty is a no-op
            // here: still {readArchive, SubRead}.
            Assert.Equal(2, result.TotalCount);
            var ids = result.Items.Select(n => n.Id).ToHashSet();
            Assert.Contains(f.ReadArchiveId, ids);
            Assert.Contains(f.SubReadId, ids);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Backward / upward keyset paging (1.11.0, name sort) ---

    [Fact]
    public async Task Browse_Backward_ReturnsPageBeforeCursorInAscendingOrder()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            // Six archives a..f in ascending sort-key order.
            foreach (var c in "abcdef")
                await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, $"Ch {c}", $"1{c}");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            // The page before "1c" (pageSize 2) is [a, b], in ascending display order.
            var page = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                pageSize: 2, before: "1c");

            Assert.Equal(new[] { "Ch a", "Ch b" }, page.Items.Select(n => n.DisplayName).ToArray());
            Assert.False(page.HasPrevious); // nothing before "a"
            Assert.Null(page.PrevCursor);
            Assert.Equal(6, page.TotalCount); // TotalCount is the whole filtered listing
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_Backward_ReportsHasPreviousAndChainsUpward()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            foreach (var c in "abcdef")
                await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, $"Ch {c}", $"1{c}");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            // The page before "1e" is [c, d]; a further page ([a, b]) exists above it.
            var first = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                pageSize: 2, before: "1e");
            Assert.Equal(new[] { "Ch c", "Ch d" }, first.Items.Select(n => n.DisplayName).ToArray());
            Assert.True(first.HasPrevious);
            Assert.Equal("1c", first.PrevCursor);

            // Chain upward using the reported PrevCursor: the next page is [a, b].
            var second = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                pageSize: 2, before: first.PrevCursor);
            Assert.Equal(new[] { "Ch a", "Ch b" }, second.Items.Select(n => n.DisplayName).ToArray());
            Assert.False(second.HasPrevious);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_Forward_FromMidCursor_ReportsBackwardCursor()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            foreach (var c in "abcdef")
                await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, $"Ch {c}", $"1{c}");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            // From the true start there is nothing before the window.
            var start = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null, pageSize: 2);
            Assert.False(start.HasPrevious);
            Assert.Null(start.PrevCursor);
            Assert.Equal("1b", start.NextCursor);

            // A forward page landed at a mid-list cursor (a jump-rail landing) reports a
            // backward cursor so the client can scroll up.
            var mid = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: "1b", pageSize: 2);
            Assert.Equal(new[] { "Ch c", "Ch d" }, mid.Items.Select(n => n.DisplayName).ToArray());
            Assert.True(mid.HasPrevious);
            Assert.Equal("1c", mid.PrevCursor);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_Backward_IgnoredForNonNameSorts()
    {
        // Backward paging is name-sort only; a `before` under another sort is ignored and
        // the normal forward page is returned (no crash, no backward semantics).
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            foreach (var c in "abc")
                await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, $"Ch {c}", $"1{c}");

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var page = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                sort: "recentlyAdded", before: "1c");

            Assert.Equal(3, page.Items.Count);
            Assert.False(page.HasPrevious);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- recentlyUpdated sort (1.12.0): folder ranks by LatestDescendantAddedAt,
    // archive by CreatedAt, interleaved, descending-only, null folders last ---

    /// <summary>
    /// Seeds a top-level folder whose newest descendant archive was created at
    /// <paramref name="newestArchiveAt"/> (a second, older archive is added too so the
    /// folder is non-empty), returning the folder node.
    /// </summary>
    private static async Task<CatalogNodeEntity> AddFolderWithArchiveAsync(
        MangaPixerDbContext db, long libraryId, string name, string sortKey, DateTimeOffset newestArchiveAt)
    {
        var folder = await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, name, sortKey);
        await AddNodeAsync(db, libraryId, folder.Id, CatalogNodeKind.Archive,
            name + " Ch", "1" + name + "Ch", newestArchiveAt);
        return folder;
    }

    [Fact]
    public async Task Browse_RecentlyUpdated_InterleavesByEffectiveRecency_NullFoldersLast()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            var t3 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

            await AddFolderWithArchiveAsync(db, libraryId, "FolderRecent", "0FR", t3);
            await AddFolderWithArchiveAsync(db, libraryId, "FolderOld", "0FO", t1);
            // A loose archive whose CreatedAt sits BETWEEN the two folders' effective times.
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "ArchiveMid", "1AM", t2);
            // An empty folder: null primitive -> sorts last.
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "FolderEmpty", "0FE");

            // Populate the maintained primitive (same computation as the migration backfill).
            await LatestDescendantAddedAtMaintenance.RecomputeAllFoldersAsync(db);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                sort: "recentlyUpdated");

            // Interleaved by effective recency (folder by descendant, archive by own), newest
            // first, with the null-timestamp folder last. The archive falls BETWEEN the folders.
            Assert.Equal(
                new[] { "FolderRecent", "ArchiveMid", "FolderOld", "FolderEmpty" },
                result.Items.Select(n => n.DisplayName).ToArray());
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyUpdated_IgnoresDirection_AlwaysDescending()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            await AddFolderWithArchiveAsync(db, libraryId, "Older", "0OL", t1);
            await AddFolderWithArchiveAsync(db, libraryId, "Newer", "0NE", t2);
            await LatestDescendantAddedAtMaintenance.RecomputeAllFoldersAsync(db);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            // Ascending is requested but recency sorts ignore direction (1.10.4 convention).
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                sort: "recentlyUpdated", direction: SortDirection.Ascending);

            Assert.Equal(new[] { "Newer", "Older" }, result.Items.Select(n => n.DisplayName).ToArray());
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyUpdated_PagesCorrectlyAcrossNullsBoundary()
    {
        var (db, userId, libraryId) = await SetupAsync();
        try
        {
            var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
            // Non-null: three folders + one loose archive with distinct effective times.
            await AddFolderWithArchiveAsync(db, libraryId, "F1", "0F1", t.AddDays(5));
            await AddFolderWithArchiveAsync(db, libraryId, "F2", "0F2", t.AddDays(4));
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Archive, "A1", "1A1", t.AddDays(3));
            await AddFolderWithArchiveAsync(db, libraryId, "F3", "0F3", t.AddDays(2));
            // Null-timestamp folders (no descendant archive), ordered among themselves by SortKey.
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Z1", "0Z1");
            await AddNodeAsync(db, libraryId, null, CatalogNodeKind.Folder, "Z2", "0Z2");
            await LatestDescendantAddedAtMaintenance.RecomputeAllFoldersAsync(db);

            var service = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
            var names = new List<string>();
            string? cursor = null;
            bool hasMore;
            do
            {
                var page = await service.BrowseAsync(userId, libraryId, parentId: null,
                    cursor: cursor, pageSize: 2, sort: "recentlyUpdated");
                names.AddRange(page.Items.Select(n => n.DisplayName));
                cursor = page.NextCursor;
                hasMore = page.HasMore;
            } while (hasMore);

            // All six, no duplicates, effective-recency order with null folders last (by SortKey).
            Assert.Equal(new[] { "F1", "F2", "A1", "F3", "Z1", "Z2" }, names.ToArray());
            Assert.Equal(6, names.Distinct().Count());
        }
        finally { await db.DisposeAsync(); }
    }
}
