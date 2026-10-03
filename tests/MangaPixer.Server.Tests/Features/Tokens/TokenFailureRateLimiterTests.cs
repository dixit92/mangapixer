namespace com.lifepixer.mangapixer.Tests.Server.Features.Tokens;

using com.lifepixer.mangapixer.Server.Features.Tokens;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Xunit;

/// <summary>Unit tests of the per-address failed token attempt limiter (1.33.0), on a manual clock.</summary>
public sealed class TokenFailureRateLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static (TokenFailureRateLimiter Limiter, ManualTime Clock) Create(bool disabled = false)
    {
        var clock = new ManualTime(Start);
        var options = new ApiTokenOptions { FailedAttemptsPerIp = 3, FailedAttemptsWindow = TimeSpan.FromMinutes(5), FailedAttemptsDisabled = disabled };
        return (new TokenFailureRateLimiter(options, clock), clock);
    }

    [Fact]
    public void BlocksAnAddress_AfterTheLimit_UntilTheWindowEnds()
    {
        var (limiter, clock) = Create();
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(limiter.RetryAfter("203.0.113.1"));
            limiter.RecordFailure("203.0.113.1");
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(4), limiter.RetryAfter("203.0.113.1"));
        // Another address is not affected.
        Assert.Null(limiter.RetryAfter("203.0.113.2"));

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(limiter.RetryAfter("203.0.113.1"));

        // A new window starts with the next failure.
        limiter.RecordFailure("203.0.113.1");
        Assert.Null(limiter.RetryAfter("203.0.113.1"));
    }

    [Fact]
    public void Disabled_NeverBlocks()
    {
        var (limiter, _) = Create(disabled: true);
        for (var i = 0; i < 10; i++)
            limiter.RecordFailure("203.0.113.1");
        Assert.Null(limiter.RetryAfter("203.0.113.1"));
    }
}
