namespace com.lifepixer.mangapixer.Tests.Server.Scanning;

using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for the content-signature backfill (1.31.1): archives analysed before 1.5.0 have no signature, so their
/// moves were never recognised. The backfill signs live, analysed archives whose file still matches the stored stamp - and a move
/// of such an archive is then recognised by the next scan, keeping the node (and every per-user row keyed by it).
/// </summary>
public sealed class ContentSignatureBackfillTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _libRoot;
    private readonly DbContextOptions<MangaPixerDbContext> _options;
    private long _libraryId;

    public ContentSignatureBackfillTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-sigfill-" + Guid.NewGuid().ToString("N")[..8]);
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
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var library = new LibraryEntity { PublicId = "lib1", DisplayName = "Test", RootPath = _libRoot, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        _libraryId = library.Id;
    }

    private async Task<ScanResult> ScanAsync(long revision)
    {
        await using var db = NewContext();
        return await new LibraryScanCoordinator(db, new ReadOnlyLibraryFileSystem(_libRoot), new LibraryScanPolicy(), _libraryId, revision, "test").ScanAsync();
    }

    private void Write(string relative, int seed, int length = 300_000)
    {
        var path = Path.Combine(_libRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    /// <summary>What an analysis from before 1.5.0 left: ready, but no signature.</summary>
    private async Task MarkAnalysedWithoutSignatureAsync()
    {
        await using var db = NewContext();
        foreach (var item in await db.ArchiveItems.ToListAsync())
        {
            item.AnalysisState = 0;
            item.ContentSignature = null;
        }
        await db.SaveChangesAsync();
    }

    private async Task<SignatureBackfillBatch> BackfillAsync()
    {
        await using var db = NewContext();
        return await new ContentSignatureBackfill(db).RunBatchAsync(0, TimeSpan.Zero, CancellationToken.None);
    }

    private async Task<(long Id, string? Signature, int Availability)> NodeAsync(string pathKey)
    {
        await using var db = NewContext();
        var n = await db.CatalogNodes.AsNoTracking().SingleAsync(x => x.PathKey == pathKey);
        var a = await db.ArchiveItems.AsNoTracking().SingleAsync(x => x.NodeId == n.Id);
        return (n.Id, a.ContentSignature, n.Availability);
    }

    [Fact]
    public async Task Backfill_SignsLegacyArchives_AndAMoveIsThenRecognised()
    {
        await SetupAsync();
        Write(Path.Combine("Old", "a.cbz"), 1);
        Write(Path.Combine("Old", "b.cbz"), 2);
        await ScanAsync(1);
        await MarkAnalysedWithoutSignatureAsync();

        var batch = await BackfillAsync();
        Assert.Equal((2, 2, 0), (batch.Rows, batch.Signed, batch.Skipped));
        var a = await NodeAsync(Path.Combine("Old", "a.cbz"));
        Assert.Equal(ContentSignature.TryComputeFile(Path.Combine(_libRoot, "Old", "a.cbz")), a.Signature);

        // Nothing left: a second batch looks at nothing.
        Assert.Equal(0, (await BackfillAsync()).Rows);

        // Now the folder is renamed on disk: the scan moves the existing node (same id) instead of tombstoning it.
        Directory.Move(Path.Combine(_libRoot, "Old"), Path.Combine(_libRoot, "New"));
        var result = await ScanAsync(2);
        Assert.Equal(2, result.NodesMoved);
        Assert.Equal(1, result.NodesAdded); // only the renamed folder itself is a new node
        var moved = await NodeAsync(Path.Combine("New", "a.cbz"));
        Assert.Equal(a.Id, moved.Id);
    }

    [Fact]
    public async Task Backfill_LeavesAChangedFile_UnanalysedRows_AndTombstones_Alone()
    {
        await SetupAsync();
        Write("changed.cbz", 1);
        Write("pending.cbz", 2);
        Write("gone.cbz", 3);
        Write("kept.cbz", 4);
        await ScanAsync(1);
        await MarkAnalysedWithoutSignatureAsync();
        await using (var db = NewContext())
        {
            var pending = await db.CatalogNodes.SingleAsync(n => n.PathKey == "pending.cbz");
            (await db.ArchiveItems.SingleAsync(x => x.NodeId == pending.Id)).AnalysisState = 1; // waiting for analysis
            await db.SaveChangesAsync();
        }
        File.Delete(Path.Combine(_libRoot, "gone.cbz"));
        await ScanAsync(2); // tombstones gone.cbz
        Write("changed.cbz", 9, length: 300_001); // rewritten after its analysis: the stored stamp is stale

        var batch = await BackfillAsync();

        Assert.Equal((2, 1, 1), (batch.Rows, batch.Signed, batch.Skipped)); // changed (skipped) + kept (signed)
        Assert.NotNull((await NodeAsync("kept.cbz")).Signature);
        Assert.Null((await NodeAsync("changed.cbz")).Signature);
        Assert.Null((await NodeAsync("pending.cbz")).Signature);
        Assert.Null((await NodeAsync("gone.cbz")).Signature);
    }
}
