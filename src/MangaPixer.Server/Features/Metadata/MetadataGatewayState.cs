namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Threading.RateLimiting;

/// <summary>Token-bucket settings of the two metadata clients (owner-approved numbers).</summary>
public sealed record MetadataRateLimitOptions
{
    /// <summary><c>api.mangaupdates.com</c>: 2 requests/s, burst 2.</summary>
    public TokenBucketRateLimiterOptions Api { get; init; } = new()
    {
        TokenLimit = 2,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromMilliseconds(500),
        QueueLimit = 10,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };

    /// <summary><c>cdn.mangaupdates.com</c> images: a separate 5 requests/s bucket.</summary>
    public TokenBucketRateLimiterOptions Images { get; init; } = new()
    {
        TokenLimit = 5,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromMilliseconds(200),
        QueueLimit = 10,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };

    /// <summary>
    /// <c>graphql.anilist.co</c> (1.28.0): 1 request/s, burst 1 - under AniList's published 90 requests/minute
    /// (lowered to 30 while degraded, when its 429 + Retry-After takes over). Admin actions and (1.29.0) the automatic
    /// totals fallback of the volume-cover pass.
    /// </summary>
    public TokenBucketRateLimiterOptions AniList { get; init; } = new()
    {
        TokenLimit = 1,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 25,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };

    /// <summary>
    /// <c>api.mangadex.org</c> (1.29.0): 2 requests/s, burst 2 - well under MangaDex's ~5 requests/s per IP; automatic
    /// calls are paced at 1/s on top.
    /// </summary>
    public TokenBucketRateLimiterOptions MangaDexApi { get; init; } = new()
    {
        TokenLimit = 2,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromMilliseconds(500),
        QueueLimit = 10,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };

    /// <summary><c>uploads.mangadex.org</c> cover images (1.29.0): 2 requests/s, burst 2.</summary>
    public TokenBucketRateLimiterOptions MangaDexImages { get; init; } = new()
    {
        TokenLimit = 2,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromMilliseconds(500),
        QueueLimit = 10,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };

    /// <summary>
    /// Minimum spacing between AUTOMATIC requests (stage 2: at most 1 request/s, so
    /// the 2 req/s API bucket always has room for an admin).
    /// </summary>
    public TimeSpan AutomaticInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Process-wide gateway state (singleton, 1.24.0 lane B2): the token buckets,
/// the lock that serializes writes of the persisted budget/backoff columns, and
/// the in-memory streak of consecutive 5xx/timeouts. 1.29.0 (gateway
/// generalisation): one set of buckets and one failure streak PER PROVIDER
/// (MangaUpdates API + images, AniList, MangaDex API + images), so a busy or
/// failing provider never slows or pauses another; the persisted backoff is per
/// provider too (<see cref="MetadataBackoff"/>). The automatic pacing (at most one
/// automatic request per second) stays process-wide, across providers.
/// </summary>
public sealed class MetadataGatewayState : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _failureStreaks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _automaticGate = new(1, 1);
    private readonly TimeSpan _automaticInterval;
    private long _lastAutomaticTicks;

    public MetadataGatewayState(MetadataRateLimitOptions options)
    {
        ApiLimiter = new TokenBucketRateLimiter(options.Api);
        ImageLimiter = new TokenBucketRateLimiter(options.Images);
        AniListLimiter = new TokenBucketRateLimiter(options.AniList);
        MangaDexApiLimiter = new TokenBucketRateLimiter(options.MangaDexApi);
        MangaDexImageLimiter = new TokenBucketRateLimiter(options.MangaDexImages);
        _automaticInterval = options.AutomaticInterval;
    }

    public RateLimiter AniListLimiter { get; }
    public RateLimiter MangaDexApiLimiter { get; }
    public RateLimiter MangaDexImageLimiter { get; }

    /// <summary>The API bucket of a provider (MangaUpdates' for an unknown id - it is refused before any call).</summary>
    public RateLimiter ApiLimiterOf(string providerId) => providerId switch
    {
        MetadataProviderAllowlist.AniList => AniListLimiter,
        MetadataProviderAllowlist.MangaDex => MangaDexApiLimiter,
        _ => ApiLimiter,
    };

    /// <summary>The image bucket of a provider.</summary>
    public RateLimiter ImageLimiterOf(string providerId) => providerId switch
    {
        MetadataProviderAllowlist.MangaDex => MangaDexImageLimiter,
        _ => ImageLimiter,
    };

    /// <summary>
    /// Waits until an automatic request may start: automatic requests are serialized
    /// process-wide and spaced by <see cref="MetadataRateLimitOptions.AutomaticInterval"/>.
    /// </summary>
    public async Task PaceAutomaticAsync(CancellationToken ct)
    {
        await _automaticGate.WaitAsync(ct);
        try
        {
            var last = Interlocked.Read(ref _lastAutomaticTicks);
            if (last != 0)
            {
                var wait = _automaticInterval - System.Diagnostics.Stopwatch.GetElapsedTime(last);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct);
            }
            Interlocked.Exchange(ref _lastAutomaticTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        finally
        {
            _automaticGate.Release();
        }
    }

    public RateLimiter ApiLimiter { get; }
    public RateLimiter ImageLimiter { get; }
    public SemaphoreSlim StateLock { get; } = new(1, 1);

    public int IncrementFailureStreak(string providerId = MetadataProviderAllowlist.MangaUpdates) =>
        _failureStreaks.AddOrUpdate(providerId, 1, (_, n) => n + 1);

    public void ResetFailureStreak(string providerId = MetadataProviderAllowlist.MangaUpdates) => _failureStreaks[providerId] = 0;

    public void Dispose()
    {
        ApiLimiter.Dispose();
        ImageLimiter.Dispose();
        AniListLimiter.Dispose();
        MangaDexApiLimiter.Dispose();
        MangaDexImageLimiter.Dispose();
        StateLock.Dispose();
        _automaticGate.Dispose();
    }
}
