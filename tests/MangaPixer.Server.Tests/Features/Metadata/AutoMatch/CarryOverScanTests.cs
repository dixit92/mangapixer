namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Folder-rename carry-over over REAL scans (service-with-DB, synthetic files in a
/// per-test temp directory): the move ledger the scanner records in its existing
/// move branch, the post-scan mapper moving link / precedence / reader default /
/// Content rows onto the renamed folder, the "Missing folders" fallback with manual
/// re-attach and delete, and the reconcile perf guard (no-change rescan: empty
/// ledger; 1,000 moves: a complete ledger within a generous bound).
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class CarryOverScanTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _libRoot;
    private readonly DbContextOptions<MangaPixerDbContext> _options;
    private long _libraryId;
    private long _revision;

    public CarryOverScanTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-carry-" + Guid.NewGuid().ToString("N")[..8]);
        _libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(_libRoot);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "test.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private MangaPixerDbContext NewContext() => new(_options);

    private async Task SetupAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var library = new LibraryEntity { PublicId = "carrylib", DisplayName = "Carry", RootPath = _libRoot, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        _libraryId = library.Id;
    }

    private async Task<ScanResult> ScanAsync()
    {
        await using var db = NewContext();
        var coordinator = new LibraryScanCoordinator(db, new ReadOnlyLibraryFileSystem(_libRoot), new LibraryScanPolicy(), _libraryId, ++_revision, "test");
        return await coordinator.ScanAsync();
    }

    private void WriteArchive(string relativePath, int seed)
    {
        var path = Path.Combine(_libRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[256];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    /// <summary>Stores content signatures as analysis would (moves are recognised by them).</summary>
    private async Task SignAllAsync()
    {
        await using var db = NewContext();
        var archives = await db.CatalogNodes.Where(n => n.LibraryId == _libraryId && n.Kind == 1 && n.Availability != 5).ToListAsync();
        var items = await db.ArchiveItems.ToDictionaryAsync(a => a.NodeId);
        foreach (var node in archives)
        {
            var item = items[node.Id];
            item.AnalysisState = 0;
            item.ContentSignature = ContentSignature.TryComputeFile(Path.Combine(_libRoot, node.RelativePath));
        }
        await db.SaveChangesAsync();
    }

    private async Task<CatalogNodeEntity> FolderAsync(string name)
    {
        await using var db = NewContext();
        return await db.CatalogNodes.AsNoTracking().SingleAsync(n => n.LibraryId == _libraryId && n.Kind == 0 && n.DisplayName == name);
    }

    private MetadataCarryOverService CarryOver(MangaPixerDbContext db) =>
        new(db, new AuditService(db), TimeProvider.System, [], NullLogger<MetadataCarryOverService>.Instance);

    /// <summary>A confirmed link, precedence, reader default and Content on a folder.</summary>
    private async Task DecorateAsync(long folderId)
    {
        await using var db = NewContext();
        var record = new MetadataRecordEntity { PublicId = "rcarry", Provider = "mangaupdates", ExternalId = "4242", Title = "Carried Saga", FetchedAt = DateTimeOffset.UtcNow };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = folderId, LibraryId = _libraryId, State = (int)SeriesLinkState.Confirmed, RecordId = record.Id,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.FolderMetadataPrecedences.Add(new FolderMetadataPrecedenceEntity { NodeId = folderId, Precedence = 1 });
        db.FolderReaderDefaults.Add(new FolderReaderDefaultEntity { NodeId = folderId, ReaderMode = 2 });
        db.FolderMetadataContents.Add(new FolderMetadataContentEntity { NodeId = folderId, Content = 2 });
        await db.SaveChangesAsync();
    }

    private async Task<(bool Link, bool Precedence, bool Reader, bool Content)> RowsOnAsync(long folderId)
    {
        await using var db = NewContext();
        return (await db.NodeSeriesLinks.AnyAsync(l => l.NodeId == folderId),
            await db.FolderMetadataPrecedences.AnyAsync(p => p.NodeId == folderId),
            await db.FolderReaderDefaults.AnyAsync(r => r.NodeId == folderId),
            await db.FolderMetadataContents.AnyAsync(c => c.NodeId == folderId));
    }

    [Fact]
    public async Task RenamedFolder_CarriesLinkPrecedenceReaderDefaultAndContent()
    {
        await SetupAsync();
        WriteArchive(Path.Combine("Old Name", "a01.cbz"), 1);
        WriteArchive(Path.Combine("Old Name", "a02.cbz"), 2);
        await ScanAsync();
        await SignAllAsync();
        var old = await FolderAsync("Old Name");
        await DecorateAsync(old.Id);

        Directory.Move(Path.Combine(_libRoot, "Old Name"), Path.Combine(_libRoot, "New Name"));
        var result = await ScanAsync();
        Assert.Equal(2, result.Moves.Count);
        Assert.All(result.Moves, m => Assert.Equal(old.Id, m.OldParentId));

        await using (var db = NewContext())
            Assert.Equal(1, await CarryOver(db).CarryAsync(_libraryId, result.Moves));

        var renamed = await FolderAsync("New Name");
        Assert.Equal((true, true, true, true), await RowsOnAsync(renamed.Id));
        Assert.Equal((false, false, false, false), await RowsOnAsync(old.Id));
        await using (var db = NewContext())
        {
            Assert.Equal(0, await CarryOver(db).StrandedFolderIds(_libraryId).CountAsync());
            Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == AuditActions.MetadataLinkCarried && a.TargetItemId == renamed.Id));
        }
    }

    [Fact]
    public async Task SplitFolder_IsNotCarried_ShowsAsMissing_AndCanBeReattachedOrDeleted()
    {
        await SetupAsync();
        WriteArchive(Path.Combine("Whole", "a01.cbz"), 11);
        WriteArchive(Path.Combine("Whole", "a02.cbz"), 12);
        await ScanAsync();
        await SignAllAsync();
        var whole = await FolderAsync("Whole");
        await DecorateAsync(whole.Id);

        Directory.CreateDirectory(Path.Combine(_libRoot, "Half One"));
        Directory.CreateDirectory(Path.Combine(_libRoot, "Half Two"));
        File.Move(Path.Combine(_libRoot, "Whole", "a01.cbz"), Path.Combine(_libRoot, "Half One", "a01.cbz"));
        File.Move(Path.Combine(_libRoot, "Whole", "a02.cbz"), Path.Combine(_libRoot, "Half Two", "a02.cbz"));
        Directory.Delete(Path.Combine(_libRoot, "Whole"));
        var result = await ScanAsync();
        Assert.Equal(2, result.Moves.Count);

        await using (var db = NewContext())
        {
            Assert.Equal(0, await CarryOver(db).CarryAsync(_libraryId, result.Moves));
            Assert.Equal([whole.Id], await CarryOver(db).StrandedFolderIds(_libraryId).ToListAsync());
        }

        var half = await FolderAsync("Half One");
        await using (var db = NewContext())
        {
            var (error, reattached) = await CarryOver(db).ReattachAsync(whole.PublicId, half.PublicId, "admin");
            Assert.Null(error);
            Assert.True(reattached!.Link && reattached.Precedence && reattached.ReaderDefault && reattached.Content);
        }
        Assert.Equal((true, true, true, true), await RowsOnAsync(half.Id));

        // A second removed folder, deleted instead of re-attached.
        await DecorateSecondAsync(whole.Id);
        await using (var db = NewContext())
        {
            Assert.True(await CarryOver(db).DeleteMissingAsync(whole.PublicId, "admin"));
            Assert.Empty(await CarryOver(db).StrandedFolderIds(_libraryId).ToListAsync());
        }
    }

    private async Task DecorateSecondAsync(long folderId)
    {
        await using var db = NewContext();
        db.FolderReaderDefaults.Add(new FolderReaderDefaultEntity { NodeId = folderId, ReaderMode = 1 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Reattach_RefusesALiveSourceOrAnotherLibrary()
    {
        await SetupAsync();
        WriteArchive(Path.Combine("Live", "a01.cbz"), 21);
        await ScanAsync();
        var live = await FolderAsync("Live");
        await using var db = NewContext();
        var (error, _) = await CarryOver(db).ReattachAsync(live.PublicId, live.PublicId, "admin");
        Assert.Equal("not_found", error); // only removed folders are "missing"
    }

    [Fact]
    public async Task PerfGuard_NoChangeRescanHasAnEmptyLedger_AndAThousandMovesAreAllRecorded()
    {
        await SetupAsync();
        const int count = 1000;
        for (var i = 0; i < count; i++)
            WriteArchive(Path.Combine("Big Old", $"c{i:D4}.cbz"), 1000 + i);
        await ScanAsync();
        await SignAllAsync();

        var watch = Stopwatch.StartNew();
        var unchanged = await ScanAsync();
        var noChangeMs = watch.ElapsedMilliseconds;
        Assert.Empty(unchanged.Moves);
        Assert.False(unchanged.MoveLedgerTruncated);

        Directory.Move(Path.Combine(_libRoot, "Big Old"), Path.Combine(_libRoot, "Big New"));
        watch.Restart();
        var moved = await ScanAsync();
        var moveMs = watch.ElapsedMilliseconds;
        Assert.Equal(count, moved.NodesMoved);
        Assert.Equal(count, moved.Moves.Count);
        Assert.Equal(count, moved.Moves.Select(m => m.ArchiveNodeId).Distinct().Count());

        // The ledger is one list append per move: a 1,000-move rescan stays within a small
        // multiple of the no-change rescan (the updates themselves dominate), never a
        // per-move query. Generous bounds - shared CI hosts are noisy.
        Assert.True(moveMs < Math.Max(20_000, noChangeMs * 10), $"no-change {noChangeMs} ms, 1k moves {moveMs} ms");

        // The mapper then carries one folder with one query set.
        var old = await FolderAsync("Big Old");
        await DecorateAsync(old.Id);
        watch.Restart();
        await using (var db = NewContext())
            Assert.Equal(1, await CarryOver(db).CarryAsync(_libraryId, moved.Moves));
        Assert.True(watch.ElapsedMilliseconds < 10_000, $"carry-over took {watch.ElapsedMilliseconds} ms");
    }
}
