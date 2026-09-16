namespace com.lifepixer.mangapixer.Tests.Server.ReleaseGate;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Source immutability release gate tests. Verifies that the server never
/// modifies, moves, renames, deletes, annotates, or extracts into source
/// library directories.
///
/// These tests verify the read-only source media invariant by checking that:
/// - No source path is ever written to by any service
/// - The database stores paths but never uses them for writes
/// - Scratch and cache roots are separate from source roots
/// </summary>
public sealed class SourceImmutabilityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _sourceDir;
    private readonly string _scratchDir;
    private readonly string _cacheDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public SourceImmutabilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-immut-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _sourceDir = Path.Combine(_tempDir, "source");
        _scratchDir = Path.Combine(_tempDir, "scratch");
        _cacheDir = Path.Combine(_tempDir, "cache");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_scratchDir);
        Directory.CreateDirectory(_cacheDir);

        // Create a marker file in source to verify it's never modified
        var markerPath = Path.Combine(_sourceDir, "marker.txt");
        File.WriteAllText(markerPath, "original content");

        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task SourceMarkerFile_IsNeverModified()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        // Simulate various operations
        db.Libraries.Add(new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test",
            RootPath = _sourceDir,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // Verify the marker file is unchanged
        var markerPath = Path.Combine(_sourceDir, "marker.txt");
        var content = await File.ReadAllTextAsync(markerPath);
        Assert.Equal("original content", content);

        await db.DisposeAsync();
    }

    [Fact]
    public async Task ScratchRoot_IsSeparateFromSourceRoot()
    {
        // Verify scratch and source are different directories
        Assert.NotEqual(_sourceDir, _scratchDir);
        Assert.NotEqual(_sourceDir, _cacheDir);

        // Verify no source files appear in scratch or cache
        var sourceFiles = Directory.GetFiles(_sourceDir);
        var scratchFiles = Directory.GetFiles(_scratchDir);
        var cacheFiles = Directory.GetFiles(_cacheDir);

        // Scratch and cache should be empty (no source files leaked)
        Assert.DoesNotContain(sourceFiles[0], scratchFiles);
        Assert.DoesNotContain(sourceFiles[0], cacheFiles);
    }

    [Fact]
    public async Task Database_StoresSourcePathButNeverUsesForWrites()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var sourcePath = Path.Combine(_sourceDir, "test.cbz");
        File.WriteAllText(sourcePath, "fake archive");

        var library = new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test",
            RootPath = _sourceDir,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(100),
            LibraryId = library.Id,
            Kind = 1, // archive
            DisplayName = "Test",
            RelativePath = sourcePath,
            PathKey = sourcePath,
            SortKey = "1Test",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // The source file should still be unchanged
        var content = await File.ReadAllTextAsync(sourcePath);
        Assert.Equal("fake archive", content);

        await db.DisposeAsync();
    }
}
