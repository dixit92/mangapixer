namespace com.lifepixer.mangaplex.Tests.Tray.Logging;

using com.lifepixer.mangaplex.Tray.Logging;
using Xunit;

public sealed class ServerOutputLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MangaPlexTrayTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string FilePath => Path.Combine(_directory, "server-output.log");

    [Fact]
    public void WriteLine_CreatesTheDirectoryAndAppendsLines()
    {
        var log = new ServerOutputLog(FilePath);
        log.WriteLine("first");
        log.WriteLine("second");
        log.Dispose();

        var lines = File.ReadAllLines(FilePath);
        Assert.Equal(["first", "second"], lines);
    }

    [Fact]
    public void WriteLine_WhenOverTheSizeCap_RollsToADotOneFileAndStartsFresh()
    {
        using var log = new ServerOutputLog(FilePath, maxSizeBytes: 100);

        for (var i = 0; i < 20; i++)
            log.WriteLine(new string('x', 20));

        Assert.True(File.Exists(FilePath + ".1"), "expected a rolled-over .1 file once the cap was exceeded");
        Assert.True(new FileInfo(FilePath).Length < 100 + 21, "the active file should have restarted after rolling");
    }
}
