namespace com.lifepixer.mangaplex.Server;

using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Features.Catalog;
using com.lifepixer.mangaplex.Server.Features.Reading;
using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.Server.Operations;
using com.lifepixer.mangaplex.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Server entry point. Wires up health checks, database, auth, media worker pool,
/// API controllers, and static file serving for the Angular SPA.
/// </summary>
public sealed partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Determine data root and database path
        var dataRoot = builder.Configuration["MangaPlex:Storage:DataRoot"];
        if (string.IsNullOrWhiteSpace(dataRoot))
            dataRoot = Path.Combine(builder.Environment.ContentRootPath, "data");
        Directory.CreateDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "mangaplex.db");

        var scratchRoot = builder.Configuration["MangaPlex:Storage:ScratchRoot"];
        if (string.IsNullOrWhiteSpace(scratchRoot))
            scratchRoot = Path.Combine(builder.Environment.ContentRootPath, "scratch");
        var cacheRoot = builder.Configuration["MangaPlex:Storage:CacheRoot"];
        if (string.IsNullOrWhiteSpace(cacheRoot))
            cacheRoot = Path.Combine(builder.Environment.ContentRootPath, "cache");
        var workerExe = builder.Configuration["Media:WorkerExecutablePath"];

        // Health checks
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("MangaPlex server is running"));

        // Auth + database (registers DbContext, Identity, cookie auth, auth services)
        builder.Services.AddMangaPlexAuth(databasePath);

        // Media worker pool
        builder.Services.AddMangaPlexMedia(options =>
        {
            options.ScratchRoot = scratchRoot;
            options.CacheRoot = cacheRoot;
            options.WorkerExecutablePath = workerExe;
        });

        // Catalog and reading services
        builder.Services.AddScoped<CatalogBrowseService>();
        builder.Services.AddScoped<ReadingStateService>();
        builder.Services.AddScoped<IdentityRelinkService>();

        // Operations services
        builder.Services.AddScoped<BackupService>();
        builder.Services.AddScoped<DiagnosticsService>();
        builder.Services.AddScoped<JobRecoveryService>();

        // Controllers
        builder.Services.AddControllers();

        var app = builder.Build();

        // Initialize database and bootstrap admin
        using (var scope = app.Services.CreateScope())
        {
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
                db.Database.EnsureCreated();
                DatabaseInitialization.ConfigureDatabaseAsync(db).GetAwaiter().GetResult();

                // Bootstrap default admin on first run
                var bootstrap = scope.ServiceProvider.GetRequiredService<DefaultAdminBootstrap>();
                bootstrap.BootstrapAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Log but don't crash — health checks should still work
                var logger = scope.ServiceProvider.GetService<ILogger<Program>>();
                logger?.LogWarning("Database initialization failed: {Error}. Health checks will still respond.", ex.GetType().Name);
            }
        }

        // Health endpoints (before auth so they're always accessible)
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/ready");

        // Auth middleware
        app.UseAuthentication();
        app.UseAuthorization();

        // API controllers
        app.MapControllers();

        // Static files — serve Angular bundle from wwwroot/
        var wwwrootPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
        if (Directory.Exists(wwwrootPath))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();

            // SPA fallback — serve index.html for non-API, non-file routes
            app.MapFallback(context =>
            {
                // Only serve index.html for non-API routes
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = 404;
                    return Task.CompletedTask;
                }

                var indexPath = Path.Combine(wwwrootPath, "index.html");
                if (File.Exists(indexPath))
                {
                    context.Response.ContentType = "text/html";
                    return context.Response.SendFileAsync(indexPath);
                }

                context.Response.StatusCode = 404;
                return Task.CompletedTask;
            });
        }

        app.Run();
    }
}
