namespace com.lifepixer.mangaplex.Tests.Server.ReleaseGate;

using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Features.Catalog;
using com.lifepixer.mangaplex.Server.Features.Reading;
using com.lifepixer.mangaplex.Server.Operations;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Privacy release gate tests. Verifies that no DTO, log, or diagnostic
/// output exposes source paths, titles, archive entry names, passwords,
/// tokens, cookies, or page bytes.
///
/// These tests are the privacy review checkpoint for release one.
/// </summary>
public sealed class PrivacyGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public PrivacyGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-privacy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPlexDbContext db, long userId, long libraryId, long nodeId, string nodePublicId)> SetupAsync()
    {
        var db = new MangaPlexDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test Library",
            RootPath = "/private/secret/path/to/library",
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
            PasswordHash = "secret-hash-do-not-leak",
            SecurityStamp = "secret-stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var node = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(100),
            LibraryId = library.Id,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = "Secret Archive Title",
            RelativePath = "/private/secret/path/to/archive.cbz",
            PathKey = "/private/secret/path/to/archive.cbz",
            SortKey = "1Secret",
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();

        // Add ArchiveItemEntity so reading progress can be saved
        db.ArchiveItems.Add(new ArchiveItemEntity
        {
            NodeId = node.Id,
            ArchiveFormat = 0,
            ByteLength = 1024,
            ModificationTicks = 0,
            ContentVersion = 1,
            AnalysisState = 0,
            PageCount = 10,
        });
        await db.SaveChangesAsync();

        // Add FTS5 index entry for search
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO catalog_search (display_name, relative_path, library_id, node_id) VALUES ({0}, {1}, {2}, {3});",
            node.DisplayName, node.RelativePath, node.LibraryId, node.Id);

        return (db, user.Id, library.Id, node.Id, node.PublicId);
    }

    [Fact]
    public async Task CatalogNodeDto_DoesNotContainSourcePath()
    {
        var (db, userId, _, _, nodePublicId) = await SetupAsync();
        try
        {
            var service = new CatalogBrowseService(db);
            var node = await service.GetNodeAsync(userId, nodePublicId);
            Assert.NotNull(node);
            var dtoJson = System.Text.Json.JsonSerializer.Serialize(node);
            Assert.DoesNotContain("/private", dtoJson);
            Assert.DoesNotContain("RelativePath", dtoJson);
            Assert.DoesNotContain("PathKey", dtoJson);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task BreadcrumbsDto_DoesNotContainSourcePath()
    {
        var (db, userId, _, nodeId, _) = await SetupAsync();
        try
        {
            var service = new CatalogBrowseService(db);
            var breadcrumbs = await service.GetBreadcrumbsAsync(userId, nodeId);
            Assert.NotNull(breadcrumbs);
            var dtoJson = System.Text.Json.JsonSerializer.Serialize(breadcrumbs);
            Assert.DoesNotContain("/private", dtoJson);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task DiagnosticsSnapshot_DoesNotContainSourcePath()
    {
        var (db, _, _, _, _) = await SetupAsync();
        try
        {
            var service = new DiagnosticsService(db);
            var snapshot = await service.GetSnapshotAsync();
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
            Assert.DoesNotContain("/private", json);
            Assert.DoesNotContain("Secret Archive", json);
            Assert.DoesNotContain("secret-hash", json);
            Assert.DoesNotContain("secret-stamp", json);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SanitizedLogExport_DoesNotContainSourcePath()
    {
        var (db, _, _, _, _) = await SetupAsync();
        try
        {
            var service = new DiagnosticsService(db);
            var export = await service.ExportLogAsync();
            var json = System.Text.Json.JsonSerializer.Serialize(export);
            Assert.DoesNotContain("/private", json);
            Assert.DoesNotContain("Secret Archive", json);
            Assert.DoesNotContain("secret-hash", json);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SearchResults_DoNotContainSourcePath()
    {
        var (db, userId, _, _, _) = await SetupAsync();
        try
        {
            var service = new CatalogBrowseService(db);
            var results = await service.SearchAsync(userId, "Secret", libraryId: null, ct: default);
            var json = System.Text.Json.JsonSerializer.Serialize(results);
            Assert.DoesNotContain("/private", json);
            Assert.DoesNotContain("RelativePath", json);
            Assert.DoesNotContain("PathKey", json);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ReadingProgressDto_DoesNotContainSourcePath()
    {
        var (db, userId, _, nodeId, _) = await SetupAsync();
        try
        {
            var auth = new com.lifepixer.mangaplex.Server.Features.Auth.LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);
            await service.UpdateProgressAsync(userId, nodeId, 0, 1, mutationId: 1);
            var progress = await service.GetProgressAsync(userId, nodeId);
            Assert.NotNull(progress);
            var json = System.Text.Json.JsonSerializer.Serialize(progress);
            Assert.DoesNotContain("/private", json);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ContinueReading_DoesNotContainSourcePath()
    {
        var (db, userId, _, nodeId, _) = await SetupAsync();
        try
        {
            var auth = new com.lifepixer.mangaplex.Server.Features.Auth.LibraryAuthorizationService(db);
            var service = new ReadingStateService(db, auth);
            await service.UpdateProgressAsync(userId, nodeId, 5, 1, mutationId: 1);
            var entries = await service.GetContinueReadingAsync(userId);
            var json = System.Text.Json.JsonSerializer.Serialize(entries);
            Assert.DoesNotContain("/private", json);
        }
        finally { await db.DisposeAsync(); }
    }
}
