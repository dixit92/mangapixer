namespace com.lifepixer.mangapixer.Server.Features.Auth;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration extensions for auth services.
/// </summary>
public static class AuthServicesExtensions
{
    /// <summary>
    /// Registers all authentication and authorization services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="rateLimitDisabledOverride">
    /// Test-only override for <see cref="LoginRateLimitOptions.Disabled"/>,
    /// taking precedence over the bound <c>MangaPixer:Security:RateLimit:Disabled</c>
    /// configuration value when non-null. Used by the test host to disable rate
    /// limiting without touching process-wide configuration (see
    /// <c>TestHostStorageOverride</c>). Always null in production, where
    /// the configuration-bound value is used unchanged.
    /// </param>
    public static IServiceCollection AddMangaPixerAuth(
        this IServiceCollection services,
        string databasePath,
        bool? rateLimitDisabledOverride = null)
    {
        // IHttpContextAccessor — required by SignInManager
        services.AddHttpContextAccessor();

        // Configure DbContext
        var connectionString = DatabaseInitialization.BuildConnectionString(databasePath);
        services.AddDbContext<MangaPixerDbContext>(options =>
        {
            options.UseSqlite(connectionString);
        });
        services.AddScoped<MangaPixerDbContextFactory>();

        // Configure Identity with custom user store
        services.AddIdentityCore<UserEntity>(options =>
        {
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.User.RequireUniqueEmail = false;
            options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
        })
        .AddUserStore<MangaPixerUserStore>()
        .AddClaimsPrincipalFactory<Microsoft.AspNetCore.Identity.UserClaimsPrincipalFactory<UserEntity>>()
        .AddDefaultTokenProviders()
        .AddSignInManager();

        // IdentityOptions — required by SignInManager
        services.Configure<Microsoft.AspNetCore.Identity.IdentityOptions>(options => { });

        // Configure cookie authentication
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = ".MangaPixer.Auth";
                options.Cookie.HttpOnly = true;
                // FH (1.16.0): SameAsRequest emits the cookie Secure exactly when
                // Request.IsHttps is true. Request.IsHttps derives from Request.Scheme,
                // which the ForwardedHeaders middleware (enabled first in Program.cs)
                // rewrites to "https" for requests arriving via a TRUSTED reverse proxy
                // that sent X-Forwarded-Proto: https. So behind TLS the auth cookie is
                // Secure, while a genuine plain-http LAN request (no trusted forwarded
                // proto) still gets a non-Secure cookie and keeps working. Do NOT switch
                // this to Always: that would break the plain-http LAN scenario.
                options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;
                options.LoginPath = null; // API-only, no redirect
                options.AccessDeniedPath = null;
                options.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = async context =>
                    {
                        // Validate session ticket
                        var ticketId = context.Properties.Items.TryGetValue(".MangaPixer.ticket", out var t) ? t : null;
                        if (string.IsNullOrEmpty(ticketId))
                        {
                            context.RejectPrincipal();
                            return;
                        }

                        var sessionService = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
                        var user = await sessionService.ValidateSessionAsync(ticketId);
                        if (user is null)
                        {
                            context.RejectPrincipal();
                            return;
                        }

                        // Check forced password change
                        if (user.ForcePasswordChange && !context.HttpContext.Request.Path.StartsWithSegments("/api/v1/auth"))
                        {
                            context.RejectPrincipal();
                        }
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("Admin", policy => policy.RequireRole("admin"));
            options.AddPolicy("Reader", policy => policy.RequireRole("admin", "reader"));
        });

        // Register auth services
        services.AddScoped<SessionService>();
        services.AddScoped<SessionOptions>();
        services.AddScoped<LibraryAuthorizationService>();
        services.AddScoped<IncognitoAccessor>();
        services.AddScoped<LastAdminProtectionService>();
        services.AddSingleton<LoginRateLimiter>(sp =>
            new LoginRateLimiter(
                sp.GetRequiredService<LoginRateLimitOptions>(),
                sp.GetService<ILogger<LoginRateLimiter>>()));
        services.AddSingleton<LoginRateLimitOptions>(sp =>
        {
            var config = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
            var options = new LoginRateLimitOptions();
            config?.GetSection("MangaPixer:Security:RateLimit").Bind(options);
            if (rateLimitDisabledOverride is not null)
                options.Disabled = rateLimitDisabledOverride.Value;
            return options;
        });
        // First-run setup. No default credential is created at startup
        // (audit finding F2); the first admin is created only via
        // POST /api/v1/auth/setup while no user exists.
        services.AddScoped<FirstRunSetupService>();

        // Register authorization handlers
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, MangaPixerAuthorizationHandler>();

        return services;
    }
}
