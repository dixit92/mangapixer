namespace com.lifepixer.mangaplex.Server;

using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Features.Catalog;
using com.lifepixer.mangaplex.Server.Features.Reading;
using com.lifepixer.mangaplex.Server.Hosting;
using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.Server.Operations;
using com.lifepixer.mangaplex.Server.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Core;
using Serilog.Events;

/// <summary>
/// Server entry point. Wires up Serilog, health checks, database, auth,
/// anti-forgery, Data Protection, media worker pool, hosted lifecycle
/// services, API controllers, and static file serving for the Angular SPA.
/// </summary>
public sealed partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Resolve storage roots from builder.Configuration so that
        // WebApplicationFactory.ConfigureAppConfiguration is visible.
        var dataRoot = ResolveRoot(builder.Configuration, "MangaPlex:Storage:DataRoot", "data");
        var scratchRoot = ResolveRoot(builder.Configuration, "MangaPlex:Storage:ScratchRoot", "scratch");
        var cacheRoot = ResolveRoot(builder.Configuration, "MangaPlex:Storage:CacheRoot", "cache");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(scratchRoot);
        Directory.CreateDirectory(cacheRoot);

        var logsRoot = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logsRoot);
        var keysRoot = Path.Combine(dataRoot, "keys");
        Directory.CreateDirectory(keysRoot);

        var databasePath = Path.Combine(dataRoot, "mangaplex.db");
        var workerExe = builder.Configuration["Media:WorkerExecutablePath"];

        // Serilog bootstrap — plain text console for both container and dev.
        // The default level is controlled by a LoggingLevelSwitch so an admin
        // can raise/lower verbosity at runtime without a restart (section 9).
        // The switch resets to Information on restart (ephemeral by design).
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        var logConfig = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.WithProperty("Application", "MangaPlex")
            .Enrich.With<RedactingDestructuringPolicy>();

        logConfig.WriteTo.Console(
            outputTemplate: "{Timestamp:O} [{Level:u}] {SourceContext} {Message:lj}{NewLine}{Exception}");

        logConfig.WriteTo.File(
            Path.Combine(logsRoot, "mangaplex-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            fileSizeLimitBytes: 20 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            outputTemplate: "{Timestamp:O} [{Level:u}] {SourceContext} {Message:lj}{NewLine}{Exception}");

        Log.Logger = logConfig.CreateLogger();

        try
        {
            builder.Host.UseSerilog();

            // Health checks
            builder.Services.AddHealthChecks()
                .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("MangaPlex server is running"));

            // Auth + database (registers DbContext, Identity, cookie auth, auth services)
            builder.Services.AddMangaPlexAuth(databasePath);

            // Media worker pool, scheduler, cache, scratch
            // Storage budgets are admin-configurable (bytes). Defaults live in
            // WorkerPoolOptions (1 GiB cache / 1 GiB scratch); override via
            // MangaPlex:Storage:CacheBudgetBytes / ScratchBudgetBytes.
            var cacheBudget = ReadByteBudget(builder.Configuration, "MangaPlex:Storage:CacheBudgetBytes");
            var scratchBudget = ReadByteBudget(builder.Configuration, "MangaPlex:Storage:ScratchBudgetBytes");
            builder.Services.AddMangaPlexMedia(options =>
            {
                options.ScratchRoot = scratchRoot;
                options.CacheRoot = cacheRoot;
                options.WorkerExecutablePath = workerExe;
                if (cacheBudget is > 0) options.CacheBudgetBytes = cacheBudget.Value;
                if (scratchBudget is > 0) options.ScratchBudgetBytes = scratchBudget.Value;
            });

            // Startup configuration logging (gap 8.3.6). Logs existence and
            // budgets only — never absolute paths, per the privacy invariant.
            Log.Logger.Information("Storage roots initialized: data={DataExists}, cache={CacheExists}, scratch={ScratchExists}",
                Directory.Exists(dataRoot), Directory.Exists(cacheRoot), Directory.Exists(scratchRoot));
            Log.Logger.Information("Storage budgets: cache={CacheBudget}, scratch={ScratchBudget}",
                cacheBudget is > 0 ? cacheBudget.Value.ToString() : "default",
                scratchBudget is > 0 ? scratchBudget.Value.ToString() : "default");
            Log.Logger.Information("Worker executable: {Status}",
                string.IsNullOrWhiteSpace(workerExe) ? "auto-discovery" : "configured");

            // Hosted lifecycle services + storage/scanning/page-delivery registrations
            builder.Services.AddMangaPlexHosting();

            // Catalog and reading services
            builder.Services.AddScoped<CatalogBrowseService>();
            builder.Services.AddScoped<ReadingStateService>();
            builder.Services.AddScoped<CatalogIdResolver>();
            builder.Services.AddScoped<ReaderModeResolver>();

            // Operations services
            builder.Services.AddScoped<BackupService>();
            builder.Services.AddScoped<DiagnosticsService>();

            // Log-level control (section 9) — singleton so the switch survives
            // across requests and mutates the live Serilog pipeline.
            builder.Services.AddSingleton(levelSwitch);
            builder.Services.AddSingleton<LogLevelSettingsService>();

            // Data Protection — persist keys in application-owned data so
            // cookies survive container recreate. Set a stable application name
            // so the key ring is not tied to the content root path.
            // On Windows, DPAPI encrypts keys at rest. On Linux there is no
            // DPAPI; keys are stored unencrypted inside the private data root
            // with owner-only permissions (set by entrypoint.sh). This is
            // disclosed honestly rather than claiming encryption at rest
            // (audit defect D16).
            var dataProtection = builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keysRoot))
                .SetApplicationName("MangaPlex");

            if (OperatingSystem.IsWindows())
            {
                dataProtection.ProtectKeysWithDpapi();
                Log.Logger.Information("Data Protection keys encrypted at rest with DPAPI");
            }
            else
            {
                Log.Logger.Information(
                    "Data Protection keys stored unencrypted inside the private data root " +
                    "with owner-only permissions (Linux has no DPAPI; entrypoint.sh chmod 700)");
            }

            // Anti-forgery — double-submit token via X-MangaPlex-Csrf header.
            // The cookie is issued by GET /auth/csrf; unsafe methods must echo
            // the header. Login is exempt (the token is obtained from /csrf
            // immediately before). GET /auth/csrf is exempt (it issues the token).
            //
            // Cookie is HttpOnly (audit defect D2): the client obtains the token
            // from GET /auth/csrf as a JSON response body, not by reading the
            // cookie. This prevents XSS from stealing the cookie value.
            builder.Services.AddAntiforgery(options =>
            {
                options.HeaderName = "X-MangaPlex-Csrf";
                options.Cookie.Name = ".MangaPlex.Csrf";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
            });

            // MVC controllers with a global auto-validate antiforgery filter.
            // [IgnoreAntiforgeryToken] opts specific actions out (csrf issuer, login).
            // AddControllersWithViews is required because AutoValidateAntiforgeryTokenAttribute
            // is part of the view features pipeline.
            builder.Services.AddControllersWithViews(options =>
            {
                options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
            })
            .AddJsonOptions(options =>
            {
                // Serialize enums as their string names ("Folder", "Available",
                // "InProgress", …) rather than integers. The API contract and the
                // Angular client are string-based; without this, enums leak as
                // opaque integers and client comparisons (node.kind === 'Folder')
                // silently fail.
                options.JsonSerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter());
            });

            // OpenAPI document (audit defect D35). The document contains no
            // private data — routes, DTO shapes, and error codes only.
            // Served unauthenticated at /openapi/v1.json so the contract drift
            // test and `openapi-typescript` can fetch it without credentials.
            builder.Services.AddOpenApi();

            var app = builder.Build();

            // Migrate the schema to the latest EF migration. This is data-critical:
            // a failure must STOP startup (fail-fast) rather than serve a
            // half-migrated database, so it is deliberately OUTSIDE the
            // degrade-to-health-only catch used for the rest of initialization. Any
            // pre-migration backup taken by the orchestrator is preserved on the
            // data volume for recovery.
            using (var migrateScope = app.Services.CreateScope())
            {
                var db = migrateScope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
                var backup = migrateScope.ServiceProvider.GetRequiredService<BackupService>();
                var dbLogger = migrateScope.ServiceProvider
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("MangaPlex.DatabaseInitialization");
                DatabaseInitialization.MigrateToLatestAsync(
                    db, dataRoot,
                    async path => (await backup.BackupAsync(path)).Succeeded,
                    dbLogger).GetAwaiter().GetResult();
            }

            // Post-migration configuration (idempotent PRAGMAs + FTS) and bootstrap.
            // These may degrade to health-only if they fail.
            using (var scope = app.Services.CreateScope())
            {
                try
                {
                    var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
                    DatabaseInitialization.ConfigureDatabaseAsync(db).GetAwaiter().GetResult();

                    // No default credential is created (audit finding F2). On a
                    // fresh instance the first admin is created by the user via
                    // POST /api/v1/auth/setup; just log that setup is pending.
                    var setup = scope.ServiceProvider.GetRequiredService<FirstRunSetupService>();
                    if (setup.IsSetupRequiredAsync().GetAwaiter().GetResult())
                        Log.Logger.Information("First-run setup required: no users exist. Create the admin via the setup screen.");
                }
                catch (Exception ex)
                {
                    Log.Logger.Warning(ex, "Database initialization failed; health checks will still respond");
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

            // OpenAPI endpoint (audit defect D35). Served at /openapi/v1.json.
            // No private data is exposed — only route shapes and DTO schemas.
            app.MapOpenApi("/openapi/v1.json");

            // Static files — serve Angular bundle from wwwroot/
            var wwwrootPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
            if (Directory.Exists(wwwrootPath))
            {
                app.UseDefaultFiles();
                app.UseStaticFiles();

                // SPA fallback — serve index.html for non-API, non-file routes
                app.MapFallback(context =>
                {
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
        catch (Exception ex)
        {
            Log.Logger.Fatal(ex, "MangaPlex server terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static string ResolveRoot(IConfiguration configuration, string key, string defaultName)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return Path.Combine(AppContext.BaseDirectory, defaultName);
        return Path.GetFullPath(value);
    }

    /// <summary>
    /// Reads an optional byte budget from config. Returns null when unset/invalid
    /// so the caller keeps the WorkerPoolOptions default. Accepts a plain byte
    /// count (e.g. 268435456) — deployments can compute from MiB/GiB as needed.
    /// </summary>
    private static long? ReadByteBudget(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) return null;
        return long.TryParse(value.Trim(), out var bytes) && bytes > 0 ? bytes : null;
    }
}
