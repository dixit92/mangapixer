namespace com.lifepixer.mangaplex.Server.Features.Auth;

using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
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
    public static IServiceCollection AddMangaPlexAuth(this IServiceCollection services, string databasePath)
    {
        // IHttpContextAccessor — required by SignInManager
        services.AddHttpContextAccessor();

        // Configure DbContext
        var connectionString = DatabaseInitialization.BuildConnectionString(databasePath);
        services.AddDbContext<MangaPlexDbContext>(options =>
        {
            options.UseSqlite(connectionString);
        });
        services.AddScoped<MangaPlexDbContextFactory>();

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
        .AddUserStore<MangaPlexUserStore>()
        .AddClaimsPrincipalFactory<Microsoft.AspNetCore.Identity.UserClaimsPrincipalFactory<UserEntity>>()
        .AddDefaultTokenProviders()
        .AddSignInManager();

        // IdentityOptions — required by SignInManager
        services.Configure<Microsoft.AspNetCore.Identity.IdentityOptions>(options => { });

        // Configure cookie authentication
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = ".MangaPlex.Auth";
                options.Cookie.HttpOnly = true;
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
                        var ticketId = context.Properties.Items.TryGetValue(".MangaPlex.ticket", out var t) ? t : null;
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
        services.AddScoped<LastAdminProtectionService>();
        services.AddSingleton<LoginRateLimiter>(sp =>
            new LoginRateLimiter(
                sp.GetRequiredService<LoginRateLimitOptions>(),
                sp.GetService<ILogger<LoginRateLimiter>>()));
        services.AddSingleton<LoginRateLimitOptions>(sp =>
        {
            var config = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
            var options = new LoginRateLimitOptions();
            config?.GetSection("MangaPlex:Security:RateLimit").Bind(options);
            return options;
        });
        // First-run setup. No default credential is created at startup
        // (audit finding F2); the first admin is created only via
        // POST /api/v1/auth/setup while no user exists.
        services.AddScoped<FirstRunSetupService>();

        // Register authorization handlers
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, MangaPlexAuthorizationHandler>();

        return services;
    }
}
