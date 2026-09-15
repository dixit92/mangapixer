namespace com.lifepixer.mangapixer.Tests.MediaWorker.Archives;

using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.MediaWorker.Archives;
using com.lifepixer.mangapixer.TestSupport;
using com.lifepixer.mangapixer.TestSupport.Fixtures;
using Xunit;

/// <summary>
/// Tests for 7z archive reading via SharpCompress.
/// Requires the 7z command-line tool for fixture generation.
/// If 7z is not available, signature-only tests run and full-read tests are skipped.
/// </summary>
public sealed class SevenZipArchiveReaderTests : IDisposable
{
    private readonly string _tempDir;

    public SevenZipArchiveReaderTests()
    {
        _tempDir = TestSupport.CreateTempTestRoot("p01-7z-reader");
    }

    public void Dispose() => TestSupport.CleanupDirectory(_tempDir);

    /// <summary>
    /// Checks whether the 7z command-line tool is available in the current environment.
    /// Tests that require real 7z fixtures are skipped when this returns false.
    /// </summary>
    private static bool IsSevenZipAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "7z",
                Arguments = "--help",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0 || p.ExitCode == 1; // 7z returns 1 for --help sometimes
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task OpenAndEnumerate_NonSolidSevenZip_ReturnsAllEntries()
    {
        var archivePath = SevenZipFixtureGenerator.CreateNonSolidSevenZip(_tempDir, "nonsolid.cb7");

        // Check if 7z tool was available (file would be just signature bytes if not)
        var fileInfo = new FileInfo(archivePath);
        if (fileInfo.Length <= 8 || !IsSevenZipAvailable())
        {
            // 7z tool not available — skip this test
            return;
        }

        using var reader = await ArchiveReader.OpenAsync(archivePath);
        var result = reader.EnumerateEntries();

        Assert.Equal(ArchiveFormat.SevenZip, result.Format);
        Assert.True(result.TotalEntries >= 3);
        Assert.True(result.ImageCount >= 3);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task OpenAndEnumerate_SolidSevenZip_ReturnsAllEntries()
    {
        var archivePath = SevenZipFixtureGenerator.CreateSolidSevenZip(_tempDir, "solid.cb7");

        var fileInfo = new FileInfo(archivePath);
        if (fileInfo.Length <= 8 || !IsSevenZipAvailable())
        {
            return;
        }

        using var reader = await ArchiveReader.OpenAsync(archivePath);
        var result = reader.EnumerateEntries();

        Assert.Equal(ArchiveFormat.SevenZip, result.Format);
        Assert.True(result.TotalEntries >= 5);
        Assert.True(result.ImageCount >= 5);
        // 7z archives are always considered solid by SharpCompress
        Assert.True(result.IsSolid);
    }

    [Fact]
    public async Task OpenAndEnumerate_SevenZipNestedDirs_ReturnsAllEntries()
    {
        var archivePath = SevenZipFixtureGenerator.CreateSevenZipWithNestedDirs(_tempDir, "nested.cb7");

        var fileInfo = new FileInfo(archivePath);
        if (fileInfo.Length <= 8 || !IsSevenZipAvailable())
        {
            return;
        }

        using var reader = await ArchiveReader.OpenAsync(archivePath);
        var result = reader.EnumerateEntries();

        Assert.True(result.TotalEntries >= 3);
        Assert.True(result.ImageCount >= 3);
    }

    [Fact]
    public async Task OpenAndEnumerate_TruncatedSevenZip_HandlesError()
    {
        var archivePath = SevenZipFixtureGenerator.CreateTruncatedSevenZip(_tempDir, "truncated.cb7");

        var fileInfo = new FileInfo(archivePath);
        if (fileInfo.Length <= 8 || !IsSevenZipAvailable())
        {
            return;
        }

        try
        {
            using var reader = await ArchiveReader.OpenAsync(archivePath);
            var result = reader.EnumerateEntries();
            // Truncated archive should either error or return partial results
            Assert.True(result.Error is not null || result.TotalEntries < 3,
                "Truncated 7z should either error or return fewer entries");
        }
        catch (InvalidDataException)
        {
            // Acceptable — truncated 7z may fail to open
            Assert.True(true);
        }
    }
}
