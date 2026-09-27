namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>A gateway refusal or provider failure, mapped 1:1 onto an HTTP <c>ApiError</c>.</summary>
public sealed class MetadataGatewayException : Exception
{
    public MetadataGatewayException(int httpStatus, string code, string message, DateTimeOffset? retryAt = null)
        : base(message)
    {
        HttpStatus = httpStatus;
        Code = code;
        RetryAt = retryAt;
    }

    public int HttpStatus { get; }
    public string Code { get; }
    public DateTimeOffset? RetryAt { get; }
}

/// <summary>Who asked for a provider call (stage 2).</summary>
public enum MetadataCallOrigin
{
    /// <summary>An admin action (identify, preview, link, refresh): stage-1 behaviour.</summary>
    Interactive = 0,

    /// <summary>
    /// Background work (auto-match, id-only refresh): additionally needs the global
    /// Automatic matching switch with the current automatic consent; waits for a
    /// token instead of failing with <c>provider_busy</c>, paced at <= 1 request/s.
    /// </summary>
    Automatic = 1,
}

/// <summary>
/// Per-call options of a gateway call: the origin, and a counter of requests the
/// call actually sent (a cache hit or a refusal sends none) for run counters.
/// </summary>
public sealed class MetadataCallContext
{
    private int _requestsSent;

    public MetadataCallOrigin Origin { get; init; }

    public int RequestsSent => Volatile.Read(ref _requestsSent);

    public static MetadataCallContext Automatic() => new() { Origin = MetadataCallOrigin.Automatic };

    internal void CountRequest() => Interlocked.Increment(ref _requestsSent);
}

/// <summary>
/// The ONLY path from MangaPixer to a metadata provider (1.24.0, lane B2; network
/// surface approved at gate G1b). Every call is made for one library and passes,
/// at call time and in this order:
/// 1. config <c>Metadata:NetworkDisabled</c> (operator hard kill) -> 409;
/// 2. the global "Fetch from the web" switch with the CURRENT consent version -> 409;
/// 3. the library's own switch -> 409;
/// 4. persisted backoff -> 503;
/// 5. the persisted daily budget -> 429;
/// 6. the provider's token bucket (small FIFO queue; overflow) -> 429.
/// Stage 2 adds <see cref="MetadataCallOrigin.Automatic"/> calls: after gate 3 they
/// also need the global Automatic matching switch with the CURRENT automatic
/// consent (409 <c>automatic_off</c>); they share the one daily budget and the
/// persisted backoff (a 429 pauses both origins), and instead of gate 6's overflow
/// they wait for a token, serialized and paced at <= 1 request/s.
/// A refusal makes ZERO calls. Switches are read per call (never cached), and are
/// re-checked when a call returns, so a result that arrives after the admin turned
/// the feature off is dropped, not stored.
///
/// Logs carry provider, operation, status, elapsed ms and ids only - never the
/// query text, titles, URLs or bodies; exceptions as their type name.
/// </summary>
public sealed class MetadataGateway
{
    public static readonly TimeSpan SearchCacheTtl = TimeSpan.FromHours(1);
    public const int MaxQueryLength = 200;
    public const int SearchPageSize = 10;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataProviderRegistry _providers;
    private readonly MetadataGatewayState _state;
    private readonly MetadataBudget _budget;
    private readonly MetadataBackoff _backoff;
    private readonly MetadataSettingsService _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MetadataGateway> _logger;

    public MetadataGateway(
        MangaPixerDbContext db,
        MetadataProviderRegistry providers,
        MetadataGatewayState state,
        MetadataBudget budget,
        MetadataBackoff backoff,
        MetadataSettingsService settings,
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        ILogger<MetadataGateway> logger)
    {
        _db = db;
        _providers = providers;
        _state = state;
        _budget = budget;
        _backoff = backoff;
        _settings = settings;
        _httpFactory = httpFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Why web lookups are unavailable for <paramref name="libraryId"/> right now
    /// (gates 1-3 only), or null when they are allowed. No network, no side effect.
    /// </summary>
    public Task<MetadataGatewayException?> CheckSwitchesAsync(long libraryId, CancellationToken ct = default) =>
        CheckSwitchesAsync(libraryId, MetadataCallOrigin.Interactive, ct);

    /// <summary>
    /// Gates 1-3 and, for <see cref="MetadataCallOrigin.Automatic"/>, the automatic
    /// switch + consent. Null when allowed. No network, no side effect.
    /// </summary>
    public async Task<MetadataGatewayException?> CheckSwitchesAsync(long libraryId, MetadataCallOrigin origin, CancellationToken ct = default)
    {
        if (_settings.NetworkDisabledByConfig)
            return new MetadataGatewayException(StatusCodes.Status409Conflict, "metadata_network_disabled",
                "Web metadata is disabled in the server configuration (Metadata:NetworkDisabled).");

        var global = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataEnabled, s.MetadataConsentVersion, s.MetadataAutoMatchEnabled, s.MetadataAutoConsentVersion })
            .FirstOrDefaultAsync(ct);
        if (global is not { MetadataEnabled: true } || global.MetadataConsentVersion != MetadataConsent.CurrentVersion)
            return new MetadataGatewayException(StatusCodes.Status409Conflict, "metadata_disabled",
                "Fetching series information from the web is off. An admin can turn it on in Metadata Manager.");
        if (origin == MetadataCallOrigin.Automatic
            && (!global.MetadataAutoMatchEnabled || global.MetadataAutoConsentVersion != AutoMatch.MetadataAutoConsent.CurrentVersion))
            return new MetadataGatewayException(StatusCodes.Status409Conflict, "automatic_off",
                "Automatic matching is off. An admin can turn it on in Metadata Manager.");

