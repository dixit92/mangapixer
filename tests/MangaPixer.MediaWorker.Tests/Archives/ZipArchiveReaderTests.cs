namespace com.lifepixer.mangaplex.Tests.MediaWorker.Archives;

using com.lifepixer.mangaplex.Core.Media;
using com.lifepixer.mangaplex.MediaWorker.Archives;
using com.lifepixer.mangaplex.TestSupport;
using com.lifepixer.mangaplex.TestSupport.Fixtures;
using Xunit;

/// <summary>
/// Tests for ZIP archive reading via SharpCompress.
/// Covers ZIP, ZIP64, nested directories, legacy encodings, non-image entries,
/// empty archives, and corrupt/truncated files.
/// </summary>
public sealed class ZipArchiveReaderTests : IDisposable
{
    private readonly string _tempDir;

    public ZipArchiveReaderTests()
    {
        _tempDir = TestSupport.CreateTempTestRoot("p01-zip-reader");
    }

    public void Dispose() => TestSupport.CleanupDirectory(_tempDir);

    [Fact]
    public async Task OpenAndEnumerate_SimpleZip_ReturnsAllEntries()
    {
        var zipPath = ZipFixtureGenerator.CreateZip(_tempDir, "simple.cbz",
            "page001.png", "page002.png", "page003.png");

        using var reader = await ArchiveReader.OpenAsync(zipPath);
        var result = reader.EnumerateEntries();

        Assert.Equal(ArchiveFormat.Zip, result.Format);
        Assert.Equal(3, result.TotalEntries);
        Assert.Equal(3, result.ImageCount);
        Assert.False(result.IsSolid);
        Assert.False(result.IsEncrypted);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task OpenAndEnumerate_NestedDirs_ReturnsAllEntries()
    {
        var zipPath = ZipFixtureGenerator.CreateZipWithNestedDirs(_tempDir, "nested.cbz");

        using var reader = await ArchiveReader.OpenAsync(zipPath);
        var result = reader.EnumerateEntries();

        Assert.Equal(5, result.TotalEntries);
        Assert.Equal(5, result.ImageCount);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task OpenAndEnumerate_NonImageEntries_FiltersCorrectly()
    {
        var zipPath = ZipFixtureGenerator.CreateZipWithNonImageEntries(_tempDir, "mixed.cbz");

        using var reader = await ArchiveReader.OpenAsync(zipPath);
        var result = reader.EnumerateEntries();

        // 2 real image entries + __MACOSX/page001.png (also has .png extension)
        // + ComicInfo.xml + Thumbs.db
        Assert.True(result.TotalEntries >= 5);
        // IsImageCandidate checks extension only, not path filtering.
        // __MACOSX/page001.png has .png extension so it counts as an image candidate.
        // Path-based filtering (ShouldIgnore) is applied at a higher layer.
        Assert.Equal(3, result.ImageCount); // page001.png, page002.png, __MACOSX/page001.png

        // Verify that the ShouldIgnore filter correctly excludes __MACOSX and metadata
        var filteredImages = result.Entries
            .Where(e => e.IsImageCandidate && !ImageExtensions.ShouldIgnore(e.EntryPath))
            .ToList();
        Assert.Equal(2, filteredImages.Count);
    }

    [Fact]
    public async Task OpenAndEnumerate_EmptyZip_ReturnsZeroEntries()
    {
        var zipPath = ZipFixtureGenerator.CreateEmptyZip(_tempDir, "empty.cbz");

        using var reader = await ArchiveReader.OpenAsync(zipPath);
        var result = reader.EnumerateEntries();

        Assert.Equal(0, result.TotalEntries);
        Assert.Equal(0, result.ImageCount);
    }

    [Fact]
    public async Task OpenAndEnumerate_LegacyEncodingZip_ReturnsEntries()
    {
        var zipPath = ZipFixtureGenerator.CreateLegacyEncodingZip(_tempDir, "legacy.cbz");

        using var reader = await ArchiveReader.OpenAsync(zipPath);
        var result = reader.EnumerateEntries();

        Assert.True(result.TotalEntries >= 2);
        Assert.True(result.ImageCount >= 2);
    }

    [Fact]
    public async Task OpenAndEnumerate_TruncatedZip_ReturnsError()
    {
        var zipPath = ZipFixtureGenerator.CreateTruncatedZip(_tempDir, "truncated.cbz");

        // Opening a truncated ZIP may or may not throw depending on where truncation occurred.
        // Enumeration should either return an error or a partial result with an error message.
        try
        {
            using var reader = await ArchiveReader.OpenAsync(zipPath);
            var result = reader.EnumerateEntries();
            // If we get here, the error should be set or entries should be incomplete
            Assert.True(result.Error is not null || result.TotalEntries < 3,
                "Truncated ZIP should either error or return fewer entries");
        }
        catch (InvalidDataException)
        {
            // This is also acceptable — truncated ZIPs may fail to open
            Assert.True(true);
        }
    }

    [Fact]
    public async Task OpenAndEnumerate_Zip64_ReturnsAllEntries()
    {
        var zipPath = ZipFixtureGenerator.CreateZip64(_tempDir, "zip64.cbz", entryCount: 10);

        using var reader = await ArchiveReader.OpenAsync(zipPath);
        var result = reader.EnumerateEntries();

        Assert.Equal(10, result.TotalEntries);
        Assert.Equal(10, result.ImageCount);
    }

    [Fact]
    public async Task ExtractEntry_SimpleZip_ReturnsEntryBytes()
    {
        var zipPath = ZipFixtureGenerator.CreateZip(_tempDir, "extract.cbz", "page001.png");

        using var reader = await ArchiveReader.OpenAsync(zipPath);
        using var stream = await reader.ExtractEntryAsync("page001.png");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        // Should be the minimal PNG we wrote
        Assert.True(bytes.Length > 0);
        Assert.Equal(0x89, bytes[0]); // PNG signature first byte
    }

    [Fact]
    public async Task Open_NonArchiveFile_ThrowsInvalidData()
    {
        var path = Path.Combine(_tempDir, "not-archive.txt");
        File.WriteAllText(path, "not an archive");

        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveReader.OpenAsync(path));
    }
}
