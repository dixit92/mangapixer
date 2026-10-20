namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddCatalogCreatedAtIndex migration (1.46.0 lane D, additive): a database at the previous migration
/// (AddLatestUploadMissing) with catalog rows in it gains the <c>(LibraryId, Kind, CreatedAt)</c> index, keeps every row, and the home
/// "New chapters" candidate query (a library's archives added since a cutoff) is answered from that index.
/// </summary>
public sealed class AddCatalogCreatedAtIndexMigrationTests : IDisposable
{
    private const string IndexName = "IX_catalog_nodes_LibraryId_Kind_CreatedAt";

    private readonly string _dir;

    public AddCatalogCreatedAtIndexMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-createdatidx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_AfterAddLatestUploadMissing_AddsTheIndex_KeepsEveryRow_AndTheCandidateQueryUsesIt()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        var now = DateTimeOffset.UtcNow;
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddCatalogCreatedAtIndex", StringComparison.Ordinal));
            Assert.True(index > 0);
            Assert.EndsWith("_AddLatestUploadMissing", all[index - 1], StringComparison.Ordinal);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);
            Assert.False(await IndexExistsAsync(db));

            await LegacySchemaSeed.InsertAsync(db, new LibraryEntity { Id = 1, PublicId = "lib1", DisplayName = "A", RootPath = "/synthetic/a", CreatedAt = now });
            await LegacySchemaSeed.InsertAsync(db, new CatalogNodeEntity
            {
                Id = 10, PublicId = "series", LibraryId = 1, Kind = (int)CatalogNodeKind.Folder, DisplayName = "Series",
                RelativePath = "Series", PathKey = "series", SortKey = "0series", CreatedAt = now.AddDays(-60),
            });
            for (var i = 0; i < 3; i++)
            {
                await LegacySchemaSeed.InsertAsync(db, new CatalogNodeEntity
                {
                    Id = 11 + i, PublicId = $"ch{i}", LibraryId = 1, ParentId = 10, Kind = (int)CatalogNodeKind.Archive,
                    DisplayName = $"Ch {i}.cbz", RelativePath = $"Series/Ch {i}.cbz", PathKey = $"series/ch {i}.cbz", SortKey = $"1ch{i}",
                    CreatedAt = now.AddDays(-10 * i),
                });
            }
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
            Assert.True(await IndexExistsAsync(db));
            Assert.Equal(new[] { "LibraryId", "Kind", "CreatedAt" }, await IndexColumnsAsync(db));
            Assert.Equal(4, await db.CatalogNodes.CountAsync());

            // The candidate query of RecentChaptersService.BuildStacksAsync, as EF writes it, is answered from the new index.
            var cutoff = now.AddDays(-15);
            var candidates = db.CatalogNodes.Where(n => n.LibraryId == 1
                && n.Kind == (int)CatalogNodeKind.Archive
                && n.Availability != (int)CatalogNodeAvailability.Tombstoned
                && n.CreatedAt >= cutoff);
            Assert.Equal(new[] { "ch0", "ch1" }, await candidates.OrderBy(n => n.Id).Select(n => n.PublicId).ToArrayAsync());
            var plan = await QueryPlanAsync(db, candidates.Select(n => n.Id).ToQueryString());
            Assert.Contains(IndexName, plan, StringComparison.Ordinal);
        }
    }

    private static async Task<bool> IndexExistsAsync(MangaPixerDbContext db) =>
        await db.Database.SqlQueryRaw<int>(
            $"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = '{IndexName}'").SingleAsync() == 1;

    private static async Task<string[]> IndexColumnsAsync(MangaPixerDbContext db) =>
        await db.Database.SqlQueryRaw<string>($"SELECT name AS Value FROM pragma_index_info('{IndexName}') ORDER BY seqno").ToArrayAsync();

    private static async Task<string> QueryPlanAsync(MangaPixerDbContext db, string queryString)
    {
        // ToQueryString() declares the parameters in front of the SELECT (".param set" lines); the plan needs only the statement, with
        // the parameters bound - a literal of the same type plans the same way here.
        var lines = queryString.Split('\n');
        var sql = string.Join('\n', lines.Where(l => !l.StartsWith(".param", StringComparison.Ordinal)));
        sql = System.Text.RegularExpressions.Regex.Replace(sql, @"@\w+", "0");
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        await using var command = conn.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync())
            plan.Add(reader.GetString(3));
        return string.Join(" | ", plan);
    }
}
