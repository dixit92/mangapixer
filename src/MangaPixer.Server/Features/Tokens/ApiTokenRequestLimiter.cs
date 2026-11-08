namespace com.lifepixer.mangapixer.Server.Features.Tokens;

using System.Globalization;
using System.Threading.RateLimiting;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// The export's per-token request ceiling (1.33.0): a fixed one-minute window of <see cref="ApiTokenOptions.RequestsPerMinute"/>
/// requests per token id, answered with 429 and <c>Retry-After</c> beyond it. Only a principal the token scheme authenticated carries a
/// token id, so cookie requests are never limited. Runs after authorization (<c>Program</c>), when that principal is known.
/// 1.52.0: a DI singleton instead of ASP.NET's <c>AddRateLimiter</c> / <c>UseRateLimiter</c>. That middleware never disposes its
/// limiters, whose replenishment timers then live as long as the process - harmless for the one host in production, but in the test
/// suite each timer kept a whole disposed host reachable. The container now disposes this limiter, and its timer, with the host.
/// </summary>
public sealed class ApiTokenRequestLimiter : IDisposable, IAsyncDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter;
    private readonly ILogger _logger;

    public ApiTokenRequestLimiter(ApiTokenOptions options, ILoggerFactory loggers)
    {
        // The category of the 1.33.0 rejection line, so the Auth debug switch still covers it.
        _logger = loggers.CreateLogger(typeof(ApiTokenService));
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var tokenId = context.User.FindFirst(ApiTokenClaims.TokenId)?.Value;
            if (tokenId is null || options.RequestsPerMinute <= 0)
                return RateLimitPartition.GetNoLimiter(string.Empty);
            return RateLimitPartition.GetFixedWindowLimiter(tokenId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.RequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });
    }

    /// <summary>The middleware step: passes the request on while its token has permits left; otherwise answers 429.</summary>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // QueueLimit 0: a permit is granted now or never, so the synchronous attempt is the whole decision.
        using var lease = _limiter.AttemptAcquire(context);
        if (lease.IsAcquired)
        {
            await next(context);
            return;
        }

        var seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 60;
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        // Debug: a client stuck in a loop would otherwise write one line per refused request.
        _logger.LogDebug(LogEvents.Auth.ApiTokenRequestsLimited, "API token {TokenId} over its request ceiling; retry in {RetryAfter}s",
            context.User.FindFirst(ApiTokenClaims.TokenId)?.Value ?? "-", seconds);
        await context.Response.WriteAsJsonAsync(new ApiError
        {
            Error = "rate_limited",
            Message = "Too many requests with this token. Try again later.",
        }, context.RequestAborted);
    }

    public void Dispose() => _limiter.Dispose();

    public ValueTask DisposeAsync() => _limiter.DisposeAsync();
}
