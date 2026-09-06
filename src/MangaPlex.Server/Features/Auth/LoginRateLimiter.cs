namespace com.lifepixer.mangaplex.Server.Features.Auth;

using System.Collections.Concurrent;

/// <summary>
/// Simple in-memory rate limiter for login attempts.
/// Limits per-IP and per-username to prevent brute-force attacks.
/// In production, this should be backed by distributed cache for multi-instance deployments.
/// </summary>
public sealed class LoginRateLimiter
{
    private readonly ConcurrentDictionary<string, RateEntry> _ipAttempts = new();
    private readonly ConcurrentDictionary<string, RateEntry> _userAttempts = new();
    private readonly LoginRateLimitOptions _options;

    public LoginRateLimiter(LoginRateLimitOptions? options = null)
    {
        _options = options ?? new LoginRateLimitOptions();
    }

    /// <summary>
    /// Records a failed login attempt and checks if the rate limit has been exceeded.
    /// Returns true if the request is allowed, false if rate-limited.
    /// </summary>
    public bool AllowAttempt(string ipAddress, string username)
    {
        if (_options.Disabled)
            return true;

        var now = DateTimeOffset.UtcNow;

        if (!IsAllowed(_ipAttempts, $"ip:{ipAddress}", now, _options.MaxAttemptsPerIp, _options.Window))
            return false;

        if (!IsAllowed(_userAttempts, $"user:{username.ToLowerInvariant()}", now, _options.MaxAttemptsPerUser, _options.Window))
            return false;

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
        var now = DateTimeOffset.UtcNow;
        RecordFailure(_ipAttempts, $"ip:{ipAddress}", now, _options.Window);
        RecordFailure(_userAttempts, $"user:{username.ToLowerInvariant()}", now, _options.Window);
    }

    /// <summary>
    /// Returns the time until the rate limit resets for a given IP/username, or null if not limited.
    /// </summary>
    public TimeSpan? GetRetryAfter(string ipAddress, string username)
    {
        var now = DateTimeOffset.UtcNow;
        var ipRetry = GetRetryAfter(_ipAttempts, $"ip:{ipAddress}", now, _options.MaxAttemptsPerIp);
        var userRetry = GetRetryAfter(_userAttempts, $"user:{username.ToLowerInvariant()}", now, _options.MaxAttemptsPerUser);

        if (ipRetry is null && userRetry is null) return null;
        if (ipRetry is null) return userRetry;
        if (userRetry is null) return ipRetry;
        return ipRetry > userRetry ? ipRetry : userRetry;
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

    private static TimeSpan? GetRetryAfter(ConcurrentDictionary<string, RateEntry> dict, string key, DateTimeOffset now, int maxAttempts)
    {
        if (dict.TryGetValue(key, out var entry))
        {
            if (entry.AttemptCount >= maxAttempts)
            {
                var resetTime = entry.WindowStart + TimeSpan.FromMinutes(15); // Lock for 15 minutes after exceeding
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
