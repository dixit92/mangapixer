namespace com.lifepixer.mangapixer.Tests.Server.Hosting;

using System.Net;
using com.lifepixer.mangapixer.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Xunit;

/// <summary>
/// Unit tests for the reverse-proxy trust configuration
/// (<see cref="ForwardedHeadersSetup"/>) — parsing of the comma-separated
/// KnownProxies / KnownNetworks config values and the safe default trust set.
/// No host boot; pure options shaping.
/// </summary>
public sealed class ForwardedHeadersSetupTests
{
    [Fact]
    public void ParseProxies_TrimsAndSkipsInvalid()
    {
        var proxies = ForwardedHeadersSetup.ParseProxies(" 203.0.113.7 , not-an-ip, 2001:db8::1 ,,");

        Assert.Equal(
            new[] { "203.0.113.7", "2001:db8::1" },
            proxies.Select(p => p.ToString()).ToArray());
    }

    [Fact]
    public void ParseProxies_NullOrBlank_ReturnsEmpty()
    {
        Assert.Empty(ForwardedHeadersSetup.ParseProxies(null));
        Assert.Empty(ForwardedHeadersSetup.ParseProxies("   "));
    }

    [Fact]
    public void ParseNetworks_ParsesCidrAndSkipsMalformed()
    {
        var networks = ForwardedHeadersSetup.ParseNetworks("203.0.113.0/24, garbage, 198.51.100.0/24, 10.0.0.0/not");

        Assert.Equal(
            new[] { "203.0.113.0/24", "198.51.100.0/24" },
            networks.Select(n => n.ToString()).ToArray());
    }

    [Fact]
    public void DefaultTrustedNetworks_CoverLoopbackAndRfc1918_ButNotPublic()
    {
        static bool TrustsAddress(string ip) =>
            ForwardedHeadersSetup.DefaultTrustedNetworks.Any(n => n.Contains(IPAddress.Parse(ip)));

        Assert.True(TrustsAddress("127.0.0.1"));    // IPv4 loopback
        Assert.True(TrustsAddress("::1"));           // IPv6 loopback
        Assert.True(TrustsAddress("10.1.2.3"));      // RFC1918
        Assert.True(TrustsAddress("172.16.9.9"));    // RFC1918
        Assert.True(TrustsAddress("192.168.1.50"));  // RFC1918

        Assert.False(TrustsAddress("8.8.8.8"));      // public
        Assert.False(TrustsAddress("172.32.0.1"));   // just outside 172.16/12
        Assert.False(TrustsAddress("203.0.113.9"));  // public (documentation range)
    }

    [Fact]
    public void Configure_ClearsFrameworkDefaults_ThenLayersDefaultsPlusConfig()
    {
        var options = new ForwardedHeadersOptions();
        // The framework ships a non-empty default (loopback network + ::1 proxy);
        // Configure must replace, not union-with-surprise, those internals.
        Assert.NotEmpty(options.KnownIPNetworks);

        ForwardedHeadersSetup.Configure(options, new NetworkOptions
        {
            KnownProxies = "203.0.113.7",
            KnownNetworks = "203.0.113.0/24",
        });

        // Scheme + host + client-IP are all honored from a trusted source.
        Assert.Equal(
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto |
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
            options.ForwardedHeaders);

        // Configured proxy is the ONLY literal proxy; the framework's default
        // ::1 proxy was cleared (loopback is still trusted, but via the network
        // list below, not the proxy list).
        Assert.Equal(new[] { "203.0.113.7" }, options.KnownProxies.Select(p => p.ToString()).ToArray());

        // Networks = safe defaults (loopback + RFC1918) PLUS the configured range.
        var networks = options.KnownIPNetworks.Select(n => n.ToString()).ToArray();
        Assert.Contains("127.0.0.0/8", networks);
        Assert.Contains("10.0.0.0/8", networks);
        Assert.Contains("172.16.0.0/12", networks);
        Assert.Contains("192.168.0.0/16", networks);
        Assert.Contains("203.0.113.0/24", networks);
    }

    [Fact]
    public void Configure_NoConfig_KeepsSafeDefaultsOnly()
    {
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersSetup.Configure(options, new NetworkOptions());

        Assert.Empty(options.KnownProxies);
        var networks = options.KnownIPNetworks.Select(n => n.ToString()).ToArray();
        Assert.Equal(
            new[] { "127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" },
            networks);
    }
}
