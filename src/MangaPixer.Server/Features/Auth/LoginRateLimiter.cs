namespace com.lifepixer.mangapixer.Server.Features.Auth;

using System.Collections.Concurrent;
using com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// Simple in-memory rate limiter for login attempts.
/// Limits per-IP and per-username to prevent brute-force attacks.
/// In production, this should be backed by distributed cache for multi-instance deployments.
/// <para>
/// 1.49.0 lane M: keys whose window has ended are dropped (an ended window counts exactly like no entry - the next attempt starts a
/// new window - so dropping one changes no answer). Before, only a successful login removed a key, so every failed attempt with a new
/// address or user name stayed in memory for the life of the process.
/// </para>
/// </summary>
public sealed class LoginRateLimiter
{
    /// <summary>Above this many tracked keys (addresses and user names together), ended windows are dropped on a failed attempt.</summary>
    internal const int PruneThreshold = 1024;

    /// <summary>At most one sweep per this interval, so a flood of live keys above the threshold does not scan them on every attempt.</summary>
    internal static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, RateEntry> _ipAttempts = new();
    private readonly ConcurrentDictionary<string, RateEntry> _userAttempts = new();
    private readonly LoginRateLimitOptions _options;
    private readonly ILogger<LoginRateLimiter>? _logger;
    private readonly TimeProvider _clock;
    private long _lastPruneTicks = long.MinValue;

