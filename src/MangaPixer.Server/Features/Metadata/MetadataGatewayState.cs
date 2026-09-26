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
}

/// <summary>
/// Process-wide gateway state (singleton, 1.24.0 lane B2): the two token buckets,
/// the lock that serializes writes of the persisted budget/backoff columns, and
/// the in-memory streak of consecutive 5xx/timeouts.
/// </summary>
public sealed class MetadataGatewayState : IDisposable
{
    private int _failureStreak;

    public MetadataGatewayState(MetadataRateLimitOptions options)
    {
        ApiLimiter = new TokenBucketRateLimiter(options.Api);
        ImageLimiter = new TokenBucketRateLimiter(options.Images);
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
        StateLock.Dispose();
    }
}
