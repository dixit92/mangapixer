namespace com.lifepixer.mangapixer.Tests.Server.Scanning;

using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for the 1.5.0 scan-speed lane: batched reconcile
/// writes, in-place content-change detection, and move/rename detection by
/// content signature. Every scan runs on a <b>fresh</b> DbContext, exactly as
/// the admin scan endpoint does (one DI scope per scan) — the pre-1.5.0
/// content-change path only worked when the same context had created the
/// rows, which the old tests happened to do.
/// </summary>
public sealed class ScanMoveDetectionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _libRoot;
    private readonly DbContextOptions<MangaPixerDbContext> _options;
    private long _libraryId;
    private long _userId;

    public ScanMoveDetectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-move-" + Guid.NewGuid().ToString("N")[..8]);
        _libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(_libRoot);
        var connectionString = DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "test.db"));
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ---- helpers -----------------------------------------------------------

    private MangaPixerDbContext NewContext() => new(_options);

    private async Task SetupAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity { PublicId = "lib1", DisplayName = "Test", RootPath = _libRoot, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        var user = new UserEntity
        {
            PublicId = "u1",
            UserName = "reader",
            NormalizedUserName = "READER",
            PasswordHash = "x",
            SecurityStamp = "s",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _libraryId = library.Id;
        _userId = user.Id;
    }

    private async Task<ScanResult> ScanAsync(long revision)
    {
        await using var db = NewContext();
        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var coordinator = new LibraryScanCoordinator(db, fs, new LibraryScanPolicy(), _libraryId, revision, "test");
        return await coordinator.ScanAsync();
    }

    private string WriteArchive(string relativePath, int length, int seed)
    {
        var path = Path.Combine(_libRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string Rel(params string[] parts) => Path.Combine(parts);

    /// <summary>
    /// Simulates a completed analysis for the archive at <paramref name="relativePath"/>:
    /// ready state, a page manifest, a stored content signature (as the worker
    /// pool persists it), plus per-user reading state keyed to the node id.
    /// </summary>
    private async Task<long> SimulateAnalyzedWithReadingStateAsync(string relativePath, bool withSignature = true)
    {
        await using var db = NewContext();
        var node = await db.CatalogNodes.SingleAsync(n => n.LibraryId == _libraryId && n.PathKey == relativePath);
        var item = await db.ArchiveItems.SingleAsync(a => a.NodeId == node.Id);
        item.AnalysisState = 0;
        item.PageCount = 2;
        item.LastAnalyzedAt = DateTimeOffset.UtcNow;
        item.ThumbnailState = 1;
        item.ThumbnailContentVersion = item.ContentVersion;
        item.ContentSignature = withSignature ? ContentSignature.TryComputeFile(Path.Combine(_libRoot, relativePath)) : null;
        for (var i = 0; i < 2; i++)
        {
            db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = node.Id,
                ContentVersion = item.ContentVersion,
                Ordinal = i,
                EntryKey = $"p{i}",
                SourceEntryLocator = $"page{i}.png",
                MediaType = "image/png",
            });
        }
        db.ReadMarks.Add(new ReadMarkEntity { UserId = _userId, ItemId = node.Id, MarkedAt = DateTimeOffset.UtcNow, Source = "manual" });
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = _userId,
            ItemId = node.Id,
            ContentVersion = item.ContentVersion,
            EntryKey = "p1",
            Ordinal = 1,
            State = 2,
            Revision = 1,
            LastMutationId = "m1",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return node.Id;
    }

    private async Task<int> FtsRowsForAsync(long nodeId, string displayName)
    {
        await using var db = NewContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM catalog_search WHERE node_id = @id AND display_name = @name";
        var p1 = cmd.CreateParameter(); p1.ParameterName = "@id"; p1.Value = nodeId; cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.ParameterName = "@name"; p2.Value = displayName; cmd.Parameters.Add(p2);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    // ---- batching ------------------------------------------------------------

    [Fact]
    public async Task Batched_reconcile_preserves_hierarchy_across_batch_boundaries_and_is_idempotent()
    {
        await SetupAsync();

        // 12 series × 4 volumes × 12 archives = 576 archives + 60 folders = 636
        // nodes: more than one ReconcileBatchSize, so parents and children are
        // split across commits.
        const int series = 12, volumes = 4, files = 12;
        for (var s = 0; s < series; s++)
            for (var v = 0; v < volumes; v++)
                for (var f = 0; f < files; f++)
                    WriteArchive(Rel($"Series {s:D2}", $"Vol {v}", $"ch{f:D2}.cbz"), 64, s * 1000 + v * 100 + f);

        var first = await ScanAsync(1);
        Assert.True(first.Success);
        Assert.Equal(series + series * volumes + series * volumes * files, first.NodesObserved);
        Assert.Equal(first.NodesObserved, first.NodesAdded);
        Assert.True(first.NodesAdded > LibraryScanCoordinator.ReconcileBatchSize);

        await using (var db = NewContext())
        {
            var nodes = await db.CatalogNodes.Where(n => n.LibraryId == _libraryId).ToListAsync();
            var byPath = nodes.ToDictionary(n => n.PathKey, StringComparer.Ordinal);
            Assert.Equal(first.NodesAdded, nodes.Count);

            foreach (var node in nodes)
            {
                var parentPath = Path.GetDirectoryName(node.PathKey) ?? "";
                if (parentPath.Length == 0)
                    Assert.Null(node.ParentId);
                else
                    Assert.Equal(byPath[parentPath].Id, node.ParentId);
            }

            // Every archive has its pending archive item (1:1 insert in the same batch).
            var archiveCount = nodes.Count(n => n.Kind == 1);
            Assert.Equal(archiveCount, await db.ArchiveItems.CountAsync(a => a.Node!.LibraryId == _libraryId && a.AnalysisState == 1));
        }

        // A second scan from a fresh context must be a no-op — in particular no
        // spurious parent "repairs".
        var second = await ScanAsync(2);
        Assert.True(second.Success);
        Assert.Equal(0, second.NodesAdded);
        Assert.Equal(0, second.NodesUpdated);
        Assert.Equal(0, second.NodesMoved);
        Assert.Equal(0, second.NodesTombstoned);
    }

    // ---- content change (fresh context) -----------------------------------------

    [Fact]
    public async Task Content_change_detected_from_fresh_context_bumps_version_and_requeues_analysis()
    {
        await SetupAsync();
        var rel = Rel("Series A", "ch01.cbz");
        var path = WriteArchive(rel, 2000, 1);
        await ScanAsync(1);
        var nodeId = await SimulateAnalyzedWithReadingStateAsync(rel);

        // Rewrite with different bytes and a clearly different mtime.
        File.WriteAllBytes(path, new byte[2500]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        var result = await ScanAsync(2);
        Assert.True(result.Success);
        Assert.Equal(1, result.NodesUpdated);
        Assert.Equal(0, result.NodesMoved);

        await using var db = NewContext();
        var item = await db.ArchiveItems.SingleAsync(a => a.NodeId == nodeId);
        Assert.Equal(2, item.ContentVersion);
        Assert.Equal(2500, item.ByteLength);
        Assert.Equal(1, item.AnalysisState);      // re-queued for analysis
        Assert.Null(item.ContentSignature);       // stale signature cleared
    }

    // ---- move detection ------------------------------------------------------------

    [Fact]
    public async Task Moved_archive_keeps_node_id_manifest_thumbnail_and_reading_state_without_reanalysis()
    {
        await SetupAsync();
        var oldRel = Rel("Series A", "ch01.cbz");
        var oldPath = WriteArchive(oldRel, 300_000, 42);
        await ScanAsync(1);
        var nodeId = await SimulateAnalyzedWithReadingStateAsync(oldRel);

        // Move into a brand-new folder (new parent inserted in the same scan)
        // under a new name, with a copy-style fresh mtime.
        var newRel = Rel("Series A (renamed)", "Chapter 001.cbz");
        var newPath = Path.Combine(_libRoot, newRel);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Move(oldPath, newPath);
        File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow.AddMinutes(1));

        var result = await ScanAsync(2);
        Assert.True(result.Success);
        Assert.Equal(1, result.NodesMoved);
        Assert.Equal(1, result.NodesAdded);       // only the new folder
        Assert.Equal(0, result.NodesTombstoned);

        await using var db = NewContext();
        var node = await db.CatalogNodes.SingleAsync(n => n.Id == nodeId);
        Assert.Equal(newRel, node.PathKey);
        Assert.Equal("Chapter 001.cbz", node.DisplayName);
        Assert.Equal(0, node.Availability);
        var newFolder = await db.CatalogNodes.SingleAsync(n => n.LibraryId == _libraryId && n.Kind == 0 && n.DisplayName == "Series A (renamed)");
        Assert.Equal(newFolder.Id, node.ParentId);
        Assert.False(await db.CatalogNodes.AnyAsync(n => n.LibraryId == _libraryId && n.PathKey == oldRel));
        // The old folder is now empty on disk but still exists → still a node; no archive tombstoned.
        Assert.False(await db.CatalogNodes.AnyAsync(n => n.LibraryId == _libraryId && n.Kind == 1 && n.Availability == 5));

        var item = await db.ArchiveItems.SingleAsync(a => a.NodeId == nodeId);
        Assert.Equal(1, item.ContentVersion);     // not bumped
        Assert.Equal(0, item.AnalysisState);      // NOT re-analysed
        Assert.Equal(2, item.PageCount);
        Assert.Equal(1, item.ThumbnailState);
        Assert.Equal(1, item.ThumbnailContentVersion);
        Assert.Equal(new FileInfo(newPath).LastWriteTimeUtc.Ticks, item.ModificationTicks);
        Assert.NotNull(item.ContentSignature);

        Assert.Equal(2, await db.PageEntries.CountAsync(p => p.ItemId == nodeId && p.ContentVersion == 1));
        Assert.True(await db.ReadMarks.AnyAsync(r => r.UserId == _userId && r.ItemId == nodeId));
        var progress = await db.ReadingProgress.SingleAsync(p => p.UserId == _userId && p.ItemId == nodeId);
        Assert.Equal(2, progress.State);
        Assert.Equal(1, progress.ContentVersion); // still matches the item → not stale

        // Search index follows the rename (the narrowed update trigger still fires
        // for DisplayName/RelativePath changes).
        Assert.Equal(1, await FtsRowsForAsync(nodeId, "Chapter 001.cbz"));
        Assert.Equal(0, await FtsRowsForAsync(nodeId, "ch01.cbz"));

        // And a subsequent scan is quiet.
        var again = await ScanAsync(3);
        Assert.Equal(0, again.NodesAdded + again.NodesUpdated + again.NodesMoved + again.NodesTombstoned);
    }

    [Fact]
    public async Task Same_size_different_content_is_not_matched_as_a_move()
    {
        await SetupAsync();
        var oldRel = Rel("Series A", "ch01.cbz");
        var oldPath = WriteArchive(oldRel, 300_000, 1);
        await ScanAsync(1);
        var oldNodeId = await SimulateAnalyzedWithReadingStateAsync(oldRel);

        File.Delete(oldPath);
        var newRel = Rel("Series B", "other.cbz");
        WriteArchive(newRel, 300_000, 2); // identical size, different bytes

        var result = await ScanAsync(2);
        Assert.True(result.Success);
        Assert.Equal(0, result.NodesMoved);
        Assert.Equal(2, result.NodesAdded);       // folder + archive
        Assert.Equal(1, result.NodesTombstoned);

        await using var db = NewContext();
        var old = await db.CatalogNodes.SingleAsync(n => n.Id == oldNodeId);
        Assert.Equal(5, old.Availability);
        Assert.Equal(oldRel, old.PathKey);
        var created = await db.CatalogNodes.SingleAsync(n => n.LibraryId == _libraryId && n.PathKey == newRel);
        Assert.NotEqual(oldNodeId, created.Id);
        var createdItem = await db.ArchiveItems.SingleAsync(a => a.NodeId == created.Id);
        Assert.Equal(1, createdItem.AnalysisState); // pending analysis, as a new file should be
    }

    [Fact]
    public async Task Legacy_row_without_signature_falls_back_to_tombstone_and_create()
    {
        await SetupAsync();
        var oldRel = Rel("Series A", "ch01.cbz");
        var oldPath = WriteArchive(oldRel, 300_000, 3);
        await ScanAsync(1);
        var oldNodeId = await SimulateAnalyzedWithReadingStateAsync(oldRel, withSignature: false);

        var newPath = Path.Combine(_libRoot, "Series A", "renamed.cbz");
        File.Move(oldPath, newPath);

        var result = await ScanAsync(2);
        Assert.Equal(0, result.NodesMoved);
        Assert.Equal(1, result.NodesAdded);
        Assert.Equal(1, result.NodesTombstoned);

        await using var db = NewContext();
        Assert.Equal(5, (await db.CatalogNodes.SingleAsync(n => n.Id == oldNodeId)).Availability);
    }

    [Fact]
    public async Task Ambiguous_duplicates_are_not_matched_but_a_single_moved_duplicate_is()
    {
        await SetupAsync();
        var relA = Rel("Series A", "a.cbz");
        var relB = Rel("Series A", "b.cbz");
        var pathA = WriteArchive(relA, 200_000, 9);
        var pathB = WriteArchive(relB, 200_000, 9); // byte-identical to a.cbz
        await ScanAsync(1);
        var idA = await SimulateAnalyzedWithReadingStateAsync(relA);
        var idB = await SimulateAnalyzedWithReadingStateAsync(relB);

        // Both identical files move at once → 2 missing rows share one signature
        // with 2 new files: ambiguous → safe fallback.
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series B"));
        File.Move(pathA, Path.Combine(_libRoot, "Series B", "a.cbz"));
        File.Move(pathB, Path.Combine(_libRoot, "Series B", "b.cbz"));

        var result = await ScanAsync(2);
        Assert.Equal(0, result.NodesMoved);
        Assert.Equal(3, result.NodesAdded);       // folder + 2 archives
        Assert.Equal(2, result.NodesTombstoned);

        // Now a clean 1:1 case: analyse one of the new copies, then move only it.
        var relB2 = Rel("Series B", "b.cbz");
        var idB2 = await SimulateAnalyzedWithReadingStateAsync(relB2);
        File.Move(Path.Combine(_libRoot, relB2), Path.Combine(_libRoot, "Series B", "b-final.cbz"));

        var result2 = await ScanAsync(3);
        Assert.Equal(1, result2.NodesMoved);
        Assert.Equal(0, result2.NodesAdded);
        Assert.Equal(0, result2.NodesTombstoned);

        await using var db = NewContext();
        var moved = await db.CatalogNodes.SingleAsync(n => n.Id == idB2);
        Assert.Equal(Rel("Series B", "b-final.cbz"), moved.PathKey);
        // The two tombstones from the ambiguous step stay tombstoned.
        Assert.Equal(5, (await db.CatalogNodes.SingleAsync(n => n.Id == idA)).Availability);
        Assert.Equal(5, (await db.CatalogNodes.SingleAsync(n => n.Id == idB)).Availability);
    }

    [Fact]
    public async Task Move_is_not_matched_when_file_is_still_being_written()
    {
        await SetupAsync();
        var oldRel = Rel("Series A", "ch01.cbz");
        var oldPath = WriteArchive(oldRel, 300_000, 11);
        await ScanAsync(1);
        var oldNodeId = await SimulateAnalyzedWithReadingStateAsync(oldRel);

        // Copy to the new place with the same bytes, but keep the destination open
        // for writing (as an in-progress copy would be). The read-only open with
        // FileShare.Read fails → no signature → no move.
        var newPath = Path.Combine(_libRoot, "Series A", "copy.cbz");
        var bytes = File.ReadAllBytes(oldPath);
        File.Delete(oldPath);
        using (var writer = new FileStream(newPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            writer.Write(bytes);
            writer.Flush();

            var result = await ScanAsync(2);
            Assert.Equal(0, result.NodesMoved);
            Assert.Equal(1, result.NodesAdded);
            Assert.Equal(1, result.NodesTombstoned);
        }

        await using var db = NewContext();
        Assert.Equal(5, (await db.CatalogNodes.SingleAsync(n => n.Id == oldNodeId)).Availability);
    }
}
