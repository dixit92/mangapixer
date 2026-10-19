namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddLatestUploadMissing migration (1.45.0 owner follow-up): a database that already applied
/// AddMangaDexLatestUpload (an instance that ran a 1.45.0 build with the latest-upload sweep) keeps every stored latest upload, and the
/// new miss columns read as "returned" (MissingSince null, MissingCount 0); the model reads and writes them.
/// </summary>
public sealed class AddLatestUploadMissingMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddLatestUploadMissingMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-latestmissing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_AfterAnAppliedAddMangaDexLatestUpload_AddsTheMissColumns_AndKeepsEveryRow()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddLatestUploadMissing", StringComparison.Ordinal));
            Assert.True(index > 0);
            Assert.EndsWith("_AddMangaDexLatestUpload", all[index - 1], StringComparison.Ordinal);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            // Seeded as SQL in the old shape (the record row is not what this test is about).
            await db.Database.ExecuteSqlRawAsync(
                "PRAGMA foreign_keys = OFF; " +
                "INSERT INTO mangadex_latest_uploads (RecordId, ChapterId, SeenAt, CheckedAt) " +
                "VALUES (42, '10000000-0000-4000-8000-0000000000a1', 0, 0)");
        }

        await using (var db = NewContext(dbPath))
        {
            await DatabaseInitialization.MigrateToLatestAsync(db, _dir, path =>
            {
                File.WriteAllText(path, "snapshot");
                return Task.FromResult(true);
            });
        }

        await using (var db = NewContext(dbPath))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var row = await db.MangaDexLatestUploads.SingleAsync();
            Assert.Equal((42L, "10000000-0000-4000-8000-0000000000a1"), (row.RecordId, row.ChapterId));
            Assert.Null(row.MissingSince);
            Assert.Equal(0, row.MissingCount);

            row.MissingSince = DateTimeOffset.UnixEpoch.AddDays(1);
            row.MissingCount = 2;
            // One open connection, so the pragma covers the update (the record row is not what this test is about).
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
        {
            var row = await db.MangaDexLatestUploads.SingleAsync();
            Assert.Equal((DateTimeOffset.UnixEpoch.AddDays(1), 2), (row.MissingSince, row.MissingCount));
        }
    }
}
