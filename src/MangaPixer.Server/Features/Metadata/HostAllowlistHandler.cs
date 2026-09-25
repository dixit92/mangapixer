namespace com.lifepixer.mangapixer.Server.Features.Metadata;

/// <summary>
/// Outermost handler of every metadata named client (1.24.0, lane B2). A request
/// leaves only when it is HTTPS on the default port to a host on this client's
/// allowlist, with no user info; anything else throws
/// <see cref="MetadataHostRefusedException"/> WITHOUT reaching the network.
/// Ambient credentials are stripped (no cookie, Authorization or Referer can ride
/// along). The primary handler has <c>AllowAutoRedirect = false</c>, and a 3xx
/// that comes back is refused here too, so a redirect can never escape the list.
/// </summary>
public sealed class HostAllowlistHandler : DelegatingHandler
{
    private readonly HashSet<string> _allowedHosts;

    public HostAllowlistHandler(params string[] allowedHosts)
    {
        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="uri"/> may be requested by a client with this allowlist.</summary>
    public bool IsAllowed(Uri? uri) => IsAllowed(uri, _allowedHosts);

    public static bool IsAllowed(Uri? uri, IReadOnlySet<string> hosts) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && hosts.Contains(uri.Host);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsAllowed(request.RequestUri))
            throw new MetadataHostRefusedException("host_not_allowed");

        request.Headers.Remove("Cookie");
        request.Headers.Authorization = null;
        request.Headers.Referrer = null;

        var response = await base.SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            response.Dispose();
            throw new MetadataHostRefusedException("redirect_refused");
        }
        return response;
    }
}
