namespace com.lifepixer.mangaplex.Tests.Server.Features.Catalog;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Reading;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Features.Catalog;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
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
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public CatalogBrowseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-browse-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "browse.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPlexDbContext db, long userId, long libraryId)> SetupAsync()
    {
        var db = new MangaPlexDbContext(_options);
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
        MangaPlexDbContext db,
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
        MangaPlexDbContext db,
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
    public async Task Browse_RecentlyAdded_Ascending_ReversesOrder()
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
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                sort: "recentlyAdded", direction: SortDirection.Ascending);

            // Full reversal of the descending default: archives (oldest first),
            // then folders (oldest first).
            Assert.Equal(5, result.Items.Count);
            Assert.Equal("Archive Old", result.Items[0].DisplayName);
            Assert.Equal("Archive Mid", result.Items[1].DisplayName);
            Assert.Equal("Archive New", result.Items[2].DisplayName);
            Assert.Equal("Folder Old", result.Items[3].DisplayName);
            Assert.Equal("Folder New", result.Items[4].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyAdded_Ascending_PagesCorrectlyAcrossBoundary()
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

            // Oldest first (reverse of the descending default's Archive 0..6 order).
            Assert.Equal(7, allNames.Count);
            Assert.Equal(7, allNames.Distinct().Count());
            for (int i = 0; i < 7; i++)
                Assert.Equal($"Archive {6 - i}", allNames[i]);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyRead_Ascending_ReversesOrder()
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
            var result = await service.BrowseAsync(userId, libraryId, parentId: null, cursor: null,
                sort: "recentlyRead", direction: SortDirection.Ascending);

            // Full reversal of the descending default: no-activity group first (by
            // reverse SortKey), then activity group oldest-first.
            Assert.Equal(5, result.Items.Count);
            Assert.Equal("Archive Unread B", result.Items[0].DisplayName);
            Assert.Equal("Archive Unread A", result.Items[1].DisplayName);
            Assert.Equal("Folder A", result.Items[2].DisplayName);
            Assert.Equal("Archive Read Old", result.Items[3].DisplayName);
            Assert.Equal("Archive Read New", result.Items[4].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Browse_RecentlyRead_Ascending_PagesCorrectlyAcrossSegments()
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

            // Reverse of the descending-default order (Read2, Read1, Folder, Unread1,
            // Unread2, Unread3): no-activity group first by reverse SortKey, then
            // activity group oldest-first.
            Assert.Equal(6, allNames.Count);
            Assert.Equal(6, allNames.Distinct().Count());
            Assert.Equal("Unread3", allNames[0]);
            Assert.Equal("Unread2", allNames[1]);
            Assert.Equal("Unread1", allNames[2]);
            Assert.Equal("Folder", allNames[3]);
            Assert.Equal("Read1", allNames[4]);
            Assert.Equal("Read2", allNames[5]);
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
    // --- Folder read rollup (1.6.0: derived Read / Reading / Unread over descendants) ---

    private static async Task AddReadMarkAsync(MangaPlexDbContext db, long userId, long itemId)
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
}
