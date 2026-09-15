namespace com.lifepixer.mangapixer.Tests.Tray.Server;

using System.Net;
using System.Net.Sockets;
using com.lifepixer.mangapixer.Tray.Server;
using Xunit;

/// <summary>Process-level: exercises real loopback socket binding.</summary>
public sealed class TcpPortAvailabilityCheckerTests
{
    [Fact]
    public void IsPortAvailable_WhenPortIsHeldByAnotherListener_ReturnsFalse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var heldPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var checker = new TcpPortAvailabilityChecker();

            Assert.False(checker.IsPortAvailable(heldPort));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void IsPortAvailable_AfterListenerReleasesThePort_ReturnsTrue()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var checker = new TcpPortAvailabilityChecker();

        Assert.True(checker.IsPortAvailable(port));
    }
}
