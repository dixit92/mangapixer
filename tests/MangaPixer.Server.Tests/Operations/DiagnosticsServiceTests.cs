namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Tests for DiagnosticsService — snapshot counts and sanitized log export.
/// </summary>
public sealed class DiagnosticsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public DiagnosticsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-diag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext db, DiagnosticsService service)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        db.Libraries.Add(new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test",
            RootPath = "/private/test",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Users.Add(new UserEntity
        {
            PublicId = OpaqueId.Encode(2),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new DiagnosticsService(db);
        return (db, service);
    }

    [Fact]
    public async Task GetSnapshot_ReturnsCorrectCounts()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var snapshot = await service.GetSnapshotAsync();

            Assert.Equal(1, snapshot.Database.LibraryCount);
            Assert.Equal(1, snapshot.Database.UserCount);
            Assert.Equal(1, snapshot.Database.ActiveUserCount);
            Assert.Equal(1, snapshot.Database.AdminCount);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task GetSnapshot_ProcessInfoIsValid()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var snapshot = await service.GetSnapshotAsync();

            Assert.True(snapshot.Process.ProcessId > 0);
            Assert.True(snapshot.Process.WorkingSetBytes > 0);
            Assert.True(snapshot.Process.UptimeSeconds >= 0);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ExportLog_ReturnsSanitizedExport()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var export = await service.ExportLogAsync();

            Assert.NotNull(export.Snapshot);
            Assert.NotEmpty(export.EventCategories);
            // Verify no paths in the export
            Assert.DoesNotContain("/private", export.Snapshot.ToString());
        }
        finally { await db.DisposeAsync(); }
    }
}
