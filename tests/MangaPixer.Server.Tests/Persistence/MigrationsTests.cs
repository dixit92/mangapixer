namespace com.lifepixer.mangaplex.Server.Tests.Persistence;

using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Guards the DB migration process (2026-09-08). Covers the three risks called out
/// in the migration plan: (1) the model drifting from the migrations, (2) a fresh
/// install migrating from empty, and (3) adopting an EnsureCreated-era (1.0.0)
/// database into the migration timeline without recreating tables or losing data.
/// </summary>
public sealed class MigrationsTests : IDisposable
{
    private readonly string _dir;

    public MigrationsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangaplex-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPlexDbContext NewContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .ConfigureSqlite(dbPath)
            .Options;
        return new MangaPlexDbContext(options);
    }

    private static Task<bool> NoBackupExpected(string _)
        => throw new Xunit.Sdk.XunitException("A pre-migration backup was requested but none was expected in this test.");

    private static UserEntity SampleUser() => new()
    {
        PublicId = "u_pub_1",
        UserName = "admin",
        NormalizedUserName = "ADMIN",
        PasswordHash = "hash",
        SecurityStamp = "stamp",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Model_matches_the_latest_migration_no_pending_changes()
    {
        using var db = NewContext(Path.Combine(_dir, "model.db"));
        Assert.False(
            db.Database.HasPendingModelChanges(),
            "The EF model has changes not captured by a migration. Run 'dotnet ef migrations add <Name>'.");
    }

    [Fact]
    public async Task Fresh_install_migrates_from_empty()
    {
        var dbPath = Path.Combine(_dir, "fresh.db");

        // No backup expected: a fresh install has no data to protect.
        await using (var db = NewContext(dbPath))
            await DatabaseInitialization.MigrateToLatestAsync(db, _dir, NoBackupExpected);

        await using (var db = NewContext(dbPath))
        {
            var applied = await db.Database.GetAppliedMigrationsAsync();
            Assert.Contains(applied, m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
            Assert.Empty(await db.Users.ToListAsync());   // schema usable
        }
    }

    [Fact]
    public async Task Adopts_ensurecreated_database_without_data_loss()
    {
        var dbPath = Path.Combine(_dir, "legacy.db");
        var backupCalls = 0;
        Task<bool> RecordBackup(string _) { backupCalls++; return Task.FromResult(true); }

        // Simulate a 1.0.0 EnsureCreated database faithfully: build ONLY the baseline
        // (InitialCreate) schema — NOT the current model, which has since advanced —
        // then drop the migrations-history table (EnsureCreated never wrote one), set
        // PRAGMA user_version = 2, and add a row of real data.
        await using (var db = NewContext(dbPath))
        {
            var baseline = db.Database.GetMigrations().First();
            await db.GetService<IMigrator>().MigrateAsync(baseline);
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"__EFMigrationsHistory\";");
            await DatabaseInitialization.SetSchemaVersionAsync(db, 2);
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO users (PublicId, UserName, NormalizedUserName, PasswordHash, SecurityStamp,
                    IsActive, IsAdmin, ForcePasswordChange, AccessFailedCount, LockoutEnabled, CreatedAt)
                  VALUES ('u_pub_1','admin','ADMIN','hash','stamp', 1, 0, 0, 0, 1, 0)");
        }

        // Migrate: adopt (stamp the baseline as applied, no data loss), then apply the
        // pending post-baseline migration(s). Data is present + migrations pending, so
        // a pre-migration backup IS expected.
        await using (var db = NewContext(dbPath))
            await DatabaseInitialization.MigrateToLatestAsync(db, _dir, RecordBackup);

        await using (var db = NewContext(dbPath))
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.Contains(applied, m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
            Assert.True(applied.Count >= 2, "baseline + at least the post-baseline migration should be applied");

            var users = await db.Users.ToListAsync();
            Assert.Single(users);
            Assert.Equal("admin", users[0].UserName);   // data preserved across the migrate
        }

        Assert.True(backupCalls >= 1, "a pre-migration backup should be taken when data is present and migrations are pending");
    }
}
