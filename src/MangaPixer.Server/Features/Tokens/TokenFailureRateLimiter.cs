namespace com.lifepixer.mangapixer.Server.Features.Tokens;

using System.Collections.Concurrent;

/// <summary>
/// Counts FAILED token authentications per client address (modeled on <c>LoginRateLimiter</c>). Once an address reaches
/// <see cref="ApiTokenOptions.FailedAttemptsPerIp"/> failures in <see cref="ApiTokenOptions.FailedAttemptsWindow"/>, every bearer
/// request from it is refused with 429 before any database lookup until the window ends. Valid requests are never counted and
/// never limited here: every container on one Docker bridge shares an address (owner decision 3, 1.33.0).
/// </summary>
public sealed class TokenFailureRateLimiter
{
    /// <summary>Above this many tracked addresses, expired entries are dropped on the next failure.</summary>
    private const int PruneThreshold = 10_000;

    /// <summary>
    /// Hard cap on tracked addresses: while it is reached even after pruning, failures from NEW addresses are not tracked, so a
    /// flood from many addresses cannot grow the memory without bound (addresses already tracked keep being counted).
    /// </summary>
    private const int MaxTracked = 100_000;

    private readonly ConcurrentDictionary<string, Entry> _failures = new(StringComparer.Ordinal);
    private readonly ApiTokenOptions _options;
    private readonly TimeProvider _clock;

    public TokenFailureRateLimiter(ApiTokenOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    /// <summary>How long an address stays blocked after its last counted failure window started.</summary>
    public TimeSpan Window => _options.FailedAttemptsWindow;

    /// <summary>Null when <paramref name="address"/> may try a token; otherwise how long it must wait.</summary>
    public TimeSpan? RetryAfter(string address)
    {
        if (_options.FailedAttemptsDisabled || !_failures.TryGetValue(address, out var entry))
            return null;
        var remaining = entry.WindowStart + _options.FailedAttemptsWindow - _clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero || entry.Count < _options.FailedAttemptsPerIp)
            return null;
        return remaining;
    }

    /// <summary>
    /// Counts one failed authentication from <paramref name="address"/>. True when this failure is the one that blocks the
    /// address (so the caller logs the block once, not on every refused request).
    /// </summary>
    public bool RecordFailure(string address)
    {
        if (_options.FailedAttemptsDisabled)
            return false;
        var now = _clock.GetUtcNow();
        var window = _options.FailedAttemptsWindow;
        if (_failures.Count > PruneThreshold)
        {
            foreach (var (key, value) in _failures)
            {
                if (value.WindowStart + window <= now)
                    _failures.TryRemove(key, out _);
            }
            if (_failures.Count >= MaxTracked && !_failures.ContainsKey(address))
                return false;
        }
        var entry = _failures.AddOrUpdate(
            address,
            _ => new Entry(now, 1),
            (_, existing) => existing.WindowStart + window <= now
                ? new Entry(now, 1)
                : existing with { Count = existing.Count + 1 });
        return entry.Count == _options.FailedAttemptsPerIp;
    }

    private sealed record Entry(DateTimeOffset WindowStart, int Count);
}
