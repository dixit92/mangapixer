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
    /// (lowered to 30 while degraded, when its 429 + Retry-After takes over). Admin actions only.
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

    /// <summary>How long AniList is left alone after a 429 / 503 that carries no Retry-After.</summary>
    public TimeSpan AniListDefaultBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Minimum spacing between AUTOMATIC requests (stage 2: at most 1 request/s, so
    /// the 2 req/s API bucket always has room for an admin).
    /// </summary>
    public TimeSpan AutomaticInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Process-wide gateway state (singleton, 1.24.0 lane B2): the token buckets,
/// the lock that serializes writes of the persisted budget/backoff columns, and
/// the in-memory streak of consecutive 5xx/timeouts. 1.28.0: AniList's bucket and
/// its backoff, kept in memory (the persisted backoff columns stay MangaUpdates'
/// own, so an AniList 429 never pauses MangaUpdates; a restart forgets it, and
/// AniList answers a request that comes too early with another 429).
/// </summary>
public sealed class MetadataGatewayState : IDisposable
{
    private int _failureStreak;
    private readonly SemaphoreSlim _automaticGate = new(1, 1);
    private readonly TimeSpan _automaticInterval;
    private long _lastAutomaticTicks;

    public MetadataGatewayState(MetadataRateLimitOptions options)
    {
        ApiLimiter = new TokenBucketRateLimiter(options.Api);
        ImageLimiter = new TokenBucketRateLimiter(options.Images);
        AniListLimiter = new TokenBucketRateLimiter(options.AniList);
        AniListDefaultBackoff = options.AniListDefaultBackoff;
        _automaticInterval = options.AutomaticInterval;
    }

    private long _aniListBackoffUntilTicks;

    public RateLimiter AniListLimiter { get; }
    public TimeSpan AniListDefaultBackoff { get; }

    /// <summary>The time AniList asked us to wait until, or null.</summary>
    public DateTimeOffset? AniListBackoffUntil(DateTimeOffset now)
    {
        var ticks = Interlocked.Read(ref _aniListBackoffUntilTicks);
        return ticks > now.UtcTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    }

    public void SetAniListBackoff(DateTimeOffset until) => Interlocked.Exchange(ref _aniListBackoffUntilTicks, until.UtcTicks);

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

    public int IncrementFailureStreak() => Interlocked.Increment(ref _failureStreak);
    public void ResetFailureStreak() => Interlocked.Exchange(ref _failureStreak, 0);

    public void Dispose()
    {
        ApiLimiter.Dispose();
        ImageLimiter.Dispose();
        AniListLimiter.Dispose();
        StateLock.Dispose();
        _automaticGate.Dispose();
    }
}
