namespace com.lifepixer.mangapixer.Server.Hosting;

using System.Net;
using Microsoft.AspNetCore.Builder;

/// <summary>
/// Reverse-proxy trust configuration, bound from the
/// <c>MangaPixer:Network</c> configuration section (env vars
/// <c>MangaPixer__Network__KnownProxies</c> / <c>MangaPixer__Network__KnownNetworks</c>).
/// Both values are comma-separated: <see cref="KnownProxies"/> is a list of
/// literal proxy IP addresses (e.g. <c>"203.0.113.7, 203.0.113.8"</c>) and
/// <see cref="KnownNetworks"/> is a list of CIDR ranges
/// (e.g. <c>"203.0.113.0/24, 2001:db8::/32"</c>).
/// </summary>
/// <remarks>
/// These EXTEND the safe defaults (loopback + RFC1918 private ranges — see
/// <see cref="ForwardedHeadersSetup.DefaultTrustedNetworks"/>); they do not
/// replace them. An operator only needs to configure these when the
/// TLS-terminating reverse proxy connects from a source address OUTSIDE the
/// private/loopback ranges (e.g. a proxy on a public IP). Leaving them unset
/// keeps the default posture where a spoofed <c>X-Forwarded-*</c> header from
/// a public source IP is ignored.
/// </remarks>
public sealed class NetworkOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "MangaPixer:Network";

    /// <summary>Comma-separated literal proxy IP addresses to trust, or null.</summary>
    public string? KnownProxies { get; set; }

    /// <summary>Comma-separated CIDR ranges (<c>address/prefix</c>) to trust, or null.</summary>
    public string? KnownNetworks { get; set; }
}

/// <summary>
/// Builds the <see cref="ForwardedHeadersOptions"/> for the app so it behaves
/// correctly behind a TLS-terminating reverse proxy, with a trust model that
/// is safe by default.
/// </summary>
/// <remarks>
/// <para>
/// The framework's <see cref="ForwardedHeadersMiddleware"/> only rewrites the
/// request's scheme/host/remote-IP from <c>X-Forwarded-*</c> headers when the
/// immediate peer (<c>Connection.RemoteIpAddress</c>) is a KNOWN proxy or
/// falls inside a KNOWN network. Its built-in default trusts only IPv4/IPv6
/// loopback, which is too narrow for the common self-hosted layout (a reverse
/// proxy container on a Docker bridge / LAN, i.e. an RFC1918 address). We
/// therefore widen the default to loopback + RFC1918 private ranges, and let
/// an operator add more via <see cref="NetworkOptions"/>.
/// </para>
/// <para>
/// Crucially this stays SAFE for an internet-exposed instance: a request that
/// arrives directly from a public source IP (no trusted proxy in front) has a
/// public <c>RemoteIpAddress</c> that matches none of the trusted networks, so
/// its <c>X-Forwarded-Proto: https</c> / <c>X-Forwarded-For</c> headers are
/// ignored rather than blindly honored. Spoofing the effective scheme (and
/// thereby tricking cookies into being emitted <c>Secure</c> over plain HTTP,
/// or forging the client IP) requires already being on loopback/the private
/// LAN or an explicitly configured proxy.
/// </para>
/// <para>
/// <b>Single hop assumed.</b> <see cref="ForwardedHeadersOptions.ForwardLimit"/>
/// is left at the framework default (1), matching the canonical "one
/// TLS-terminating reverse proxy in front" topology. Chained proxies would
/// need a larger limit; that is out of scope here.
/// </para>
/// </remarks>
public static class ForwardedHeadersSetup
{
    /// <summary>
    /// Networks trusted by default (no configuration): IPv4 + IPv6 loopback and
    /// the three RFC1918 private ranges. These are added to
    /// <see cref="ForwardedHeadersOptions.KnownIPNetworks"/> so a proxy that
    /// connects from any of them is honored out of the box.
    /// </summary>
    public static IReadOnlyList<IPNetwork> DefaultTrustedNetworks { get; } = new[]
    {
        new IPNetwork(IPAddress.Parse("127.0.0.0"), 8),    // IPv4 loopback
        new IPNetwork(IPAddress.IPv6Loopback, 128),        // ::1/128
        new IPNetwork(IPAddress.Parse("10.0.0.0"), 8),     // RFC1918
        new IPNetwork(IPAddress.Parse("172.16.0.0"), 12),  // RFC1918
        new IPNetwork(IPAddress.Parse("192.168.0.0"), 16), // RFC1918
    };

    /// <summary>
    /// Populates <paramref name="options"/> from the safe defaults plus any
    /// operator-configured trust in <paramref name="network"/>. Clears the
    /// framework's built-in loopback-only defaults first, then repopulates so
    /// the trusted set is exactly "defaults + configured" and never a surprise
    /// union with framework internals.
    /// </summary>
    public static void Configure(ForwardedHeadersOptions options, NetworkOptions network)
    {
        // Rewrite scheme (drives cookie Secure + https activation links), the
        // client IP, and the host. Host is included so redirect/link building
        // behind a proxy reflects the external hostname.
        options.ForwardedHeaders =
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto |
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;

        // Note: KnownNetworks (the pre-.NET-8 member) is obsolete (ASPDEPR005);
        // KnownIPNetworks is its System.Net.IPNetwork-based replacement. This
        // repo builds warnings-as-errors, so KnownIPNetworks is the only usable
        // surface here anyway.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var net in DefaultTrustedNetworks)
            options.KnownIPNetworks.Add(net);
        foreach (var net in ParseNetworks(network.KnownNetworks))
            options.KnownIPNetworks.Add(net);
        foreach (var proxy in ParseProxies(network.KnownProxies))
            options.KnownProxies.Add(proxy);
    }

    /// <summary>
    /// Parses a comma-separated list of literal IP addresses. Blank entries and
    /// entries that are not valid IP addresses are silently skipped, so a
    /// single fat-fingered value never takes down startup.
    /// </summary>
    public static IReadOnlyList<IPAddress> ParseProxies(string? value)
    {
        var result = new List<IPAddress>();
        if (string.IsNullOrWhiteSpace(value))
            return result;
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(token, out var address))
                result.Add(address);
        }
        return result;
    }

    /// <summary>
    /// Parses a comma-separated list of CIDR ranges (<c>address/prefix</c>).
    /// Blank or malformed entries are silently skipped.
    /// </summary>
    public static IReadOnlyList<IPNetwork> ParseNetworks(string? value)
    {
        var result = new List<IPNetwork>();
        if (string.IsNullOrWhiteSpace(value))
            return result;
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(token, out var network))
                result.Add(network);
        }
        return result;
    }
}
