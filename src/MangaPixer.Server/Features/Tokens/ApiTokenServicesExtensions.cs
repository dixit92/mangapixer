namespace com.lifepixer.mangapixer.Server.Features.Tokens;

using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Export;
using Microsoft.AspNetCore.Authentication;

/// <summary>DI registration of personal access tokens (1.33.0; the scan request's cooldown 1.36.0).</summary>
public static class ApiTokenServicesExtensions
{
    /// <summary>
    /// Registers the token scheme, <see cref="ApiTokenService"/>, <see cref="TokenFailureRateLimiter"/> and the per-token request
    /// ceiling (<see cref="ApiTokenRequestLimiter"/>, whose only limited partitions are token ids; <c>app.UseApiTokenRequestLimit()</c>
    /// runs it after authorization, when an export request's token principal is known). Cookie requests are never limited by it.
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
        // 1.36.0: the per-library cooldown of token-requested scans (ExportScanController).
        services.AddSingleton<TokenScanCooldown>();

        // AddAuthentication() without a scheme name leaves the default (the cookie) unchanged.
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ExportApi.TokenScheme, displayName: null, _ => { });

        // The per-token request ceiling; the container disposes it (and its replenishment timer) with the host - see the class.
        services.AddSingleton<ApiTokenRequestLimiter>();

        return services;
    }

    /// <summary>Adds the per-token request ceiling to the pipeline. Call it after authorization (the token principal is known then).</summary>
    public static IApplicationBuilder UseApiTokenRequestLimit(this IApplicationBuilder app) =>
        app.Use((context, next) => context.RequestServices.GetRequiredService<ApiTokenRequestLimiter>().InvokeAsync(context, next));
}
