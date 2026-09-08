namespace com.lifepixer.mangaplex.Server.Tests.Persistence;

using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
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

        // Simulate a 1.0.0 database: EnsureCreated + ConfigureDatabaseAsync (which
        // sets PRAGMA user_version = 2), with a row of real data.
        await using (var db = NewContext(dbPath))
        {
            await db.Database.EnsureCreatedAsync();
            await DatabaseInitialization.ConfigureDatabaseAsync(db);
            db.Users.Add(SampleUser());
            await db.SaveChangesAsync();
        }

        // Migrate: must adopt (stamp the baseline as applied) and NOT recreate the
        // tables — so no backup is taken (nothing pending) and the row survives.
        await using (var db = NewContext(dbPath))
            await DatabaseInitialization.MigrateToLatestAsync(db, _dir, NoBackupExpected);

        await using (var db = NewContext(dbPath))
        {
            var applied = await db.Database.GetAppliedMigrationsAsync();
            Assert.Contains(applied, m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));

            var users = await db.Users.ToListAsync();
            Assert.Single(users);
            Assert.Equal("admin", users[0].UserName);   // data preserved
        }
    }
}
