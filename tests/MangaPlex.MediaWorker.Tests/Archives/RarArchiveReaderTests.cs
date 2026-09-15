namespace com.lifepixer.mangaplex.Tests.MediaWorker.Archives;

using com.lifepixer.mangaplex.Core.Media;
using com.lifepixer.mangaplex.MediaWorker.Archives;
using com.lifepixer.mangaplex.TestSupport;
using com.lifepixer.mangaplex.TestSupport.Fixtures;
using Xunit;

/// <summary>
/// Tests for RAR archive format detection.
/// RAR files cannot be created programmatically (proprietary format).
/// These tests verify signature detection only.
/// Full RAR read tests require pre-built fixtures from a licensed RAR tool
/// or the SharpCompress test suite.
/// </summary>
public sealed class RarArchiveReaderTests : IDisposable
{
    private readonly string _tempDir;

    public RarArchiveReaderTests()
    {
        _tempDir = TestSupport.CreateTempTestRoot("p01-rar-reader");
    }

    public void Dispose() => TestSupport.CleanupDirectory(_tempDir);

    [Fact]
    public async Task Detect_Rar4SignatureFile_DetectsRar4()
    {
        var path = RarFixtureProvider.CreateRar4SignatureFile(_tempDir, "rar4.cbr");
        var format = await ArchiveFormatDetector.DetectFromFileAsync(path);
        Assert.Equal(ArchiveFormat.Rar4, format);
    }

    [Fact]
    public async Task Detect_Rar5SignatureFile_DetectsRar5()
    {
        var path = RarFixtureProvider.CreateRar5SignatureFile(_tempDir, "rar5.cbr");
        var format = await ArchiveFormatDetector.DetectFromFileAsync(path);
        Assert.Equal(ArchiveFormat.Rar5, format);
    }

    [Fact]
    public async Task Open_TruncatedRar4_HandlesError()
    {
        var path = RarFixtureProvider.CreateTruncatedRar4(_tempDir, "truncated.cbr");

        // A truncated RAR should either fail to open or fail to enumerate
        try
        {
            using var reader = await ArchiveReader.OpenAsync(path);
            var result = reader.EnumerateEntries();
            // If enumeration succeeds, it should have an error or zero entries
            Assert.True(result.Error is not null || result.TotalEntries == 0,
                "Truncated RAR should error or return zero entries");
        }
        catch (InvalidDataException)
        {
            // Acceptable — truncated RAR may fail to open
            Assert.True(true);
        }
        catch (SharpCompress.Common.InvalidFormatException)
        {
            // Also acceptable — SharpCompress may reject truncated RAR
            Assert.True(true);
        }
    }

    [Fact]
    public void RarFixtureProvider_RealRarFixturesAvailable_IsFalseByDefault()
    {
        // Real RAR fixtures are not embedded by default.
        // A separate fixture-generation step is needed to enable full RAR read tests.
        Assert.False(RarFixtureProvider.RealRarFixturesAvailable);
    }
}
