namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddJobSchedules migration (1.32.0): the existing settings row follows the publishing pace and keeps
/// every job time unset (the built-in defaults), an existing library keeps scanning "any time", an existing record has no computed
/// cadence yet, and an observation goes with its record.
/// </summary>
public sealed class AddJobSchedulesMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddJobSchedulesMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-jobsmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_KeepsEveryScheduleAsItWas_AndFollowsThePace()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddJobSchedules", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO app_settings (Id, UpdateCheckEnabled, TrashAutomaticHour) VALUES (1, 0, 2);");
            await LegacySchemaSeed.InsertAsync(db, new LibraryEntity { Id = 1, PublicId = "lib1", DisplayName = "A", RootPath = "/synthetic/a", ScanSchedule = "7d", CreatedAt = now });
            await LegacySchemaSeed.InsertAsync(db, new MetadataRecordEntity { Id = 5, PublicId = "rec5", Provider = "mangaupdates", ExternalId = "5", Title = "T", FetchedAt = now });
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
            var row = await db.AppSettings.AsNoTracking().SingleAsync();
            Assert.True(row.MetadataRefreshFollowPace);
            Assert.Null(row.MetadataRefreshHour);
            Assert.Null(row.MetadataRefreshOngoingDays);
            Assert.Null(row.MetadataRefreshFinishedDays);
            Assert.Null(row.BackupHour);
            Assert.Null(row.CacheEvictionHour);
            Assert.Equal(2, row.TrashAutomaticHour);

            var library = await db.Libraries.AsNoTracking().SingleAsync();
            Assert.Equal("7d", library.ScanSchedule);
            Assert.Null(library.ScanHour);
            Assert.Null(library.ScanWeekday);

            Assert.Null((await db.MetadataRecords.AsNoTracking().SingleAsync()).RefreshCadenceDays);
            Assert.Empty(await db.JobRuns.ToListAsync());

            db.MetadataRecordObservations.Add(new MetadataRecordObservationEntity { RecordId = 5, ObservedAt = now, LatestChapter = 12.5, OriginVolumes = 2 });
            db.JobRuns.Add(new JobRunEntity { Key = "cache-eviction", LastStartedAt = now, LastOutcome = "ok" });
            await db.SaveChangesAsync();
            await db.MetadataRecords.ExecuteDeleteAsync();
            Assert.Empty(await db.MetadataRecordObservations.ToListAsync());
            Assert.Equal(now, (await db.JobRuns.AsNoTracking().SingleAsync()).LastStartedAt);
        }
    }
}
