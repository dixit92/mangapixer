namespace com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;

using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Metadata;

/// <summary>
/// The tracker network surface in constants (1.43.0, lane A). One named client per tracker, each behind a
/// <see cref="HostAllowlistHandler"/> with ONLY that tracker's hosts, on a primary handler that never follows a redirect and never keeps
/// cookies, with the fixed <see cref="MetadataHttp.UserAgent"/>, a timeout and bounded response reads. The user's token is added by
/// <see cref="TrackerBearerHandler"/> INSIDE the allowlist handler, so it is attached only after the host was checked (the allowlist
/// handler strips any ambient Authorization header first).
/// </summary>
public static class TrackerHttp
{
    /// <summary>Named client for MyAnimeList: <c>api.myanimelist.net</c> (API v2) and <c>myanimelist.net</c> (the OAuth token endpoint).</summary>
    public const string MyAnimeListClient = "TrackerMyAnimeList";

    /// <summary>Named client for MangaUpdates lists and account sign-in (<c>api.mangaupdates.com</c>, the metadata host).</summary>
    public const string MangaUpdatesClient = "TrackerMangaUpdates";

    public const string MyAnimeListApiHost = "api.myanimelist.net";
    public const string MyAnimeListSiteHost = "myanimelist.net";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Tracker answers are small (one entry, one profile, one token): 256 KiB is plenty.</summary>
    public const int MaxJsonBytes = 256 * 1024;

    /// <summary>The token <see cref="TrackerBearerHandler"/> puts in the <c>Authorization</c> header. Never logged.</summary>
    public static readonly HttpRequestOptionsKey<string> BearerToken = new("mangapixer.tracker.bearer");

    /// <summary>Waits before the next try when a tracker says "slow down" without a <c>Retry-After</c>.</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>The longest wait honoured from a <c>Retry-After</c> header.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(6);

    /// <summary>The wait when the pacer's queue is full.</summary>
    public static readonly TimeSpan BusyRetryAfter = TimeSpan.FromSeconds(2);

    /// <summary>The wait a <c>Retry-After</c> asks for (delta, or a date against <paramref name="time"/>), capped; null when absent.</summary>
    public static TimeSpan? RetryAfterOf(HttpResponseMessage response, TimeProvider time)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is { } date ? date - time.GetUtcNow() : null);
        if (wait is null)
            return null;
        if (wait < TimeSpan.Zero)
            return TimeSpan.Zero;
        return wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }

    /// <summary>
    /// The default mapping of a non-success status (the adapters handle their tracker-specific codes first): 401 -> needs reconnect;
    /// 429 -> rate limited (with <c>Retry-After</c>); 5xx -> unavailable; any other status -> rejected. Status code only, never a body.
    /// </summary>
    public static TrackerException Failure(HttpResponseMessage response, TimeProvider time)
    {
        var status = (int)response.StatusCode;
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new TrackerException(TrackerFailure.NeedsReconnect, status),
            HttpStatusCode.TooManyRequests => new TrackerException(TrackerFailure.RateLimited, status, RetryAfterOf(response, time) ?? DefaultRetryAfter),
            _ when status >= 500 => new TrackerException(TrackerFailure.Unavailable, status, RetryAfterOf(response, time)),
            _ => new TrackerException(TrackerFailure.Rejected, status),
        };
    }

    /// <summary>
    /// Sends one tracker request through <paramref name="limiter"/> (waiting in its small queue; a full queue is "rate limited") and
    /// returns the response for the caller to read. Network failures, timeouts, a refused host or redirect map to
    /// <see cref="TrackerException"/> WITHOUT the inner exception (its text is not ours to vouch for). Logs the tracker, the operation,
    /// the status and the elapsed time - never a URL, an id, a body or a token.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, RateLimiter limiter, ILogger logger, int eventId, string slug, string operation,
        CancellationToken ct)
    {
        using var lease = await limiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired)
        {
            logger.LogInformation(eventId, "Tracker {Tracker} {Operation}: paced out", slug, operation);
            throw new TrackerException(TrackerFailure.RateLimited, retryAfter: lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry : BusyRetryAfter);
        }

        var watch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (MetadataHostRefusedException ex)
        {
            logger.LogWarning(eventId, "Tracker {Tracker} {Operation}: refused ({Code}) in {ElapsedMs} ms", slug, operation, ex.Code, watch.ElapsedMilliseconds);
            throw new TrackerException(TrackerFailure.Rejected);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(eventId, "Tracker {Tracker} {Operation}: {Error} in {ElapsedMs} ms", slug, operation, ex.GetType().Name, watch.ElapsedMilliseconds);
            throw new TrackerException(TrackerFailure.Unavailable);
        }
        logger.LogInformation(eventId, "Tracker {Tracker} {Operation}: {Status} in {ElapsedMs} ms", slug, operation, (int)response.StatusCode, watch.ElapsedMilliseconds);
        return response;
    }

    /// <summary>Reads a JSON body (bounded); an oversized or unreadable body is "unavailable", never echoed.</summary>
    public static async Task<System.Text.Json.JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var bytes = await MetadataHttp.ReadBoundedAsync(response, MaxJsonBytes, ct);
            return System.Text.Json.JsonDocument.Parse(bytes);
        }
        catch (Exception ex) when (ex is MetadataResponseTooLargeException or System.Text.Json.JsonException or HttpRequestException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new TrackerException(TrackerFailure.Unavailable, (int)response.StatusCode);
        }
    }

    /// <summary>A request carrying the user's token for <see cref="TrackerBearerHandler"/>.</summary>
    public static HttpRequestMessage WithBearer(this HttpRequestMessage request, TrackerTokens tokens)
    {
        if (string.IsNullOrEmpty(tokens.AccessToken))
            throw new TrackerException(TrackerFailure.NeedsReconnect);
        request.Options.Set(BearerToken, tokens.AccessToken);
        return request;
    }
}

