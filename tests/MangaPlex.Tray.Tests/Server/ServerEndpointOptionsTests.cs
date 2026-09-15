namespace com.lifepixer.mangaplex.Tests.Tray.Server;

using com.lifepixer.mangaplex.Tray.Server;
using Xunit;

public sealed class ServerEndpointOptionsTests
{
    [Fact]
    public void Port_DefaultsToServerEndpointOptionsDefaultPort()
    {
        var options = new ServerEndpointOptions();

        Assert.Equal(ServerEndpointOptions.DefaultPort, options.Port);
    }

    [Fact]
    public void BuildAspNetCoreUrls_DefaultsToLoopback()
    {
        var options = new ServerEndpointOptions { Port = 6280 };

        Assert.Equal("http://127.0.0.1:6280", options.BuildAspNetCoreUrls());
    }

    [Fact]
    public void BuildAspNetCoreUrls_WhenLanAllowed_BindsAllInterfaces()
    {
        var options = new ServerEndpointOptions { Port = 6280, AllowLanAccess = true };

        Assert.Equal("http://0.0.0.0:6280", options.BuildAspNetCoreUrls());
    }

    [Fact]
    public void BuildLocalBaseUri_IsAlwaysLoopback_EvenWhenLanAllowed()
    {
        var options = new ServerEndpointOptions { Port = 6280, AllowLanAccess = true };

        Assert.Equal(new Uri("http://127.0.0.1:6280"), options.BuildLocalBaseUri());
    }
}
