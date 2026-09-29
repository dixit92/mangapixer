namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Net;

/// <summary>
/// The metadata network surface in constants (1.24.0, lane B2; owner-approved
/// gate G1b). Named clients, each behind <see cref="HostAllowlistHandler"/> with
/// its own host only: the MangaUpdates API, its image CDN, (1.28.0) the AniList
/// GraphQL endpoint and (1.29.0) the MangaDex API and its cover image host. Nothing
/// else is reachable.
/// </summary>
public static class MetadataHttp
{
    /// <summary>Named client for <c>api.mangaupdates.com</c> (search + get).</summary>
    public const string MangaUpdatesApiClient = "MangaUpdates";

    /// <summary>Named client for <c>cdn.mangaupdates.com</c> (poster images from stored records).</summary>
    public const string MangaUpdatesImageClient = "MangaUpdatesImages";

    public const string MangaUpdatesApiHost = "api.mangaupdates.com";
    public const string MangaUpdatesImageHost = "cdn.mangaupdates.com";

    /// <summary>
    /// Named client for <c>graphql.anilist.co</c> (1.28.0): ONLY the chapters-per-volume lookup of the Missing
    /// report, by record id or by the linked MangaUpdates title. No images, no search surfaced anywhere else.
    /// </summary>
    public const string AniListClient = "AniList";
    public const string AniListHost = "graphql.anilist.co";

    /// <summary>
    /// Named client for <c>api.mangadex.org</c> (1.29.0): ONLY the companion of an already-linked MangaUpdates
    /// record - a title search by that record's title, a GET by MangaDex id, its volume -> chapter list and its
    /// volume-cover list. Never used to identify or match a folder.
    /// </summary>
    public const string MangaDexApiClient = "MangaDex";

    /// <summary>Named client for <c>uploads.mangadex.org</c> (1.29.0): cover images by the record id and file name MangaDex returned.</summary>
    public const string MangaDexImageClient = "MangaDexImages";

    public const string MangaDexApiHost = "api.mangadex.org";
    public const string MangaDexImageHost = "uploads.mangadex.org";

    /// <summary>
    /// Generic, non-identifying User-Agent: no version, no contact, no browser-UA fallback of any kind. It names
    /// MangaPixer honestly (MangaDex's terms ask for a real, non-spoofed User-Agent); no <c>Via</c> header is ever sent.
    /// </summary>
    public const string UserAgent = "MangaPixer-Metadata";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Response body caps.</summary>
    public const int MaxJsonBytes = 2 * 1024 * 1024;
    public const int MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Reads a response body into memory, refusing anything over <paramref name="maxBytes"/>
    /// (checked against Content-Length up front and while streaming).
    /// </summary>
    public static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new MetadataResponseTooLargeException();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new MetadataResponseTooLargeException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Throws <see cref="MetadataHttpStatusException"/> for any non-2xx response (the body is never read). The wait
    /// comes from <c>Retry-After</c>, else from MangaDex's <c>X-RateLimit-Retry-After</c> (a Unix time in seconds).
    /// </summary>
    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var date = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter is null
            && response.Headers.TryGetValues(RateLimitRetryAfterHeader, out var values)
            && long.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var unix)
            && unix is > 0 and < 32503680000)
            date = DateTimeOffset.FromUnixTimeSeconds(unix);
        throw new MetadataHttpStatusException(response.StatusCode, response.Headers.RetryAfter?.Delta, date);
    }

    /// <summary>MangaDex's rate-limit header: the Unix time (seconds) when requests may start again.</summary>
    public const string RateLimitRetryAfterHeader = "X-RateLimit-Retry-After";

    /// <summary>
    /// The network shape of each provider (1.29.0 gateway generalisation): its API client, its image client and the
    /// fixed set of image hosts an image URL must be on. A MangaDex 403 is read as "slow down" (its edge answers an
    /// over-eager client with 403), like a 429.
    /// </summary>
    public sealed record ProviderTransport(
        string Id, string DisplayName, string ApiClient, string? ImageClient, IReadOnlySet<string> ImageHosts, bool ForbiddenMeansSlowDown = false);

    private static readonly IReadOnlySet<string> s_none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, ProviderTransport> Transports { get; } = new Dictionary<string, ProviderTransport>(StringComparer.Ordinal)
    {
        [MetadataProviderAllowlist.MangaUpdates] = new(MetadataProviderAllowlist.MangaUpdates, "MangaUpdates", MangaUpdatesApiClient,
            MangaUpdatesImageClient, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MangaUpdatesImageHost }),
        [MetadataProviderAllowlist.AniList] = new(MetadataProviderAllowlist.AniList, "AniList", AniListClient, null, s_none),
        [MetadataProviderAllowlist.MangaDex] = new(MetadataProviderAllowlist.MangaDex, "MangaDex", MangaDexApiClient,
            MangaDexImageClient, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MangaDexImageHost }, ForbiddenMeansSlowDown: true),
    };

    /// <summary>The transport of a provider id, or null for an unknown id.</summary>
    public static ProviderTransport? Transport(string providerId) => Transports.GetValueOrDefault(providerId);
}

/// <summary>A provider answered with a non-success status. Carries Retry-After when sent.</summary>
public sealed class MetadataHttpStatusException : Exception
{
    public MetadataHttpStatusException(HttpStatusCode status, TimeSpan? retryAfterDelta, DateTimeOffset? retryAfterDate)
        : base("Provider returned HTTP " + (int)status)
    {
        Status = status;
        RetryAfterDelta = retryAfterDelta;
        RetryAfterDate = retryAfterDate;
    }

    public HttpStatusCode Status { get; }
    public TimeSpan? RetryAfterDelta { get; }
    public DateTimeOffset? RetryAfterDate { get; }
}

/// <summary>A response body exceeded its cap.</summary>
public sealed class MetadataResponseTooLargeException : Exception
{
    public MetadataResponseTooLargeException() : base("Provider response exceeded its size cap") { }
}

/// <summary>A malformed provider response (unparseable JSON, not an image, ...).</summary>
public sealed class MetadataResponseInvalidException : Exception
{
    public MetadataResponseInvalidException(string code) : base("Provider response rejected: " + code)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>The allowlist handler refused a request (other host, scheme or port) or a redirect.</summary>
public sealed class MetadataHostRefusedException : Exception
{
    public MetadataHostRefusedException(string code) : base("Metadata request refused: " + code)
    {
        Code = code;
    }

    /// <summary><c>host_not_allowed</c> or <c>redirect_refused</c>.</summary>
    public string Code { get; }
}
