namespace com.lifepixer.mangapixer.Tests.Server.Features.Auth;

using com.lifepixer.mangapixer.Server.Features.Auth;
using Xunit;

/// <summary>
/// Unit tests for <see cref="LoginRateLimiter"/> covering the 1.16.0 RL lane's
/// two correctness fixes that live entirely inside this class: per-real-client
/// IP bucketing (once the caller passes the resolved client IP — see
/// LoginRateLimitHttpTests for the end-to-end forwarded-headers wiring) and
/// <see cref="LoginRateLimiter.GetRetryAfter"/> matching the actual sliding
/// window instead of a stale hardcoded duration.
/// </summary>
public sealed class LoginRateLimiterTests
{
    private static LoginRateLimitOptions Options(int maxAttemptsPerIp = 10, int maxAttemptsPerUser = 5, TimeSpan? window = null) =>
        new()
        {
            MaxAttemptsPerIp = maxAttemptsPerIp,
            MaxAttemptsPerUser = maxAttemptsPerUser,
            Window = window ?? TimeSpan.FromMinutes(5),
        };

    [Fact]
    public void AllowAttempt_TwoDifferentClientIps_GetIndependentBuckets()
    {
        var limiter = new LoginRateLimiter(Options(maxAttemptsPerIp: 2));

        // Exhaust the IP bucket for "203.0.113.1" with distinct usernames each
        // time so the per-username bucket (a separate limit) never trips.
        Assert.True(limiter.AllowAttempt("203.0.113.1", "user-a"));
        limiter.RecordFailure("203.0.113.1", "user-a");
        Assert.True(limiter.AllowAttempt("203.0.113.1", "user-b"));
        limiter.RecordFailure("203.0.113.1", "user-b");

        // A third attempt from the SAME IP is now blocked.
        Assert.False(limiter.AllowAttempt("203.0.113.1", "user-c"));

        // A DIFFERENT client IP is completely unaffected — its own bucket.
        Assert.True(limiter.AllowAttempt("203.0.113.2", "user-d"));
    }

    [Fact]
    public void GetRetryAfter_MatchesConfiguredWindow_NotAHardcodedFifteenMinutes()
    {
        var window = TimeSpan.FromMinutes(5);
        var limiter = new LoginRateLimiter(Options(maxAttemptsPerIp: 1, window: window));

        limiter.RecordFailure("203.0.113.5", "someone");
        Assert.False(limiter.AllowAttempt("203.0.113.5", "someone"));

        var retryAfter = limiter.GetRetryAfter("203.0.113.5", "someone");

        Assert.NotNull(retryAfter);
        // The block lifts when the actual 5-minute window ends — never longer
        // than the configured window (a stale 15-minute constant would fail
        // this bound), and not (spuriously) zero either.
        Assert.True(retryAfter > TimeSpan.Zero);
        Assert.True(retryAfter <= window);
    }

    [Fact]
    public void GetRetryAfter_ReflectsShortTestWindow_ProvingItIsNotFixedAtFifteenMinutes()
    {
        // A window far shorter than the old hardcoded 15 minutes makes the
        // regression impossible to miss: if GetRetryAfter ever reverts to the
        // constant, this assertion fails by four orders of magnitude.
        var window = TimeSpan.FromSeconds(30);
        var limiter = new LoginRateLimiter(Options(maxAttemptsPerIp: 1, window: window));

        limiter.RecordFailure("203.0.113.9", "someone");
        var retryAfter = limiter.GetRetryAfter("203.0.113.9", "someone");

        Assert.NotNull(retryAfter);
        Assert.True(retryAfter <= window);
    }

    [Fact]
    public void AllowAttempt_DifferentTargetKeys_DoNotShareABucket()
    {
        // Exercises the same "distinct key -> distinct bucket" mechanism the
        // activation endpoint now relies on (AuthController keys activation
        // attempts per hashed token instead of one shared "__activation__"
        // constant) using the rate limiter directly.
        var limiter = new LoginRateLimiter(Options(maxAttemptsPerUser: 1));

        Assert.True(limiter.AllowAttempt("203.0.113.7", "__activation__:token-aaa-hash"));
        limiter.RecordFailure("203.0.113.7", "__activation__:token-aaa-hash");

        Assert.False(limiter.AllowAttempt("203.0.113.7", "__activation__:token-aaa-hash"));
        Assert.True(limiter.AllowAttempt("203.0.113.7", "__activation__:token-bbb-hash"));
    }
}
