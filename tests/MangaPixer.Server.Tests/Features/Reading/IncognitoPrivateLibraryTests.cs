namespace com.lifepixer.mangaplex.Tests.Server.Features.Reading;

using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Reading;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Features.Catalog;
using com.lifepixer.mangaplex.Server.Features.Reading;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB integration tests for the 1.4.0 Incognito/Private library
/// feature. Verifies the visible-ids filter across continue-reading, search,
/// browse, and library-list callsites, the DTO projection (LibraryId +
/// LibraryName), the per-library continue query, the prefs CRUD, and
/// non-owner isolation.
/// </summary>
public sealed class IncognitoPrivateLibraryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public IncognitoPrivateLibraryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-incognito-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "incognito.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>
    /// Seeds two libraries, an admin user, an archive node in each library,
    /// reading progress in each, and FTS5 entries for search.
    /// </summary>
    private async Task<(MangaPlexDbContext db, long userId, long libAId, string libAPubId, long libBId, string libBPubId, long nodeAId, long nodeBId)> SetupAsync()
    {
        var db = new MangaPlexDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var libA = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Public Library",
            RootPath = "/private/lib-a",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var libB = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(2),
            DisplayName = "Private Library",
            RootPath = "/private/lib-b",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.AddRange(libA, libB);
        await db.SaveChangesAsync();

        var user = new UserEntity
        {
            PublicId = OpaqueId.Encode(10),
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

        var nodeA = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(101),
            LibraryId = libA.Id,
            ParentId = null,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = "Public Archive",
            RelativePath = "/private/lib-a/archive.cbz",
            PathKey = "/private/lib-a/archive.cbz",
            SortKey = "1Public",
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var nodeB = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(102),
            LibraryId = libB.Id,
            ParentId = null,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = "Private Archive",
            RelativePath = "/private/lib-b/archive.cbz",
            PathKey = "/private/lib-b/archive.cbz",
            SortKey = "1Private",
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.AddRange(nodeA, nodeB);
        await db.SaveChangesAsync();

        db.ArchiveItems.AddRange(
            new ArchiveItemEntity
            {
                NodeId = nodeA.Id,
                ArchiveFormat = 0,
                ByteLength = 1024,
                ModificationTicks = 0,
                ContentVersion = 1,
                AnalysisState = 0,
                PageCount = 10,
            },
            new ArchiveItemEntity
            {
                NodeId = nodeB.Id,
                ArchiveFormat = 0,
                ByteLength = 1024,
                ModificationTicks = 0,
                ContentVersion = 1,
                AnalysisState = 0,
                PageCount = 10,
            });
        await db.SaveChangesAsync();

        // FTS5 triggers on catalog_nodes auto-populate catalog_search — no
        // manual inserts needed (manual inserts would create duplicates).

        return (db, user.Id, libA.Id, libA.PublicId, libB.Id, libB.PublicId, nodeA.Id, nodeB.Id);
    }

    private static async Task SeedProgressAsync(MangaPlexDbContext db, long userId, long nodeAId, long nodeBId)
    {
        db.ReadingProgress.AddRange(
            new ReadingProgressEntity
            {
                UserId = userId,
                ItemId = nodeAId,
                ContentVersion = 1,
                EntryKey = OpaqueId.Encode(5),
                Ordinal = 5,
                State = (int)ReadingState.InProgress,
                Revision = 1,
                LastMutationId = "mut-a",
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            new ReadingProgressEntity
            {
                UserId = userId,
                ItemId = nodeBId,
                ContentVersion = 1,
                EntryKey = OpaqueId.Encode(3),
                Ordinal = 3,
                State = (int)ReadingState.InProgress,
                Revision = 1,
                LastMutationId = "mut-b",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
    }

    // --- GetVisibleLibraryIdsAsync ---

    [Fact]
    public async Task VisibleLibraryIds_NoPrivate_ReturnsAllAccessible()
    {
        var (db, userId, libAId, _, libBId, _, _, _) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);

            var visible = await auth.GetVisibleLibraryIdsAsync(userId, incognito: false);
            Assert.Contains(libAId, visible);
            Assert.Contains(libBId, visible);

            var visibleIncog = await auth.GetVisibleLibraryIdsAsync(userId, incognito: true);
            Assert.Contains(libAId, visibleIncog);
            Assert.Contains(libBId, visibleIncog);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task VisibleLibraryIds_WithPrivate_ExcludesWhenIncognito()
    {
        var (db, userId, libAId, _, libBId, _, _, _) = await SetupAsync();
        try
        {
            db.PrivateLibraries.Add(new PrivateLibraryEntity
            {
                UserId = userId,
                LibraryId = libBId,
                MarkedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var auth = new LibraryAuthorizationService(db);

            // Incognito off → both visible
            var visibleOff = await auth.GetVisibleLibraryIdsAsync(userId, incognito: false);
            Assert.Contains(libAId, visibleOff);
            Assert.Contains(libBId, visibleOff);

            // Incognito on → private lib excluded
            var visibleOn = await auth.GetVisibleLibraryIdsAsync(userId, incognito: true);
            Assert.Contains(libAId, visibleOn);
            Assert.DoesNotContain(libBId, visibleOn);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Continue-reading with incognito ---

    [Fact]
    public async Task ContinueReading_Incognito_ExcludesPrivateLibraryItems()
    {
        var (db, userId, libAId, _, libBId, _, nodeAId, nodeBId) = await SetupAsync();
        try
        {
            await SeedProgressAsync(db, userId, nodeAId, nodeBId);

            db.PrivateLibraries.Add(new PrivateLibraryEntity
            {
                UserId = userId,
                LibraryId = libBId,
                MarkedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var auth = new LibraryAuthorizationService(db);
            var svc = new ReadingStateService(db, auth);

            // Incognito off → both items
            var off = await svc.GetContinueReadingAsync(userId, incognito: false);
            Assert.Equal(2, off.Count);

            // Incognito on → only public library item
            var on = await svc.GetContinueReadingAsync(userId, incognito: true);
            Assert.Single(on);
            Assert.Equal(OpaqueId.Encode(101), on[0].ItemId);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ContinueReading_DtoProjection_HasLibraryIdAndName()
    {
        var (db, userId, _, libAPubId, _, _, nodeAId, nodeBId) = await SetupAsync();
        try
        {
            await SeedProgressAsync(db, userId, nodeAId, nodeBId);

            var auth = new LibraryAuthorizationService(db);
            var svc = new ReadingStateService(db, auth);

            var entries = await svc.GetContinueReadingAsync(userId, incognito: false);
            var entry = Assert.Single(entries, e => e.ItemId == OpaqueId.Encode(101));

            Assert.Equal(libAPubId, entry.LibraryId);
            Assert.Equal("Public Library", entry.LibraryName);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Per-library continue query ---

    [Fact]
    public async Task ContinueReadingByLibrary_ReturnsOnlyThatLibrary()
    {
        var (db, userId, libAId, _, libBId, _, nodeAId, nodeBId) = await SetupAsync();
        try
        {
            await SeedProgressAsync(db, userId, nodeAId, nodeBId);

            var auth = new LibraryAuthorizationService(db);
            var svc = new ReadingStateService(db, auth);

            var aEntries = await svc.GetContinueReadingByLibraryAsync(userId, libAId, incognito: false);
            Assert.Single(aEntries);
            Assert.Equal(OpaqueId.Encode(101), aEntries[0].ItemId);

            var bEntries = await svc.GetContinueReadingByLibraryAsync(userId, libBId, incognito: false);
            Assert.Single(bEntries);
            Assert.Equal(OpaqueId.Encode(102), bEntries[0].ItemId);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ContinueReadingByLibrary_Incognito_EmptyForPrivate()
    {
        var (db, userId, libAId, _, libBId, _, nodeAId, nodeBId) = await SetupAsync();
        try
        {
            await SeedProgressAsync(db, userId, nodeAId, nodeBId);

            db.PrivateLibraries.Add(new PrivateLibraryEntity
            {
                UserId = userId,
                LibraryId = libBId,
                MarkedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var auth = new LibraryAuthorizationService(db);
            var svc = new ReadingStateService(db, auth);

            // Public library still returns items
            var aEntries = await svc.GetContinueReadingByLibraryAsync(userId, libAId, incognito: true);
            Assert.Single(aEntries);

            // Private library returns empty
            var bEntries = await svc.GetContinueReadingByLibraryAsync(userId, libBId, incognito: true);
            Assert.Empty(bEntries);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Browse with incognito ---

    [Fact]
    public async Task Browse_Incognito_EmptyForPrivateLibrary()
    {
        var (db, userId, _, _, libBId, _, _, _) = await SetupAsync();
        try
        {
            db.PrivateLibraries.Add(new PrivateLibraryEntity
            {
                UserId = userId,
                LibraryId = libBId,
                MarkedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var auth = new LibraryAuthorizationService(db);
            var svc = new CatalogBrowseService(db, auth);

            // Incognito off → browse root returns the archive
            var off = await svc.BrowseAsync(userId, libBId, parentId: null, cursor: null, incognito: false);
            Assert.NotEmpty(off.Items);

            // Incognito on → browse root is empty
            var on = await svc.BrowseAsync(userId, libBId, parentId: null, cursor: null, incognito: true);
            Assert.Empty(on.Items);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Search with incognito ---

    [Fact]
    public async Task Search_Incognito_ExcludesPrivateLibraryResults()
    {
        var (db, userId, _, _, libBId, _, _, _) = await SetupAsync();
        try
        {
            db.PrivateLibraries.Add(new PrivateLibraryEntity
            {
                UserId = userId,
                LibraryId = libBId,
                MarkedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var auth = new LibraryAuthorizationService(db);
            var svc = new CatalogBrowseService(db, auth);

            // "Archive" matches both nodes' display names
            var off = await svc.SearchAsync(userId, "Archive", incognito: false);
            Assert.Equal(2, off.TotalCount);

            // Incognito on → only public library result
            var on = await svc.SearchAsync(userId, "Archive", incognito: true);
            Assert.Equal(1, on.TotalCount);
            Assert.Equal(OpaqueId.Encode(101), on.Items[0].Id);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Private library prefs CRUD ---

    [Fact]
    public async Task GetPrivateLibraries_EmptyByDefault()
    {
        var (db, userId, _, _, _, _, _, _) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var svc = new ReadingStateService(db, auth);

            var prefs = await svc.GetPrivateLibrariesAsync(userId);
            Assert.Empty(prefs.LibraryIds);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SetPrivateLibraries_ReplacesEntireSet()
    {
        var (db, userId, _, libAPubId, _, libBPubId, _, _) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var svc = new ReadingStateService(db, auth);

            // Set libB as private
            await svc.SetPrivateLibrariesAsync(userId, [libBPubId]);
            var prefs = await svc.GetPrivateLibrariesAsync(userId);
            Assert.Single(prefs.LibraryIds);
            Assert.Contains(libBPubId, prefs.LibraryIds);

            // Replace with libA
            await svc.SetPrivateLibrariesAsync(userId, [libAPubId]);
            prefs = await svc.GetPrivateLibrariesAsync(userId);
            Assert.Single(prefs.LibraryIds);
            Assert.Contains(libAPubId, prefs.LibraryIds);
            Assert.DoesNotContain(libBPubId, prefs.LibraryIds);

            // Clear entirely
            await svc.SetPrivateLibrariesAsync(userId, []);
            prefs = await svc.GetPrivateLibrariesAsync(userId);
            Assert.Empty(prefs.LibraryIds);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SetPrivateLibraries_SkipsUnknownLibraryIds()
    {
        var (db, userId, _, _, _, libBPubId, _, _) = await SetupAsync();
        try
        {
            var auth = new LibraryAuthorizationService(db);
            var svc = new ReadingStateService(db, auth);

            await svc.SetPrivateLibrariesAsync(userId, [libBPubId, "nonexistent"]);
            var prefs = await svc.GetPrivateLibrariesAsync(userId);
            Assert.Single(prefs.LibraryIds);
            Assert.Contains(libBPubId, prefs.LibraryIds);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Non-owner isolation ---

    [Fact]
    public async Task PrivateLibraries_DoNotAffectOtherUsers()
    {
        var (db, userId, _, _, libBId, libBPubId, _, _) = await SetupAsync();
        try
        {
            // Add a second user
            var user2 = new UserEntity
            {
                PublicId = OpaqueId.Encode(20),
                UserName = "reader",
                NormalizedUserName = "READER",
                IsActive = true,
                IsAdmin = true,
                PasswordHash = "hash",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user2);
            await db.SaveChangesAsync();

            var auth = new LibraryAuthorizationService(db);
            var svc = new ReadingStateService(db, auth);

            // User 1 marks libB as private
            await svc.SetPrivateLibrariesAsync(userId, [libBPubId]);

            // User 2's visible libs should still include libB
            var user2Visible = await auth.GetVisibleLibraryIdsAsync(user2.Id, incognito: true);
            Assert.Contains(libBId, user2Visible);

            // User 1's visible libs should exclude libB
            var user1Visible = await auth.GetVisibleLibraryIdsAsync(userId, incognito: true);
            Assert.DoesNotContain(libBId, user1Visible);
        }
        finally { await db.DisposeAsync(); }
    }
}
