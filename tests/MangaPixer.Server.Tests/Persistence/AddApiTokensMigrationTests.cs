namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddApiTokens migration (1.33.0): an existing database gains an empty <c>api_tokens</c> table,
/// keeps its users, and the table's unique secret-hash index and owner cascade are in place.
/// </summary>
public sealed class AddApiTokensMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddApiTokensMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-tokmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_AddsAnEmptyTokenTable_KeepsUsers_AndCascadesWithTheOwner()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddApiTokens", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);
            // The users table as the previous migration left it (columns a later migration adds are skipped).
            await LegacySchemaSeed.InsertAsync(db, new UserEntity
            {
                PublicId = "u1",
                UserName = "alice",
                NormalizedUserName = "ALICE",
                PasswordHash = "hash",
                SecurityStamp = "stamp",
                IsActive = true,
                IsAdmin = true,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
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
            Assert.Empty(await db.ApiTokens.ToListAsync());
            var user = await db.Users.SingleAsync();
            Assert.Equal("alice", user.UserName);

            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
            db.ApiTokens.Add(new ApiTokenEntity
            {
                PublicId = "t1", UserId = user.Id, Name = "MangaList", Prefix = "mpx_abcd", SecretHash = new string('a', 64),
                Scopes = "metadata:read", CreatedAt = DateTimeOffset.UnixEpoch,
            });
            await db.SaveChangesAsync();

            db.ApiTokens.Add(new ApiTokenEntity
            {
                PublicId = "t2", UserId = user.Id, Name = "Copy", Prefix = "mpx_abcd", SecretHash = new string('a', 64),
                Scopes = "metadata:read", CreatedAt = DateTimeOffset.UnixEpoch,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
            Assert.Empty(await db.ApiTokens.ToListAsync());
        }
    }
}
