namespace com.lifepixer.mangaplex.Tests.MediaWorker.Archives;

using com.lifepixer.mangaplex.Core.Media;
using com.lifepixer.mangaplex.MediaWorker.Archives;
using com.lifepixer.mangaplex.TestSupport;
using com.lifepixer.mangaplex.TestSupport.Fixtures;
using Xunit;

/// <summary>
/// Tests for archive format detection from file signatures.
/// Verifies that format is detected by magic bytes, not extension.
/// </summary>
public sealed class ArchiveFormatDetectorTests : IDisposable
{
    private readonly string _tempDir;

    public ArchiveFormatDetectorTests()
    {
        _tempDir = TestSupport.CreateTempTestRoot("p01-format-detect");
    }

    public void Dispose() => TestSupport.CleanupDirectory(_tempDir);

    [Fact]
    public async Task Detect_ZipSignature_ReturnsZip()
    {
        var zipPath = ZipFixtureGenerator.CreateZip(_tempDir, "test.cbz", "page001.png", "page002.png");
        var format = await ArchiveFormatDetector.DetectFromFileAsync(zipPath);
        Assert.Equal(ArchiveFormat.Zip, format);
    }

    [Fact]
    public async Task Detect_EmptyZipSignature_ReturnsZip()
    {
        var zipPath = ZipFixtureGenerator.CreateEmptyZip(_tempDir, "empty.cbz");
        var format = await ArchiveFormatDetector.DetectFromFileAsync(zipPath);
        Assert.Equal(ArchiveFormat.Zip, format);
    }

    [Fact]
    public async Task Detect_SevenZipSignature_ReturnsSevenZip()
    {
        // Create a file with 7z signature
        var path = Path.Combine(_tempDir, "test.cb7");
        File.WriteAllBytes(path, SevenZipFixtureGenerator.SevenZipSignature);
        var format = await ArchiveFormatDetector.DetectFromFileAsync(path);
        Assert.Equal(ArchiveFormat.SevenZip, format);
    }

    [Fact]
    public async Task Detect_Rar4Signature_ReturnsRar4()
    {
        var path = RarFixtureProvider.CreateRar4SignatureFile(_tempDir, "test.cbr");
        var format = await ArchiveFormatDetector.DetectFromFileAsync(path);
        Assert.Equal(ArchiveFormat.Rar4, format);
    }

    [Fact]
    public async Task Detect_Rar5Signature_ReturnsRar5()
    {
        var path = RarFixtureProvider.CreateRar5SignatureFile(_tempDir, "test.cbr");
        var format = await ArchiveFormatDetector.DetectFromFileAsync(path);
        Assert.Equal(ArchiveFormat.Rar5, format);
    }

    [Fact]
    public async Task Detect_NonArchiveFile_ReturnsUnknown()
    {
        var path = Path.Combine(_tempDir, "not-an-archive.txt");
        File.WriteAllText(path, "This is not an archive");
        var format = await ArchiveFormatDetector.DetectFromFileAsync(path);
        Assert.Equal(ArchiveFormat.Unknown, format);
    }

    [Fact]
    public async Task Detect_EmptyFile_ReturnsUnknown()
    {
        var path = Path.Combine(_tempDir, "empty.bin");
        File.WriteAllBytes(path, []);
        var format = await ArchiveFormatDetector.DetectFromFileAsync(path);
        Assert.Equal(ArchiveFormat.Unknown, format);
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip, ".cbz")]
    [InlineData(ArchiveFormat.Rar4, ".cbr")]
    [InlineData(ArchiveFormat.Rar5, ".cbr")]
    [InlineData(ArchiveFormat.SevenZip, ".cb7")]
    public void GetExtension_ReturnsExpectedExtension(ArchiveFormat format, string expected)
    {
        Assert.Equal(expected, ArchiveFormatDetector.GetExtension(format));
    }
}
