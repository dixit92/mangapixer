using com.lifepixer.mangapixer.Tray.Server;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Tray.Server;

public sealed class ServerExecutableLocatorTests : IDisposable
{
    private readonly string _trayDirectory;

    public ServerExecutableLocatorTests()
    {
        _trayDirectory = Path.Combine(Path.GetTempPath(), $"mangapixer-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_trayDirectory);
    }

    [Fact]
    public void Resolve_DistributionLayout_ReturnsServerSubfolderPath()
    {
        var serverDir = Path.Combine(_trayDirectory, "server");
        Directory.CreateDirectory(serverDir);
        var expected = Path.Combine(serverDir, ServerExecutableLocator.ServerExecutableName);
        File.WriteAllText(expected, "");

        Assert.Equal(expected, ServerExecutableLocator.Resolve(_trayDirectory));
    }

    [Fact]
    public void Resolve_SameDirectoryOnly_ReturnsSameDirectoryPath()
    {
        var expected = Path.Combine(_trayDirectory, ServerExecutableLocator.ServerExecutableName);
        File.WriteAllText(expected, "");

        Assert.Equal(expected, ServerExecutableLocator.Resolve(_trayDirectory));
    }

    [Fact]
    public void Resolve_BothPresent_PrefersDistributionLayout()
    {
        var serverDir = Path.Combine(_trayDirectory, "server");
        Directory.CreateDirectory(serverDir);
        var distribution = Path.Combine(serverDir, ServerExecutableLocator.ServerExecutableName);
        File.WriteAllText(distribution, "");
        File.WriteAllText(Path.Combine(_trayDirectory, ServerExecutableLocator.ServerExecutableName), "");

        Assert.Equal(distribution, ServerExecutableLocator.Resolve(_trayDirectory));
    }

    [Fact]
    public void Resolve_NeitherPresent_ReturnsDistributionLayoutPathForErrorReporting()
    {
        var expected = Path.Combine(_trayDirectory, "server", ServerExecutableLocator.ServerExecutableName);

        Assert.Equal(expected, ServerExecutableLocator.Resolve(_trayDirectory));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_trayDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
