namespace com.lifepixer.mangaplex.Tests.Server.Storage;

using com.lifepixer.mangaplex.Server.Storage;
using Xunit;

/// <summary>
/// Tests that the admin directory browser lists real subdirectories but stays
/// strictly confined to the configured media browse root (no path traversal).
/// </summary>
public sealed class FilesystemBrowseServiceTests : IDisposable
{
    private readonly string _root;

    public FilesystemBrowseServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mangaplex-browse-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "Series A", "Volume 1"));
        Directory.CreateDirectory(Path.Combine(_root, "Series B"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private FilesystemBrowseService Service() =>
        new(new MediaBrowseOptions { Root = _root });

    [Fact]
    public void Browse_Root_ListsTopLevelDirectories()
    {
        var result = Service().Browse(null);

        Assert.True(result.Available);
        Assert.Null(result.Parent); // at the root, cannot go up
        Assert.Collection(result.Entries,
            e => { Assert.Equal("Series A", e.Name); Assert.True(e.HasChildren); },
            e => { Assert.Equal("Series B", e.Name); Assert.False(e.HasChildren); });
    }

    [Fact]
    public void Browse_Subdirectory_ListsChildrenAndExposesParent()
    {
        var seriesA = Path.Combine(_root, "Series A");
        var result = Service().Browse(seriesA);

        Assert.True(result.Available);
        Assert.NotNull(result.Parent);
        Assert.Single(result.Entries);
        Assert.Equal("Volume 1", result.Entries[0].Name);
    }

    [Fact]
    public void Browse_TraversalOutsideRoot_IsClampedToRoot()
    {
        // A relative escape and an absolute escape both resolve back to the root.
        var escapeRelative = Path.Combine(_root, "..", "..", "etc");
        var clampedRel = Service().Browse(escapeRelative);
        Assert.True(clampedRel.Available);
        Assert.Equal(
            _root.TrimEnd(Path.DirectorySeparatorChar),
            clampedRel.Current!.TrimEnd(Path.DirectorySeparatorChar));

        var clampedAbs = Service().Browse(OperatingSystem.IsWindows() ? "C:\\Windows" : "/etc");
        Assert.True(clampedAbs.Available);
        Assert.Equal(
            _root.TrimEnd(Path.DirectorySeparatorChar),
            clampedAbs.Current!.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Browse_NoRootConfigured_ReportsUnavailable()
    {
        var missing = new FilesystemBrowseService(
            new MediaBrowseOptions { Root = Path.Combine(_root, "does-not-exist") });
        var result = missing.Browse(null);

        Assert.False(result.Available);
        Assert.Empty(result.Entries);
    }
}
