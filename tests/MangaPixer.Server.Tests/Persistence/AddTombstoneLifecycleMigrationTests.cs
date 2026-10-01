namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddTombstoneLifecycle migration (1.31.0 step 0): applied to a populated pre-1.31 database it
/// back-fills <c>TombstonedAt</c> of every existing tombstone from the <c>UpdatedAt</c> the tombstoning scan wrote (the same
/// stored encoding), leaves live nodes null, and adds the retention setting as null (= Monthly).
/// </summary>
public sealed class AddTombstoneLifecycleMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddTombstoneLifecycleMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-tombmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    /// <summary>
    /// Inserts one row with the given values; every other NOT NULL column without a default gets a neutral value (0 / ''),
    /// read from the table's schema at the migration under test, so the seed survives unrelated column additions.
    /// </summary>
    private static async Task InsertAsync(MangaPixerDbContext db, string table, IReadOnlyDictionary<string, object?> values)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        var columns = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        await using (var info = conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(1);
                var type = reader.GetString(2);
                var notNull = reader.GetInt32(3) == 1;
                var hasDefault = !reader.IsDBNull(4);
                var isKey = reader.GetInt32(5) > 0;
                if (notNull && !hasDefault && !isKey && !columns.ContainsKey(name))
                    columns[name] = type.Contains("INT", StringComparison.OrdinalIgnoreCase) ? 0L : "";
            }
        }
        await using var cmd = conn.CreateCommand();
        var names = columns.Keys.ToList();
        cmd.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) " +
                          $"VALUES ({string.Join(", ", names.Select((_, i) => $"$p{i}"))})";
        for (var i = 0; i < names.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"$p{i}";
            p.Value = columns[names[i]] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long?> TombstonedAtRawAsync(MangaPixerDbContext db, long id) =>
        await db.Database.SqlQueryRaw<long?>("SELECT TombstonedAt AS Value FROM catalog_nodes WHERE Id = {0}", id).SingleAsync();

    [Fact]
    public async Task Migration_BackfillsTombstonedAt_FromUpdatedAt_ForTombstonesOnly()
    {
        // Stored values as the model writes them (DateTimeOffset-to-binary); the back-fill copies the raw value as is.
        var conv = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter();
        long Raw(DateTimeOffset t) => (long)conv.ConvertToProvider(t)!;
        var created = Raw(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var tombstoneUpdatedAt = Raw(new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero));
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddTombstoneLifecycle", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            await InsertAsync(db, "libraries", new Dictionary<string, object?>
            {
                ["Id"] = 1L,
                ["PublicId"] = "lib1",
                ["DisplayName"] = "Lib",
                ["RootPath"] = "/synthetic/lib",
                ["CaseComparisonPolicy"] = "ordinal",
                ["State"] = "active",
            });
            await InsertAsync(db, "catalog_nodes", new Dictionary<string, object?>
            {
                ["Id"] = 10L,
                ["PublicId"] = "n10",
                ["LibraryId"] = 1L,
                ["Kind"] = 1L,
                ["DisplayName"] = "gone",
                ["RelativePath"] = "gone.cbz",
                ["PathKey"] = "gone.cbz",
                ["SortKey"] = "1gone",
                ["Availability"] = 5L,
                ["CreatedAt"] = created,
                ["UpdatedAt"] = tombstoneUpdatedAt,
            });
            await InsertAsync(db, "catalog_nodes", new Dictionary<string, object?>
            {
                ["Id"] = 11L,
                ["PublicId"] = "n11",
                ["LibraryId"] = 1L,
                ["Kind"] = 1L,
                ["DisplayName"] = "kept",
                ["RelativePath"] = "kept.cbz",
                ["PathKey"] = "kept.cbz",
                ["SortKey"] = "1kept",
                ["Availability"] = 0L,
                ["CreatedAt"] = created,
                ["UpdatedAt"] = tombstoneUpdatedAt,
            });
            // A tombstone that never got an UpdatedAt falls back to CreatedAt.
            await InsertAsync(db, "catalog_nodes", new Dictionary<string, object?>
            {
                ["Id"] = 12L,
                ["PublicId"] = "n12",
                ["LibraryId"] = 1L,
                ["Kind"] = 1L,
                ["DisplayName"] = "old",
                ["RelativePath"] = "old.cbz",
                ["PathKey"] = "old.cbz",
                ["SortKey"] = "1old",
                ["Availability"] = 5L,
                ["CreatedAt"] = created,
                ["UpdatedAt"] = null,
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
            Assert.Equal(tombstoneUpdatedAt, await TombstonedAtRawAsync(db, 10));
            Assert.Null(await TombstonedAtRawAsync(db, 11));
            Assert.Equal(created, await TombstonedAtRawAsync(db, 12));

            // Through the model: the stored value reads back as the same instant as UpdatedAt.
            var gone = await db.CatalogNodes.AsNoTracking().SingleAsync(n => n.Id == 10);
            Assert.Equal(gone.UpdatedAt, gone.TombstonedAt);
        }
    }
}
