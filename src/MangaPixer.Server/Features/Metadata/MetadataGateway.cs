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
/// 3. the library's own switch -> 409; (1.28.0) the provider allowlist -> 409;
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
/// 1.29.0 (gateway generalisation): every provider has its own token buckets,
/// persisted backoff and failure streak (<see cref="MetadataHttp.Transports"/>:
/// API client, image client, fixed image hosts), so a 429 from MangaDex pauses
/// MangaDex only. Companion calls (MangaDex, AniList - never an Identify provider)
/// go through <see cref="CompanionCallAsync{T}"/> with the same gates, the same
/// budget and, when automatic, the same pacing.
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
    /// switch + consent, for MangaUpdates (the provider of Identify, auto-match and
    /// refresh). Null when allowed. No network, no side effect.
    /// </summary>
    public Task<MetadataGatewayException?> CheckSwitchesAsync(long libraryId, MetadataCallOrigin origin, CancellationToken ct = default) =>
        CheckSwitchesAsync(libraryId, origin, MetadataProviderAllowlist.MangaUpdates, ct);

    /// <summary>
    /// The switch gates for one provider (1.28.0): gates 1-3, the automatic switch for
    /// automatic calls, and the provider allowlist - a site the admin removed gets no
    /// request of any kind (409 <c>provider_not_allowed</c>).
    /// </summary>
    public async Task<MetadataGatewayException?> CheckSwitchesAsync(long libraryId, MetadataCallOrigin origin, string providerId, CancellationToken ct = default)
    {
        if (_settings.NetworkDisabledByConfig)
            return new MetadataGatewayException(StatusCodes.Status409Conflict, "metadata_network_disabled",
                "Web metadata is disabled in the server configuration (Metadata:NetworkDisabled).");

        var global = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataEnabled, s.MetadataConsentVersion, s.MetadataAutoMatchEnabled, s.MetadataAutoConsentVersion, s.MetadataProvidersJson })
            .FirstOrDefaultAsync(ct);
        if (global is not { MetadataEnabled: true } || global.MetadataConsentVersion != MetadataConsent.CurrentVersion)
            return new MetadataGatewayException(StatusCodes.Status409Conflict, "metadata_disabled",
                "Fetching series information from the web is off. An admin can turn it on in Metadata Manager.");
        if (origin == MetadataCallOrigin.Automatic
            && (!global.MetadataAutoMatchEnabled || global.MetadataAutoConsentVersion != AutoMatch.MetadataAutoConsent.CurrentVersion))
            return new MetadataGatewayException(StatusCodes.Status409Conflict, "automatic_off",
                "Automatic matching is off. An admin can turn it on in Metadata Manager.");
        if (!MetadataProviderAllowlist.IsAllowed(global.MetadataProvidersJson, providerId))
            return new MetadataGatewayException(StatusCodes.Status409Conflict, "provider_not_allowed",
                "This site is not on the provider allowlist. An admin can add it back in Metadata Manager > Settings.");

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
        await ThrowIfSwitchedOffAsync(libraryId, origin, provider.Id, ct);
        var key = SearchCacheKey(provider.Id, text, page, hideDoujinshiAndNovels, allowDoujinshi);
        if (_cache.TryGetValue<ProviderSearchPage>(key, out var cached) && cached is not null)
            return cached;

        var result = await CallAsync(provider.Id, "search", libraryId, _state.ApiLimiterOf(provider.Id),
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
        await ThrowIfSwitchedOffAsync(libraryId, call?.Origin ?? MetadataCallOrigin.Interactive, provider.Id, ct);
        return await CallAsync(provider.Id, "get", libraryId, _state.ApiLimiterOf(provider.Id), c => provider.GetSeriesAsync(externalId, c), call, ct);
    }

    /// <summary>
    /// One gated image GET. The URL must come from a stored record or a search
    /// result held server-side (never from the client) and is re-validated against
    /// THAT provider's image hosts (1.29.0: MangaUpdates' CDN, MangaDex's
    /// <c>uploads.mangadex.org</c>); it uses that provider's image client, bucket and
    /// backoff. The body is capped and must carry image magic bytes. HEAD is never used.
    /// </summary>
    public async Task<byte[]> FetchImageAsync(
        string providerId, long libraryId, string imageUrl, CancellationToken ct = default, MetadataCallContext? call = null)
    {
        var transport = MetadataHttp.Transport(providerId) is { ImageClient: not null } t
            ? t
            : throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "unknown_provider", "No such metadata provider.");
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            || !HostAllowlistHandler.IsAllowed(uri, transport.ImageHosts))
            throw new MetadataGatewayException(StatusCodes.Status502BadGateway, "host_not_allowed", "The image address is not on the allowlist.");

        await ThrowIfSwitchedOffAsync(libraryId, call?.Origin ?? MetadataCallOrigin.Interactive, transport.Id, ct);
        return await CallAsync(transport.Id, "image", libraryId, _state.ImageLimiterOf(transport.Id), async c =>
        {
            var client = _httpFactory.CreateClient(transport.ImageClient!);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, c);
            MetadataHttp.EnsureSuccess(response);
            var bytes = await MetadataHttp.ReadBoundedAsync(response, MetadataHttp.MaxImageBytes, c);
            if (MetadataImageStore.DetectExtension(bytes) is null)
                throw new MetadataResponseInvalidException("not_an_image");
            return bytes;
        }, call, ct);
    }

    /// <summary>
    /// One gated call to a chapters-per-volume source (1.28.0, AniList). Same as <see cref="CompanionCallAsync{T}"/>
    /// for the provider's id: admin actions by default; (1.29.0) an <see cref="MetadataCallOrigin.Automatic"/> context
    /// for the volume-cover pass's totals fallback (then the automatic switch + consent are required too, and the call
    /// is paced). A 429 / 503 starts AniList's OWN persisted backoff and never touches MangaUpdates'.
    /// </summary>
    public Task<T> ConversionCallAsync<T>(
        Providers.AniList.IUnitConversionProvider provider, string operation, long libraryId, Func<CancellationToken, Task<T>> call,
        CancellationToken ct = default, MetadataCallContext? context = null) =>
        CompanionCallAsync(provider.Id, operation, libraryId, call, context, ct);

    /// <summary>
    /// One gated API call to a companion provider (1.29.0: MangaDex, AniList - providers that are never offered to
    /// Identify or the matcher). Gates, in order: the config kill switch, "Fetch from the web" with the current consent,
    /// for automatic calls the Automatic matching switch with the current automatic consent, the provider allowlist, the
    /// library's switch; then the provider's persisted backoff (503), the ONE daily budget (429), the automatic pacing
    /// and the provider's token bucket. Failures update the provider's own backoff; the switches are re-checked when
    /// the call returns. <paramref name="call"/> must only translate HTTP/JSON (the provider classes).
    /// </summary>
    public async Task<T> CompanionCallAsync<T>(
        string providerId, string operation, long libraryId, Func<CancellationToken, Task<T>> call, MetadataCallContext? context,
        CancellationToken ct = default)
    {
        if (MetadataHttp.Transport(providerId) is null || _providers.Find(providerId) is not null)
            throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "unknown_provider", "No such metadata provider.");
        await ThrowIfSwitchedOffAsync(libraryId, context?.Origin ?? MetadataCallOrigin.Interactive, providerId, ct);
        return await CallAsync(providerId, operation, libraryId, _state.ApiLimiterOf(providerId), call, context, ct);
    }

    private IMetadataProvider Provider(string providerId) =>
        _providers.Find(providerId)
        ?? throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "unknown_provider", "No such metadata provider.");

    private async Task ThrowIfSwitchedOffAsync(long libraryId, MetadataCallOrigin origin, string providerId, CancellationToken ct)
    {
        if (await CheckSwitchesAsync(libraryId, origin, providerId, ct) is { } refusal)
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
        if (await _backoff.ActiveUntilAsync(providerId, ct) is { } until)
            throw Refuse(libraryId, new MetadataGatewayException(StatusCodes.Status503ServiceUnavailable, "provider_backoff",
                $"{NameOf(providerId)} asked us to slow down. Try again later.", until));

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
        await _backoff.RecordSuccessAsync(providerId, ct);

        // In-flight calls finish, but their results are kept only if the switches are still on.
        await ThrowIfSwitchedOffAsync(libraryId, origin, providerId, ct);
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

    /// <summary>"The metadata provider" for MangaUpdates (the wording of 1.24.0), else the provider's name.</summary>
    private static string NameOf(string providerId) =>
        providerId == MetadataProviderAllowlist.MangaUpdates ? "The metadata provider" : MetadataHttp.Transport(providerId)?.DisplayName ?? "The metadata provider";

    /// <summary>
    /// Classifies a failed call, updates THAT provider's persisted backoff / last error, and returns the error to throw.
    /// A 429 / 503 - and for MangaDex a 403 (its edge's answer to a client that is too fast) - is a backoff.
    /// </summary>
    private async Task<MetadataGatewayException> FailAsync(string providerId, string operation, long libraryId, Exception ex, long elapsedMs, CancellationToken ct)
    {
        var status = ex is MetadataHttpStatusException http ? (int)http.Status : 0;
        _logger.LogWarning(LogEvents.Metadata.ProviderCallFailed, "Metadata {Provider} {Operation} for library {LibraryId} failed: {Status} {Error} in {ElapsedMs} ms",
            providerId, operation, libraryId, status, ex.GetType().Name, elapsedMs);

        var name = NameOf(providerId);
        var forbiddenIsLimit = MetadataHttp.Transport(providerId)?.ForbiddenMeansSlowDown == true;
        switch (ex)
        {
            case MetadataHttpStatusException limited when limited.Status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                || (forbiddenIsLimit && limited.Status == HttpStatusCode.Forbidden):
                {
                    var code = limited.Status switch
                    {
                        HttpStatusCode.TooManyRequests => "rate_limited",
                        HttpStatusCode.Forbidden => "http_403",
                        _ => "http_503",
                    };
                    var until = await _backoff.RecordRateLimitedAsync(providerId, limited.RetryAfterDelta, limited.RetryAfterDate, code, ct);
                    return new MetadataGatewayException(StatusCodes.Status503ServiceUnavailable, "provider_backoff",
                        $"{name} is busy. Try again later.", until);
                }
            case MetadataHttpStatusException { Status: >= HttpStatusCode.InternalServerError }:
                {
                    var until = await _backoff.RecordFailureAsync(providerId, "http_5xx", countsTowardStreak: true, ct);
                    return new MetadataGatewayException(StatusCodes.Status502BadGateway, "provider_error",
                        $"{name} returned an error. Try again later.", until);
                }
            case MetadataHttpStatusException:
                await _backoff.RecordFailureAsync(providerId, "http_4xx", countsTowardStreak: false, ct);
                return new MetadataGatewayException(StatusCodes.Status502BadGateway, "provider_error",
                    $"{name} rejected the request.");
            case OperationCanceledException or TimeoutException:
                {
                    var until = await _backoff.RecordFailureAsync(providerId, "timeout", countsTowardStreak: true, ct);
                    return new MetadataGatewayException(StatusCodes.Status504GatewayTimeout, "provider_timeout",
                        $"{name} did not answer in time.", until);
                }
            case MetadataHostRefusedException refused:
                await _backoff.RecordFailureAsync(providerId, refused.Code, countsTowardStreak: false, ct);
                return new MetadataGatewayException(StatusCodes.Status502BadGateway, refused.Code,
                    $"{name} answered with a redirect or an address that is not allowed.");
            case MetadataResponseTooLargeException:
                await _backoff.RecordFailureAsync(providerId, "response_too_large", countsTowardStreak: false, ct);
                return new MetadataGatewayException(StatusCodes.Status502BadGateway, "response_too_large",
                    $"{name}'s response was too large.");
            case MetadataResponseInvalidException invalid:
                await _backoff.RecordFailureAsync(providerId, invalid.Code, countsTowardStreak: false, ct);
                return new MetadataGatewayException(StatusCodes.Status502BadGateway, invalid.Code,
                    $"{name}'s response could not be read.");
            default:
                {
                    // Network errors (DNS, connection refused, TLS) count like a 5xx.
                    var until = await _backoff.RecordFailureAsync(providerId, "network_error", countsTowardStreak: true, ct);
                    return new MetadataGatewayException(StatusCodes.Status502BadGateway, "provider_unreachable",
                        $"{name} could not be reached.", until);
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
