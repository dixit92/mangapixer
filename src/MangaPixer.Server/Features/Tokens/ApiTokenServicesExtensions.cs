namespace com.lifepixer.mangapixer.Server.Features.Tokens;

using System.Globalization;
using System.Threading.RateLimiting;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>DI registration of personal access tokens (1.33.0).</summary>
public static class ApiTokenServicesExtensions
{
    /// <summary>
    /// Registers the token scheme, <see cref="ApiTokenService"/>, <see cref="TokenFailureRateLimiter"/> and the per-token request
    /// ceiling (a global ASP.NET rate limiter whose only limited partitions are token ids; <c>app.UseRateLimiter()</c> runs it
    /// after authorization, when an export request's token principal is known). Cookie requests are never limited by it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="rateLimitDisabledOverride">The test host's switch for the login limiter; it turns the failed-attempt limiter off too.</param>
    public static IServiceCollection AddMangaPixerApiTokens(this IServiceCollection services, bool? rateLimitDisabledOverride)
    {
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var options = new ApiTokenOptions();
            config.GetSection(ApiTokenOptions.SectionName).Bind(options);
            options.FailedAttemptsDisabled = rateLimitDisabledOverride
                ?? config.GetValue<bool>("MangaPixer:Security:RateLimit:Disabled");
            return options;
        });
        services.AddSingleton<TokenFailureRateLimiter>();
        services.AddScoped<ApiTokenService>();

        // AddAuthentication() without a scheme name leaves the default (the cookie) unchanged.
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ExportApi.TokenScheme, displayName: null, _ => { });

        services.AddRateLimiter(limiter =>
        {
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // The claim exists only on a principal the token scheme authenticated, i.e. on an export request with a token.
                var tokenId = context.User.FindFirst(ApiTokenClaims.TokenId)?.Value;
                var perMinute = context.RequestServices.GetRequiredService<ApiTokenOptions>().RequestsPerMinute;
                if (tokenId is null || perMinute <= 0)
                    return RateLimitPartition.GetNoLimiter(string.Empty);
                return RateLimitPartition.GetFixedWindowLimiter(tokenId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = perMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (rejected, ct) =>
            {
                var http = rejected.HttpContext;
                var seconds = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : 60;
                http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiTokenService))
                    // Debug: a client stuck in a loop would otherwise write one line per refused request.
                    .LogDebug(LogEvents.Auth.ApiTokenRequestsLimited, "API token {TokenId} over its request ceiling; retry in {RetryAfter}s",
                        http.User.FindFirst(ApiTokenClaims.TokenId)?.Value ?? "-", seconds);
                await http.Response.WriteAsJsonAsync(new ApiError
                {
                    Error = "rate_limited",
                    Message = "Too many requests with this token. Try again later.",
                }, ct);
            };
        });

        return services;
    }
}