    public LoginRateLimiter(LoginRateLimitOptions? options = null, ILogger<LoginRateLimiter>? logger = null, TimeProvider? clock = null)
    {
        _options = options ?? new LoginRateLimitOptions();
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Tracked keys, addresses and user names together (tests).</summary>
    internal int TrackedKeys => _ipAttempts.Count + _userAttempts.Count;

    /// <summary>
    /// Records a failed login attempt and checks if the rate limit has been exceeded.
    /// Returns true if the request is allowed, false if rate-limited.
    /// </summary>
    public bool AllowAttempt(string ipAddress, string username)
    {
        if (_options.Disabled)
            return true;

        var now = _clock.GetUtcNow();

        if (!IsAllowed(_ipAttempts, $"ip:{ipAddress}", now, _options.MaxAttemptsPerIp, _options.Window))
        {
            // Security-relevant event. Never log the IP address (privacy
            // invariant) — the trigger kind and retry window are enough.
            _logger?.LogWarning(LogEvents.Auth.RateLimitedByIp,
                "Login rate-limited by {Trigger} limit for user {UserName}; retry window {RetryAfter}s",
                "ip", username, GetRetryAfter(ipAddress, username)?.TotalSeconds);
            return false;
        }

        if (!IsAllowed(_userAttempts, $"user:{username.ToLowerInvariant()}", now, _options.MaxAttemptsPerUser, _options.Window))
        {
            _logger?.LogWarning(LogEvents.Auth.RateLimitedByUsername,
                "Login rate-limited by {Trigger} limit for user {UserName}; retry window {RetryAfter}s",
                "username", username, GetRetryAfter(ipAddress, username)?.TotalSeconds);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records a successful login, resetting the rate limit counters.
    /// </summary>
    public void RecordSuccess(string ipAddress, string username)
    {
        _ipAttempts.TryRemove($"ip:{ipAddress}", out _);
        _userAttempts.TryRemove($"user:{username.ToLowerInvariant()}", out _);
    }

    /// <summary>
    /// Records a failed login attempt.
    /// </summary>
    public void RecordFailure(string ipAddress, string username)
    {
        var now = _clock.GetUtcNow();
        PruneEndedWindows(now);
        RecordFailure(_ipAttempts, $"ip:{ipAddress}", now, _options.Window);
        RecordFailure(_userAttempts, $"user:{username.ToLowerInvariant()}", now, _options.Window);
    }

    /// <summary>
    /// Returns the time until the rate limit resets for a given IP/username, or null if not limited.
    /// </summary>
    public TimeSpan? GetRetryAfter(string ipAddress, string username)
    {
        var now = _clock.GetUtcNow();
        var ipRetry = GetRetryAfter(_ipAttempts, $"ip:{ipAddress}", now, _options.MaxAttemptsPerIp, _options.Window);
        var userRetry = GetRetryAfter(_userAttempts, $"user:{username.ToLowerInvariant()}", now, _options.MaxAttemptsPerUser, _options.Window);

        if (ipRetry is null && userRetry is null) return null;
        if (ipRetry is null) return userRetry;
        if (userRetry is null) return ipRetry;
        return ipRetry > userRetry ? ipRetry : userRetry;
    }

    /// <summary>
    /// Drops the keys whose window ended - only above <see cref="PruneThreshold"/> keys and at most once per <see cref="PruneInterval"/>.
    /// A key is removed only if it still holds the ended entry that was read (a concurrent failure that renewed it keeps it).
    /// </summary>
    private void PruneEndedWindows(DateTimeOffset now)
    {
        if (_ipAttempts.Count + _userAttempts.Count <= PruneThreshold)
            return;
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (last != long.MinValue && now.UtcTicks - last < PruneInterval.Ticks)
            return;
        if (Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) != last)
            return;
        Prune(_ipAttempts, now, _options.Window);
        Prune(_userAttempts, now, _options.Window);
    }

    private static void Prune(ConcurrentDictionary<string, RateEntry> dict, DateTimeOffset now, TimeSpan window)
    {
        foreach (var pair in dict)
        {
            if (pair.Value.WindowStart + window <= now)
                dict.TryRemove(pair);
        }
    }

    private static bool IsAllowed(ConcurrentDictionary<string, RateEntry> dict, string key, DateTimeOffset now, int maxAttempts, TimeSpan window)
    {
        if (dict.TryGetValue(key, out var entry))
        {
            if (entry.WindowStart + window > now)
            {
                return entry.AttemptCount < maxAttempts;
            }
            // Window expired, reset
            dict.TryUpdate(key, new RateEntry { WindowStart = now, AttemptCount = 0 }, entry);
        }
        return true;
    }

    private static void RecordFailure(ConcurrentDictionary<string, RateEntry> dict, string key, DateTimeOffset now, TimeSpan window)
    {
        dict.AddOrUpdate(
            key,
            _ => new RateEntry { WindowStart = now, AttemptCount = 1 },
            (_, existing) =>
            {
                if (existing.WindowStart + window <= now)
                    return new RateEntry { WindowStart = now, AttemptCount = 1 };
                return new RateEntry { WindowStart = existing.WindowStart, AttemptCount = existing.AttemptCount + 1 };
            });
    }

    private static TimeSpan? GetRetryAfter(ConcurrentDictionary<string, RateEntry> dict, string key, DateTimeOffset now, int maxAttempts, TimeSpan window)
    {
        if (dict.TryGetValue(key, out var entry))
        {
            if (entry.AttemptCount >= maxAttempts)
            {
                // The block actually lifts when the sliding window that got the
                // caller rate-limited ends, not some independent fixed duration —
                // otherwise a client is told to wait longer than the real block.
                var resetTime = entry.WindowStart + window;
                var remaining = resetTime - now;
                return remaining > TimeSpan.Zero ? remaining : null;
            }
        }
        return null;
    }

    private sealed record RateEntry
    {
        public DateTimeOffset WindowStart { get; init; }
        public int AttemptCount { get; init; }
    }
}

/// <summary>
/// Rate limit options for login attempts.
/// </summary>
public sealed class LoginRateLimitOptions
{
    public int MaxAttemptsPerIp { get; set; } = 10;
    public int MaxAttemptsPerUser { get; set; } = 5;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When true, rate limiting is disabled entirely. Used in test environments
    /// where many login attempts are expected.
    /// </summary>
    public bool Disabled { get; set; }
}
