namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddWikipediaVolumeLists migration (1.32.0 lane C): applied to a populated database at the previous
/// migration it adds <c>wikipedia_lists</c> without touching existing rows (a record and its volume map stay); one row per series record;
/// the row goes with its record (cascade) and the stored volume map is a separate row the migration never wrote.
/// </summary>
public sealed class AddWikipediaVolumeListsMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddWikipediaVolumeListsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-wikimig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_AddsTheTable_KeepsRows_OneRowPerRecord_AndCascadesWithTheRecord()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        var now = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddWikipediaVolumeLists", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            await LegacySchemaSeed.InsertAsync(db, new MetadataRecordEntity
            {
                Id = 7,
                PublicId = "rec7",
                Provider = "mangaupdates",
                ExternalId = "51239621230",
                Title = "Synthetic Saga",
                FetchedAt = now,
            });
            await LegacySchemaSeed.InsertAsync(db, new SeriesVolumeMapEntity
            {
                Id = 3,
                RecordId = 7,
                Source = (int)Core.Metadata.VolumeMapSource.MangaDexAggregate,
                State = 0,
                VolumesJson = "[{\"v\":\"1\",\"c\":[\"1\",\"2\"]}]",
                ContentHash = "seed",
                Version = 1,
                FetchedAt = now,
            });
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
            Assert.Equal(1, await db.MetadataRecords.CountAsync());
            Assert.Equal(1, await db.SeriesVolumeMaps.CountAsync());
            Assert.Equal(0, await db.WikipediaLists.CountAsync());

            db.WikipediaLists.Add(new WikipediaListEntity
            {
                RecordId = 7,
                State = (int)Core.Metadata.WikipediaListState.Found,
                Method = (int)Core.Metadata.WikipediaListMethod.Wikidata,
                PagesJson = "[{\"t\":\"List of Synthetic Saga chapters\",\"r\":100}]",
                DetailsJson = "[{\"v\":\"1\",\"d\":\"2021-01-01\",\"i\":\"9781974725762\"}]",
                CheckedAt = now,
                NextCheckAt = now.AddDays(30),
            });
            await db.SaveChangesAsync();

            // One row per series record.
            db.WikipediaLists.Add(new WikipediaListEntity { RecordId = 7, State = (int)Core.Metadata.WikipediaListState.NotFound });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            // The row goes with its record.
            await db.MetadataRecords.Where(r => r.Id == 7).ExecuteDeleteAsync();
            Assert.Equal(0, await db.WikipediaLists.CountAsync());
        }
    }
}
