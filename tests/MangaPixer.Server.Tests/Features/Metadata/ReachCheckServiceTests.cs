namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests for the reach check (1.30.0): an Auto link whose folder goes far past its record drops to Needs review with
/// the record as its one candidate and the "reach" reason; a structure clash only adds the reason; Confirmed links and folders that
/// fit are never touched; the check is idempotent. Synthetic rows only.
/// </summary>
public sealed class ReachCheckServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public ReachCheckServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-reachcheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "reach.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext Db, LibraryEntity Library)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        return (db, await VolumeTestData.AddLibraryAsync(db));
    }

    private static ReachCheckService Service(MangaPixerDbContext db) => new(db, TimeProvider.System, NullLogger<ReachCheckService>.Instance);

    private static async Task<CatalogNodeEntity> SeriesAsync(MangaPixerDbContext db, LibraryEntity lib, string name, long recordId, SeriesLinkState state,
        params string[] archives)
    {
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, name);
        foreach (var a in archives)
            await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, $"{name} {a}");
        await VolumeTestData.LinkAsync(db, folder, recordId, state);
        db.NodeSeriesLinks.Single(l => l.NodeId == folder.Id).MatchScore = 0.97;
        db.MetadataMatchQueue.Add(new MetadataMatchQueueEntity
        {
            NodeId = folder.Id,
            LibraryId = lib.Id,
            State = 2,
            Outcome = (int)MatchBand.Auto,
            EnqueuedAt = DateTimeOffset.UtcNow,
            OutcomeReasons = (int)MatchReason.CoverMatch,
        });
        await db.SaveChangesAsync();
        return folder;
    }

    [Fact]
    public async Task AnAutoLinkFarPastItsRecord_DropsToReview_WithTheRecordAsItsCandidate()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var record = await VolumeTestData.AddRecordAsync(db, "Synthetic Short", originVolumes: 5, status: MetadataOriginStatus.Complete);
        var wrong = await SeriesAsync(db, lib, "Synthetic Long", record.Id, SeriesLinkState.Auto, Enumerable.Range(1, 30).Select(v => $"v{v:00}").ToArray());
        var confirmed = await SeriesAsync(db, lib, "Synthetic Kept", record.Id, SeriesLinkState.Confirmed, Enumerable.Range(1, 30).Select(v => $"v{v:00}").ToArray());
        var fits = await SeriesAsync(db, lib, "Synthetic Fits", record.Id, SeriesLinkState.Auto, "v01", "v02", "v03");
        db.NodeSeriesLinks.Single(l => l.NodeId == wrong.Id).LaterAt = DateTimeOffset.UnixEpoch; // a stale "Later" (1.33.0)
        await db.SaveChangesAsync();

        Assert.Equal(1, await Service(db).CheckRecordAsync(record.Id));

        db.ChangeTracker.Clear();
        var link = await db.NodeSeriesLinks.SingleAsync(l => l.NodeId == wrong.Id);
        Assert.Equal(((int)SeriesLinkState.NeedsReview, (long?)null), (link.State, link.RecordId));
        Assert.Null(link.LaterAt); // back in review as a fresh result, not set aside
        var candidate = await db.MetadataMatchCandidates.SingleAsync(c => c.NodeId == wrong.Id);
        Assert.Equal((record.ExternalId, 1, (int)MatchReason.ReachConflict, 0.97), (candidate.ExternalId, candidate.Rank, candidate.Reasons, candidate.TitleScore));
        var queue = await db.MetadataMatchQueue.SingleAsync(q => q.NodeId == wrong.Id);
        Assert.Equal((int)MatchBand.NeedsReview, queue.Outcome);
        Assert.True(((MatchReason)queue.OutcomeReasons).HasFlag(MatchReason.ReachConflict));
        Assert.True(((MatchReason)queue.OutcomeReasons).HasFlag(MatchReason.CoverMatch)); // earlier reasons are kept

        Assert.Equal(((int)SeriesLinkState.Confirmed, (long?)record.Id), await db.NodeSeriesLinks.Where(l => l.NodeId == confirmed.Id).Select(l => ValueTuple.Create(l.State, l.RecordId)).SingleAsync());
        Assert.Equal((int)SeriesLinkState.Auto, (await db.NodeSeriesLinks.SingleAsync(l => l.NodeId == fits.Id)).State);

        // Idempotent: nothing left to change.
        Assert.Equal(0, await Service(db).CheckRecordAsync(record.Id));
    }

    [Fact]
    public async Task AStructureClash_OnlyAddsTheReason_Once()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var record = await VolumeTestData.AddRecordAsync(db, "Synthetic Listed", originVolumes: 10);
        await VolumeTestData.AddMapAsync(db, record.Id, knownVolumes: 10, volumes: Enumerable.Range(1, 10).Select(v => (v, 10 * v - 9, 10 * v)).ToArray());
        var folder = await SeriesAsync(db, lib, "Synthetic Clash", record.Id, SeriesLinkState.Auto, "v03 c091", "v03 c092", "v03 c093", "v03 c094");

        Assert.Equal(1, await Service(db).CheckRecordAsync(record.Id));
        Assert.Equal(0, await Service(db).CheckRecordAsync(record.Id));

        db.ChangeTracker.Clear();
        var link = await db.NodeSeriesLinks.SingleAsync(l => l.NodeId == folder.Id);
        Assert.Equal(((int)SeriesLinkState.Auto, (long?)record.Id), (link.State, link.RecordId));
        Assert.True(((MatchReason)(await db.MetadataMatchQueue.SingleAsync(q => q.NodeId == folder.Id)).OutcomeReasons).HasFlag(MatchReason.ReachConflict));
        Assert.Empty(await db.MetadataMatchCandidates.Where(c => c.NodeId == folder.Id).ToListAsync());
    }

    [Fact]
    public async Task ARecordWithoutAutoLinks_OrUnknown_ChangesNothing()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var record = await VolumeTestData.AddRecordAsync(db, originVolumes: 2);
        await SeriesAsync(db, lib, "Synthetic Confirmed", record.Id, SeriesLinkState.Confirmed, "v01", "v40");

        Assert.Equal(0, await Service(db).CheckRecordAsync(record.Id));
        Assert.Equal(0, await Service(db).CheckRecordAsync(999_999));
        await Service(db).TryCheckRecordAsync(record.Id, default); // never throws
    }
}