/// <summary>
/// Puts the user's token (<see cref="TrackerHttp.BearerToken"/>) in the <c>Authorization</c> header. Registered INSIDE
/// <see cref="HostAllowlistHandler"/>, so a token is attached only to a request the allowlist already let through.
/// </summary>
public sealed class TrackerBearerHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(TrackerHttp.BearerToken, out var token) && !string.IsNullOrEmpty(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Pacing of the tracker calls (1.43.0). MangaUpdates calls take a token from the SAME bucket as the metadata fetcher's
/// <c>api.mangaupdates.com</c> calls (<see cref="MetadataGatewayState.ApiLimiterOf"/>): one per-host pacer for both features. They do
/// not count in the metadata daily budget (a separate feature with its own switch). MyAnimeList documents no limit (an over-eager
/// client gets "403 DoS detected"): one request a second, one at a time, for the whole instance.
/// </summary>
public sealed class TrackerPacing : IDisposable
{
    private readonly MetadataGatewayState _metadata;

    public TrackerPacing(MetadataGatewayState metadata, TrackerRateLimitOptions options)
    {
        _metadata = metadata;
        MyAnimeList = new TokenBucketRateLimiter(options.MyAnimeList);
    }

    public RateLimiter MyAnimeList { get; }

    /// <summary>The metadata fetcher's <c>api.mangaupdates.com</c> bucket - shared, never a copy.</summary>
    public RateLimiter MangaUpdates => _metadata.ApiLimiterOf(MetadataProviderAllowlist.MangaUpdates);

    public void Dispose() => MyAnimeList.Dispose();
}

/// <summary>Token-bucket settings of the tracker clients that do not share a metadata bucket.</summary>
public sealed record TrackerRateLimitOptions
{
    /// <summary>MyAnimeList (API and token endpoint): 1 request/s, burst 1 - a read and a write per series take two seconds.</summary>
    public TokenBucketRateLimiterOptions MyAnimeList { get; init; } = new()
    {
        TokenLimit = 1,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 20,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };
}
