namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Features.VolumeScan;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddVolumeContentCompared migration (1.54.0, migration #63, additive): a database at migration #62
/// (AddHomeRowOrder) with a stored volume contents answer keeps it whole, and the new column is null (= the answer does not say what it
/// compared: nothing is ruled out until the next sweep re-matches it). Through the model, a compared list round-trips.
/// </summary>
public sealed class AddVolumeContentComparedMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddVolumeContentComparedMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-volcompared-" + Guid.NewGuid().ToString("N"));
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
    public async Task Migration_AfterMigration62_AddsANullComparedList_AndKeepsTheStoredAnswer()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        long volumeId;
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddVolumeContentCompared", StringComparison.Ordinal));
            Assert.Equal(62, index); // migration #63: the upgrade from #62 (AddHomeRowOrder)
            Assert.EndsWith("_AddHomeRowOrder", all[index - 1], StringComparison.Ordinal);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            // Tables #63 does not touch go through the model; volume_contents through the legacy seed.
            var lib = await VolumeTestData.AddLibraryAsync(db);
            var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Synthetic Series");
            var volume = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Synthetic Series v08");
            volumeId = volume.Id;
            await LegacySchemaSeed.InsertAsync(db, new VolumeContentEntity
            {
                VolumeNodeId = volume.Id,
                Volume = 8,
                VolumeContentVersion = 1,
                FolderId = folder.Id,
                State = (int)VolumeContentRowState.Partial,
                ChaptersJson = """[{"id":5,"cv":1,"n":73,"f":2,"l":19,"m":18,"p":18}]""",
                CandidatesKey = "k1",
                ScannedAt = DateTimeOffset.UnixEpoch,
            });
        }

        await using (var db = NewContext(dbPath))
            await DatabaseInitialization.MigrateToLatestAsync(db, _dir, _ => Task.FromResult(true));

        await using (var db = NewContext(dbPath))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var row = await db.VolumeContents.SingleAsync();
            Assert.Null(row.ComparedJson);
            Assert.Null(VolumeContentReader.ParseCompared(row.ComparedJson));
            Assert.Equal(73m, Assert.Single(VolumeContentReader.ParseChapters(row.ChaptersJson)).Number);
            Assert.Equal(("k1", (int)VolumeContentRowState.Partial), (row.CandidatesKey, row.State));

            row.ComparedJson = VolumeContentReader.SerializeCompared([(9, 2), (5, 1)]);
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
        {
            var row = await db.VolumeContents.SingleAsync(v => v.VolumeNodeId == volumeId);
            Assert.Equal("[[5,1],[9,2]]", row.ComparedJson);
            Assert.Equal([(5L, 1L), (9L, 2L)], VolumeContentReader.ParseCompared(row.ComparedJson));
        }
    }
}
