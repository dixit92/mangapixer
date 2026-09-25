namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Net;

/// <summary>
/// The metadata network surface in constants (1.24.0, lane B2; owner-approved
/// gate G1b). Two named clients, both behind <see cref="HostAllowlistHandler"/>:
/// the MangaUpdates API and its image CDN. Nothing else is reachable.
/// </summary>
public static class MetadataHttp
{
    /// <summary>Named client for <c>api.mangaupdates.com</c> (search + get).</summary>
    public const string MangaUpdatesApiClient = "MangaUpdates";

    /// <summary>Named client for <c>cdn.mangaupdates.com</c> (poster images from stored records).</summary>
    public const string MangaUpdatesImageClient = "MangaUpdatesImages";

    public const string MangaUpdatesApiHost = "api.mangaupdates.com";
    public const string MangaUpdatesImageHost = "cdn.mangaupdates.com";

    /// <summary>Generic, non-identifying User-Agent: no version, no contact, no browser-UA fallback of any kind.</summary>
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

    /// <summary>Throws <see cref="MetadataHttpStatusException"/> for any non-2xx response (the body is never read).</summary>
    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        throw new MetadataHttpStatusException(
            response.StatusCode,
            response.Headers.RetryAfter?.Delta,
            response.Headers.RetryAfter?.Date);
    }
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
