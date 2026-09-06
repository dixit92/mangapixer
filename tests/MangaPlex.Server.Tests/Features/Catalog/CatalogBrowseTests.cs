namespace com.lifepixer.mangaplex.Tests.Server.Features.Catalog;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
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
        string sortKey)
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
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
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

            var service = new CatalogBrowseService(db);
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

            var service = new CatalogBrowseService(db);
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

            var service = new CatalogBrowseService(db);
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

            var service = new CatalogBrowseService(db);
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

            var service = new CatalogBrowseService(db);
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

            var service = new CatalogBrowseService(db);
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

            var service = new CatalogBrowseService(db);

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

            var service = new CatalogBrowseService(db);
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

            var service = new CatalogBrowseService(db);
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
            var service = new CatalogBrowseService(db);
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

            var service = new CatalogBrowseService(db);
            var result = await service.SearchAsync(reader.Id, "test");

            Assert.Empty(result.Items);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }
}
