namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddCommunityScores migration (1.54.0, migration #64, additive): a database at migration #63
/// (AddVolumeContentCompared) keeps its metadata records whole, and the six new columns are null (no score read yet - the next fetch or the
/// statistics pass fills them). Through the model, the scores round-trip.
/// </summary>
public sealed class AddCommunityScoresMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddCommunityScoresMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-communityscores-" + Guid.NewGuid().ToString("N"));
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
    public async Task Migration_AfterMigration63_AddsNullScores_AndKeepsEveryRecord()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        var at = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddCommunityScores", StringComparison.Ordinal));
            Assert.Equal(63, index); // migration #64: after #63 (AddVolumeContentCompared)
            Assert.EndsWith("_AddVolumeContentCompared", all[index - 1], StringComparison.Ordinal);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);
            await LegacySchemaSeed.InsertAsync(db, new MetadataRecordEntity
            {
                Id = 5,
                PublicId = "rec5",
                Provider = "mangaupdates",
                ExternalId = "5",
                Title = "T",
                CrossIdsJson = "{\"x\":\"1\"}",
                FetchedAt = at,
            });
            await LegacySchemaSeed.InsertAsync(db, new MetadataRecordEntity
            {
                Id = 6,
                PublicId = "rec6",
                Provider = "mangadex",
                ExternalId = "6",
                Title = "U",
                CrossIdsJson = "{\"mal\":\"2\"}",
                FetchedAt = at,
            });
        }

        await using (var db = NewContext(dbPath))
            await DatabaseInitialization.MigrateToLatestAsync(db, _dir, _ => Task.FromResult(true));

        await using (var db = NewContext(dbPath))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var records = await db.MetadataRecords.OrderBy(r => r.Id).ToListAsync();
            Assert.Equal(["T", "U"], records.Select(r => r.Title));
            Assert.Equal("{\"mal\":\"2\"}", records[1].CrossIdsJson);
            Assert.All(records, r => Assert.Equal((null, null, null, null, null, null),
                (r.CommunityScore, r.CommunityVotes, r.CommunityScoreAt, r.MalScore, r.MalVotes, r.MalScoreAt)));
            (records[0].CommunityScore, records[0].CommunityVotes, records[0].CommunityScoreAt) = (8.98, 1200, at);
            (records[1].MalScore, records[1].MalVotes, records[1].MalScoreAt) = (8.62, 51000, at);
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
        {
            var records = await db.MetadataRecords.OrderBy(r => r.Id).ToListAsync();
            Assert.Equal((8.98, 1200, at), (records[0].CommunityScore!.Value, records[0].CommunityVotes!.Value, records[0].CommunityScoreAt!.Value));
            Assert.Equal((8.62, 51000, at), (records[1].MalScore!.Value, records[1].MalVotes!.Value, records[1].MalScoreAt!.Value));
        }
    }
}