        var libraryEnabled = await _db.Libraries.AsNoTracking()
            .Where(l => l.Id == libraryId)
            .Select(l => l.MetadataEnabled)
            .FirstOrDefaultAsync(ct);
        if (!libraryEnabled)
            return new MetadataGatewayException(StatusCodes.Status409Conflict, "library_metadata_disabled",
                "Fetching series information from the web is off for this library.");
        return null;
    }

    /// <summary>
    /// Parses a pasted URL / shortcode LOCALLY (no network, no gate). Null when the
    /// provider does not recognize it.
    /// </summary>
    public ProviderRef? ParseReference(string providerId, string input)
    {
        var provider = Provider(providerId);
        return provider.TryParseReference(input, out var reference) ? reference : null;
    }

    /// <summary>
    /// A confirmed search. Served from the in-memory cache (1 h, keyed by a SHA-256
    /// of provider + normalized query + page; never persisted or logged) when
    /// possible, else one gated request.
    /// </summary>
    public Task<ProviderSearchPage> SearchAsync(
        string providerId, long libraryId, string query, int page, bool hideDoujinshiAndNovels = false, CancellationToken ct = default) =>
        SearchCoreAsync(providerId, libraryId, query, page, hideDoujinshiAndNovels, allowDoujinshi: false, call: null, ct);

    /// <summary>
    /// An AUTOMATIC search (stage 2): always with the fixed provider type filter
    /// (owner decision 4a), doujinshi allowed only when <paramref name="allowDoujinshi"/>
    /// (below a "Doujinshi &amp; adult one-shots" folder). Page 1, or page 2 of the SAME text when the
    /// matcher asks for it (1.27.0; see <c>AutoMatchLookup</c>).
    /// </summary>
    public Task<ProviderSearchPage> SearchAutomaticAsync(
        string providerId, long libraryId, string query, bool allowDoujinshi, MetadataCallContext call, CancellationToken ct = default,
        int page = 1) =>
        SearchCoreAsync(providerId, libraryId, query, Math.Clamp(page, 1, 2), hideDoujinshiAndNovels: true, allowDoujinshi, call, ct);

    private async Task<ProviderSearchPage> SearchCoreAsync(
        string providerId, long libraryId, string query, int page, bool hideDoujinshiAndNovels, bool allowDoujinshi,
        MetadataCallContext? call, CancellationToken ct)
    {
        var provider = Provider(providerId);
        var text = NormalizeQuery(query);
        if (text.Length == 0 || text.Length > MaxQueryLength)
            throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "invalid_query",
                $"The search text must be 1-{MaxQueryLength} characters.");
        page = Math.Clamp(page, 1, 100);

        var origin = call?.Origin ?? MetadataCallOrigin.Interactive;
        await ThrowIfSwitchedOffAsync(libraryId, origin, ct);
        var key = SearchCacheKey(provider.Id, text, page, hideDoujinshiAndNovels, allowDoujinshi);
        if (_cache.TryGetValue<ProviderSearchPage>(key, out var cached) && cached is not null)
            return cached;

        var result = await CallAsync(provider.Id, "search", libraryId, _state.ApiLimiter,
            c => provider.SearchSeriesAsync(
                new ProviderSearchQuery(text, libraryId, page, SearchPageSize, hideDoujinshiAndNovels, hideDoujinshiAndNovels && allowDoujinshi), c),
            call, ct);
        _cache.Set(key, result, SearchCacheTtl);
        return result;
    }

    /// <summary>One gated GET of a record; null when the provider says it does not exist.</summary>
    public async Task<ProviderSeriesRecord?> GetSeriesAsync(
        string providerId, long libraryId, string externalId, CancellationToken ct = default, MetadataCallContext? call = null)
    {
        var provider = Provider(providerId);
        if (!MetadataIdentifiers.IsValidExternalId(externalId))
            throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "invalid_request", "The record id is not valid.");
        await ThrowIfSwitchedOffAsync(libraryId, call?.Origin ?? MetadataCallOrigin.Interactive, ct);
        return await CallAsync(provider.Id, "get", libraryId, _state.ApiLimiter, c => provider.GetSeriesAsync(externalId, c), call, ct);
    }

    /// <summary>
    /// One gated image GET. The URL must come from a stored record or a search
    /// result held server-side (never from the client) and is re-validated against
    /// the image allowlist; the body is capped and must carry image magic bytes.
    /// </summary>
    public async Task<byte[]> FetchImageAsync(
        string providerId, long libraryId, string imageUrl, CancellationToken ct = default, MetadataCallContext? call = null)
    {
        var provider = Provider(providerId);
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            || !HostAllowlistHandler.IsAllowed(uri, s_imageHosts))
            throw new MetadataGatewayException(StatusCodes.Status502BadGateway, "host_not_allowed", "The image address is not on the allowlist.");

        await ThrowIfSwitchedOffAsync(libraryId, call?.Origin ?? MetadataCallOrigin.Interactive, ct);
        return await CallAsync(provider.Id, "image", libraryId, _state.ImageLimiter, async c =>
        {
            var client = _httpFactory.CreateClient(MetadataHttp.MangaUpdatesImageClient);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, c);
            MetadataHttp.EnsureSuccess(response);
            var bytes = await MetadataHttp.ReadBoundedAsync(response, MetadataHttp.MaxImageBytes, c);
            if (MetadataImageStore.DetectExtension(bytes) is null)
                throw new MetadataResponseInvalidException("not_an_image");
            return bytes;
        }, call, ct);
    }

    private static readonly IReadOnlySet<string> s_imageHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataHttp.MangaUpdatesImageHost };

    private IMetadataProvider Provider(string providerId) =>
        _providers.Find(providerId)
        ?? throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "unknown_provider", "No such metadata provider.");

    private async Task ThrowIfSwitchedOffAsync(long libraryId, MetadataCallOrigin origin, CancellationToken ct)
    {
        if (await CheckSwitchesAsync(libraryId, origin, ct) is { } refusal)
        {
            _logger.LogInformation(LogEvents.Metadata.GatewayRefused, "Metadata call refused for library {LibraryId}: {Code}", libraryId, refusal.Code);
            throw refusal;
        }
    }

    /// <summary>Gates 4-6, the call itself, backoff bookkeeping and the post-call switch re-check.</summary>
    private async Task<T> CallAsync<T>(string providerId, string operation, long libraryId, RateLimiter limiter,
        Func<CancellationToken, Task<T>> call, MetadataCallContext? context, CancellationToken ct)
    {
        var origin = context?.Origin ?? MetadataCallOrigin.Interactive;
        if (await _backoff.ActiveUntilAsync(ct) is { } until)
            throw Refuse(libraryId, new MetadataGatewayException(StatusCodes.Status503ServiceUnavailable, "provider_backoff",
                "The metadata provider asked us to slow down. Try again later.", until));

        if ((await _budget.GetAsync(ct)).Exhausted)
            throw Refuse(libraryId, BudgetExhausted());

        if (origin == MetadataCallOrigin.Automatic)
            await _state.PaceAutomaticAsync(ct);

        using var lease = await AcquireAsync(limiter, origin, ct);
        if (!lease.IsAcquired)
            throw Refuse(libraryId, new MetadataGatewayException(StatusCodes.Status429TooManyRequests, "provider_busy",
                "Too many metadata requests are queued. Try again in a moment."));

        if (!await _budget.TryConsumeAsync(ct))
            throw Refuse(libraryId, BudgetExhausted());
        context?.CountRequest();

        var watch = Stopwatch.StartNew();
        T result;
        try
        {
            result = await call(ct);
        }
        catch (Exception ex) when (ex is not MetadataGatewayException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw await FailAsync(providerId, operation, libraryId, ex, watch.ElapsedMilliseconds, ct);
        }

        _logger.LogInformation(LogEvents.Metadata.ProviderCall, "Metadata {Provider} {Operation} ({Origin}) for library {LibraryId}: {Status} in {ElapsedMs} ms",
            providerId, operation, origin, libraryId, 200, watch.ElapsedMilliseconds);
        await _backoff.RecordSuccessAsync(ct);

        // In-flight calls finish, but their results are kept only if the switches are still on.
        await ThrowIfSwitchedOffAsync(libraryId, origin, ct);
        return result;
    }

    /// <summary>
    /// Interactive calls take a token or join the bucket's small queue (overflow
    /// refuses). Automatic calls never refuse for a busy bucket: they wait and try
    /// again, so an admin's burst always goes first.
    /// </summary>
    private static async Task<RateLimitLease> AcquireAsync(RateLimiter limiter, MetadataCallOrigin origin, CancellationToken ct)
    {
        if (origin == MetadataCallOrigin.Interactive)
            return await limiter.AcquireAsync(1, ct);
        while (true)
        {
            var lease = limiter.AttemptAcquire(1);
            if (lease.IsAcquired)
                return lease;
            lease.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    private MetadataGatewayException Refuse(long libraryId, MetadataGatewayException refusal)
    {
        _logger.LogInformation(LogEvents.Metadata.GatewayRefused, "Metadata call refused for library {LibraryId}: {Code}", libraryId, refusal.Code);
        return refusal;
    }

    private static MetadataGatewayException BudgetExhausted() =>
        new(StatusCodes.Status429TooManyRequests, "budget_exhausted",
            "Today's metadata request budget is used up. It resets at 00:00 UTC; an admin can raise it in Metadata Manager.");

    /// <summary>Classifies a failed call, updates the persisted backoff / last error, and returns the error to throw.</summary>
    private async Task<MetadataGatewayException> FailAsync(string providerId, string operation, long libraryId, Exception ex, long elapsedMs, CancellationToken ct)
    {
        var status = ex is MetadataHttpStatusException http ? (int)http.Status : 0;
        _logger.LogWarning(LogEvents.Metadata.ProviderCallFailed, "Metadata {Provider} {Operation} for library {LibraryId} failed: {Status} {Error} in {ElapsedMs} ms",
            providerId, operation, libraryId, status, ex.GetType().Name, elapsedMs);

        switch (ex)
        {
            case MetadataHttpStatusException { Status: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable } limited:
                {
                    var code = limited.Status == HttpStatusCode.TooManyRequests ? "rate_limited" : "http_503";
                    var until = await _backoff.RecordRateLimitedAsync(limited.RetryAfterDelta, limited.RetryAfterDate, code, ct);
                    return new MetadataGatewayException(StatusCodes.Status503ServiceUnavailable, "provider_backoff",
                        "The metadata provider is busy. Try again later.", until);
                }
            case MetadataHttpStatusException { Status: >= HttpStatusCode.InternalServerError }:
                {
                    var until = await _backoff.RecordFailureAsync("http_5xx", countsTowardStreak: true, ct);
                    return new MetadataGatewayException(StatusCodes.Status502BadGateway, "provider_error",
                        "The metadata provider returned an error. Try again later.", until);
                }
            case MetadataHttpStatusException:
                await _backoff.RecordFailureAsync("http_4xx", countsTowardStreak: false, ct);
                return new MetadataGatewayException(StatusCodes.Status502BadGateway, "provider_error",
                    "The metadata provider rejected the request.");
            case OperationCanceledException or TimeoutException:
                {
                    var until = await _backoff.RecordFailureAsync("timeout", countsTowardStreak: true, ct);
                    return new MetadataGatewayException(StatusCodes.Status504GatewayTimeout, "provider_timeout",
                        "The metadata provider did not answer in time.", until);
                }
            case MetadataHostRefusedException refused:
                await _backoff.RecordFailureAsync(refused.Code, countsTowardStreak: false, ct);
                return new MetadataGatewayException(StatusCodes.Status502BadGateway, refused.Code,
                    "The metadata provider answered with a redirect or an address that is not allowed.");
            case MetadataResponseTooLargeException:
                await _backoff.RecordFailureAsync("response_too_large", countsTowardStreak: false, ct);
                return new MetadataGatewayException(StatusCodes.Status502BadGateway, "response_too_large",
                    "The metadata provider's response was too large.");
            case MetadataResponseInvalidException invalid:
                await _backoff.RecordFailureAsync(invalid.Code, countsTowardStreak: false, ct);
                return new MetadataGatewayException(StatusCodes.Status502BadGateway, invalid.Code,
                    "The metadata provider's response could not be read.");
            default:
                {
                    // Network errors (DNS, connection refused, TLS) count like a 5xx.
                    var until = await _backoff.RecordFailureAsync("network_error", countsTowardStreak: true, ct);
                    return new MetadataGatewayException(StatusCodes.Status502BadGateway, "provider_unreachable",
                        "The metadata provider could not be reached.", until);
                }
        }
    }

    /// <summary>Trimmed, whitespace-collapsed query (what is sent and what the cache key hashes).</summary>
    public static string NormalizeQuery(string? query) =>
        string.Join(' ', (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string SearchCacheKey(string providerId, string text, int page, bool hideDoujinshiAndNovels, bool allowDoujinshi)
    {
        var filter = !hideDoujinshiAndNovels ? 0 : allowDoujinshi ? 2 : 1;
        var material = Encoding.UTF8.GetBytes($"{providerId}\n{text.ToLowerInvariant()}\n{page}\n{filter}");
        return "metadata-search:" + Convert.ToHexString(SHA256.HashData(material));
    }
}
