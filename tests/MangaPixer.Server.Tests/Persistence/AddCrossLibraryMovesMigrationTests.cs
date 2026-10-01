namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddCrossLibraryMoves migration (1.31.0 lane A): applied to a populated database at the
/// previous migration it adds <c>node_moves</c> and <c>move_conflicts</c> without touching existing rows; moves and their
/// conflicts go with the node they started from (cascade), so the trash's purge needs no extra step.
/// </summary>
public sealed class AddCrossLibraryMovesMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddCrossLibraryMovesMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-movesmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_AddsTheMoveTables_KeepsRows_AndCascadesFromTheOldNode()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        var now = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddCrossLibraryMoves", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            await LegacySchemaSeed.InsertAsync(db, new LibraryEntity { Id = 1, PublicId = "lib1", DisplayName = "A", RootPath = "/synthetic/a", CreatedAt = now });
            await LegacySchemaSeed.InsertAsync(db, new LibraryEntity { Id = 2, PublicId = "lib2", DisplayName = "B", RootPath = "/synthetic/b", CreatedAt = now });
            await LegacySchemaSeed.InsertAsync(db, new CatalogNodeEntity
            {
                Id = 10,
                PublicId = "n10",
                LibraryId = 1,
                Kind = 1,
                DisplayName = "v1",
                RelativePath = "v1.cbz",
                PathKey = "v1.cbz",
                SortKey = "1v1",
                Availability = 5,
                CreatedAt = now,
                UpdatedAt = now,
                TombstonedAt = now,
            });
            await LegacySchemaSeed.InsertAsync(db, new CatalogNodeEntity
            {
                Id = 20,
                PublicId = "n20",
                LibraryId = 2,
                Kind = 1,
                DisplayName = "v1",
                RelativePath = "v1.cbz",
                PathKey = "v1.cbz",
                SortKey = "1v1",
                Availability = 0,
                CreatedAt = now,
            });
            await LegacySchemaSeed.InsertAsync(db, new UserEntity { Id = 5, PublicId = "u5", UserName = "u", NormalizedUserName = "U", PasswordHash = "x", SecurityStamp = "s" });
        }

        await using (var db = NewContext(dbPath))
            await DatabaseInitialization.MigrateToLatestAsync(db, _dir, path =>
            {
                File.WriteAllText(path, "snapshot");
                return Task.FromResult(true);
            });

        await using (var db = NewContext(dbPath))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(2, await db.CatalogNodes.CountAsync());
            Assert.Equal(0, await db.NodeMoves.CountAsync());

            var move = new NodeMoveEntity { FromNodeId = 10, ToNodeId = 20, Kind = 1, CreatedAt = now };
            db.NodeMoves.Add(move);
            await db.SaveChangesAsync();
            db.MoveConflicts.Add(new MoveConflictEntity { MoveId = move.Id, UserId = 5, Kind = 1, State = 0, CreatedAt = now });
            await db.SaveChangesAsync();

            // One move per old node.
            db.NodeMoves.Add(new NodeMoveEntity { FromNodeId = 10, ToNodeId = 20, Kind = 1, CreatedAt = now });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            await db.CatalogNodes.Where(n => n.Id == 10).ExecuteDeleteAsync();
            Assert.Equal(0, await db.NodeMoves.CountAsync());
            Assert.Equal(0, await db.MoveConflicts.CountAsync());
        }
    }
}
