namespace com.lifepixer.mangapixer.Server;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Home;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
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
    /// <summary>
    /// Single source of truth for the product name embedded in on-disk paths
    /// and display strings (Windows LOCALAPPDATA data-root folder, Data
    /// Protection application name). A future product rename only needs to
    /// change this constant.
    /// </summary>
    private const string ProductName = "MangaPixer";

    public static void Main(string[] args)
    {
        // WebApplication.CreateBuilder defaults ContentRootPath to the
        // process's current working directory, which drives where
        // appsettings.{Environment}.json and wwwroot/ are discovered. The
        // container sets its WORKDIR to match (see deploy/Dockerfile), so
        // this is a no-op there. On Windows, no launcher is guaranteed to set
        // the working directory to the exe's own folder (a shortcut with no
        // "Start in", or a future tray child-process spawn) — pinning the
        // content root to the exe's own directory keeps the Windows
        // distribution's bundled appsettings.Production.json and wwwroot/
        // (both published alongside MangaPixer.Server.exe) discoverable
        // regardless of the launcher's working directory.
        var builder = OperatingSystem.IsWindows()
            ? WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory })
            : WebApplication.CreateBuilder(args);

        // Resolve storage roots from builder.Configuration, with a test-only
        // ambient override checked first (used by the test host to isolate storage per-run).
        // See the remarks on TestHostStorageOverride for why the override
        // exists: WebApplicationFactory's ConfigureAppConfiguration does not
        // reach these reads in time for this minimal-hosting entry point, and
        // a process-global environment variable would race under parallel
        // test-host boots. TestHostStorageOverride.Current is always null in
        // production, so this is a no-op outside tests.
        var storageOverride = TestHostStorageOverride.Current;
        var dataRoot = storageOverride?.DataRoot
            ?? ResolveRoot(builder.Configuration, "MangaPixer:Storage:DataRoot", "data");
        var scratchRoot = storageOverride?.ScratchRoot
            ?? ResolveRoot(builder.Configuration, "MangaPixer:Storage:ScratchRoot", "scratch");
        var cacheRoot = storageOverride?.CacheRoot
            ?? ResolveRoot(builder.Configuration, "MangaPixer:Storage:CacheRoot", "cache");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(scratchRoot);
        Directory.CreateDirectory(cacheRoot);

        var logsRoot = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logsRoot);
        var keysRoot = Path.Combine(dataRoot, "keys");
        Directory.CreateDirectory(keysRoot);

        var databasePath = Path.Combine(dataRoot, "mangapixer.db");
        var workerExe = storageOverride?.WorkerExecutablePath
            ?? builder.Configuration["Media:WorkerExecutablePath"];
        // A relative WorkerExecutablePath (e.g. the Windows distribution's
        // "..\worker\MangaPixer.MediaWorker.exe") is resolved against the
        // server's own assembly directory, not the process's current working
        // directory — the CWD a launcher (double-click, Start-Process, a
        // future tray app) uses is not guaranteed to match the exe's folder.
        // The container's env var is already an absolute path, so this is a
        // no-op there.
        if (!string.IsNullOrWhiteSpace(workerExe) && !Path.IsPathRooted(workerExe))
            workerExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, workerExe));

        // Re-inject the fully-resolved absolute roots (and worker path) back
        // into configuration so any OTHER code that re-reads these same keys
        // via a DI-resolved IConfiguration later (e.g. AppRootOptions and the
        // rotating-backups directory in HostingServicesExtensions.AddMangaPixerHosting)
        // sees exactly what Program.Main itself resolved above — including a
        // test-only TestHostStorageOverride — rather than independently
        // re-deriving a possibly-different (or unresolved/relative) value
        // straight from configuration. Without this, a test override only
        // reached the four locals above and every other DI-resolved consumer
        // silently fell back to its own default.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MangaPixer:Storage:DataRoot"] = dataRoot,
            ["MangaPixer:Storage:CacheRoot"] = cacheRoot,
            ["MangaPixer:Storage:ScratchRoot"] = scratchRoot,
            ["Media:WorkerExecutablePath"] = workerExe,
        });

        // Serilog bootstrap — plain text console for both container and dev.
        // The default level is controlled by a LoggingLevelSwitch so an admin
        // can raise/lower verbosity at runtime without a restart.
        // The switch resets to Information on restart (ephemeral by design).
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        // Per-category debug switches. Each category
        // gets its own LoggingLevelSwitch wired via MinimumLevel.Override so an
        // admin can enable Debug for ONE subsystem without the whole firehose.
        // All switches default to Information (matching the global default) so
        // the global switch remains the single control until a category is
        // explicitly overridden. LogLevelSettingsService keeps inherit-mode
        // category switches in sync with the global switch on SetLevel.
        var categorySwitches = new Dictionary<string, LoggingLevelSwitch>();
        foreach (var (name, prefix) in com.lifepixer.mangapixer.Server.Logging.DebugCategories.All)
            categorySwitches[name] = new LoggingLevelSwitch(LogEventLevel.Information);

        var logConfig = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning);

        foreach (var (name, prefix) in com.lifepixer.mangapixer.Server.Logging.DebugCategories.All)
            logConfig.MinimumLevel.Override(prefix, categorySwitches[name]);

        // Drop the EF Core CommandError log line for the recovered reading_progress
        // unique-constraint race (SQLite error 19). EF logs the failed INSERT at Error
        // BEFORE ReadingStateService catches and recovers it, so a recovered race still
        // surfaced as an error (~10/24h in production). The filter excludes ONLY that
        // specific EF log event (SourceContext under Microsoft.EntityFrameworkCore + a
        // SQLite-19 reading_progress message). A genuine uncaught failure still
        // surfaces via the unhandled-exception middleware (LogEvents.Http.UnhandledRequestError),
        // which has a different SourceContext, so real 500s are not masked. The recovery
        // itself is observable at Debug via ReadingStateService when the Reading debug
        // category is enabled. See RecoveredRaceNoiseFilter.
        logConfig.Filter.With(new RecoveredRaceNoiseFilter());

        logConfig
            .Enrich.WithProperty("Application", "MangaPixer")
            .Enrich.With<RedactingDestructuringPolicy>();

        logConfig.WriteTo.Console(
            outputTemplate: "{Timestamp:O} [{Level:u}] {SourceContext} ({EventId}) {Message:lj}{NewLine}{Exception}");

        logConfig.WriteTo.File(
            Path.Combine(logsRoot, "mangapixer-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            fileSizeLimitBytes: 20 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            outputTemplate: "{Timestamp:O} [{Level:u}] {SourceContext} ({EventId}) {Message:lj}{NewLine}{Exception}");

        Log.Logger = logConfig.CreateLogger();

        try
        {
            builder.Host.UseSerilog();

            // Health checks. "/health" is liveness (cheap, in-process only,
            // tagged "live"); "/health/ready" is readiness (tagged "ready") —
            // it exercises the database so an admin/orchestrator can tell a
            // process that is merely alive apart from one that can actually
            // serve requests. Before this split both endpoints mapped to the
            // same "self" check, so a broken DB never showed up as unready.
            builder.Services.AddHealthChecks()
                .AddCheck("self",
                    () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("MangaPixer server is running"),
                    tags: new[] { "live" })
                .AddCheck<DatabaseReadinessHealthCheck>("database", tags: new[] { "ready" });

            // Auth + database (registers DbContext, Identity, cookie auth, auth services)
            builder.Services.AddMangaPixerAuth(databasePath, storageOverride?.RateLimitDisabled);

            // Media worker pool, scheduler, cache, scratch
            // Storage budgets are admin-configurable (bytes). Defaults live in
            // WorkerPoolOptions (1 GiB cache / 1 GiB scratch); override via
            // MangaPixer:Storage:CacheBudgetBytes / ScratchBudgetBytes.
            var cacheBudget = ReadByteBudget(ResolveConfigValue(builder.Configuration, storageOverride, "MangaPixer:Storage:CacheBudgetBytes"));
            var scratchBudget = ReadByteBudget(ResolveConfigValue(builder.Configuration, storageOverride, "MangaPixer:Storage:ScratchBudgetBytes"));
            var maxConcurrentJobs = ReadPositiveInt(ResolveConfigValue(builder.Configuration, storageOverride, "MangaPixer:Media:MaxConcurrentJobs"));
            builder.Services.AddMangaPixerMedia(options =>
            {
                options.ScratchRoot = scratchRoot;
                options.CacheRoot = cacheRoot;
                options.WorkerExecutablePath = workerExe;
                if (cacheBudget is > 0) options.CacheBudgetBytes = cacheBudget.Value;
                if (scratchBudget is > 0) options.ScratchBudgetBytes = scratchBudget.Value;
                if (maxConcurrentJobs is > 0) options.MaxConcurrentJobs = maxConcurrentJobs.Value;
            });

            // Startup configuration logging. Logs existence and
            // budgets only — never absolute paths, per the privacy invariant.
            Log.Logger.ForContext("EventId", LogEvents.Database.StartupStorageRoots).Information("Storage roots initialized: data={DataExists}, cache={CacheExists}, scratch={ScratchExists}",
                Directory.Exists(dataRoot), Directory.Exists(cacheRoot), Directory.Exists(scratchRoot));
            Log.Logger.ForContext("EventId", LogEvents.Database.StartupStorageBudgets).Information("Storage budgets: cache={CacheBudget}, scratch={ScratchBudget}",
                cacheBudget is > 0 ? cacheBudget.Value.ToString() : "default",
                scratchBudget is > 0 ? scratchBudget.Value.ToString() : "default");
            Log.Logger.ForContext("EventId", LogEvents.Database.StartupWorkerExecutable).Information("Worker executable: {Status}",
                string.IsNullOrWhiteSpace(workerExe) ? "auto-discovery" : "configured");

            // Hosted lifecycle services + storage/scanning/page-delivery registrations
            builder.Services.AddMangaPixerHosting();

            // Catalog and reading services
            builder.Services.AddScoped<CatalogBrowseService>();
            builder.Services.AddScoped<ReadingStateService>();
            builder.Services.AddScoped<CatalogIdResolver>();
            builder.Services.AddScoped<ReaderModeResolver>();
            // Jump-index endpoint, kept separate from CatalogBrowseService.
            builder.Services.AddScoped<JumpIndexService>();

            // Home "New chapters" — separate service, kept out of
            // CatalogBrowseService, which owns the main catalog-browsing surface.
            builder.Services.AddScoped<RecentChaptersService>();

            // Operations services
            builder.Services.AddScoped<BackupService>();
            builder.Services.AddScoped<DiagnosticsService>();

            // Log-level control — singleton so the switch survives
            // across requests and mutates the live Serilog pipeline.
            builder.Services.AddSingleton(levelSwitch);
            builder.Services.AddSingleton<IReadOnlyDictionary<string, LoggingLevelSwitch>>(categorySwitches);
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
                .SetApplicationName(ProductName);

            if (OperatingSystem.IsWindows())
            {
                dataProtection.ProtectKeysWithDpapi();
                Log.Logger.ForContext("EventId", LogEvents.Database.DataProtectionKeys).Information("Data Protection keys encrypted at rest with DPAPI");
            }
            else
            {
                Log.Logger.ForContext("EventId", LogEvents.Database.DataProtectionKeys).Information(
                    "Data Protection keys stored unencrypted inside the private data root " +
                    "with owner-only permissions (Linux has no DPAPI; entrypoint.sh chmod 700)");
            }

            // Anti-forgery — double-submit token via X-MangaPixer-Csrf header.
            // The cookie is issued by GET /auth/csrf; unsafe methods must echo
            // the header. Login is exempt (the token is obtained from /csrf
            // immediately before). GET /auth/csrf is exempt (it issues the token).
            //
            // Cookie is HttpOnly (audit defect D2): the client obtains the token
            // from GET /auth/csrf as a JSON response body, not by reading the
            // cookie. This prevents XSS from stealing the cookie value.
            builder.Services.AddAntiforgery(options =>
            {
                options.HeaderName = "X-MangaPixer-Csrf";
                options.Cookie.Name = ".MangaPixer.Csrf";
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

            // Unhandled-error middleware: app-level capture of 500s.
            // The exception goes to the log (file sink gets the trace); the client
            // gets a sanitized response with no internals.
            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    var error = context.Features
                        .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>()?.Error;
                    Log.Logger.ForContext("EventId", LogEvents.Http.UnhandledRequestError).Error(error,
                        "Unhandled request error: {ErrorType}", error?.GetType().Name ?? "unknown");
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new ApiError
                    {
                        Error = "internal_error",
                        Message = "An unexpected error occurred.",
                    });
                });
            });

            // Migrate the schema to the latest EF migration. This is data-critical:
            // a failure must STOP startup (fail-fast) rather than serve a
            // half-migrated database, so it is deliberately OUTSIDE the
            // degrade-to-health-only catch used for the rest of initialization. Any
            // pre-migration backup taken by the orchestrator is preserved on the
            // data volume for recovery.
            //
            // Apply a pending DB restore BEFORE any connection to the live DB
            // is opened (1.7.0). An admin uploads a validated backup via
            // POST /api/v1/operations/restore; the validated file is staged and
            // a marker written. The atomic swap happens here, at startup, with
            // no connections open — so the live DB is never overwritten while
            // in use. On failure the system rolls back to the original DB.
            com.lifepixer.mangapixer.Server.Operations.RestoreApplyOutcome restoreOutcome =
                com.lifepixer.mangapixer.Server.Operations.RestoreApplyOutcome.None;
            using (var migrateScope = app.Services.CreateScope())
            {
                restoreOutcome = com.lifepixer.mangapixer.Server.Operations.DbRestoreService
                    .ApplyPendingRestoreAsync(dataRoot, databasePath,
                        migrateScope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("MangaPixer.DbRestore"))
                    .GetAwaiter().GetResult();

                var db = migrateScope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
                var backup = migrateScope.ServiceProvider.GetRequiredService<BackupService>();
                var dbLogger = migrateScope.ServiceProvider
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("MangaPixer.DatabaseInitialization");
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
                    var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
                    DatabaseInitialization.ConfigureDatabaseAsync(db).GetAwaiter().GetResult();

                    // Audit a completed DB restore (1.7.0). The swap happened
                    // before migrate; the audit row lands in the restored DB.
                    if (restoreOutcome.Applied)
                    {
                        var dbRestore = scope.ServiceProvider.GetRequiredService<com.lifepixer.mangapixer.Server.Operations.DbRestoreService>();
                        dbRestore.AuditRestoreAsync(
                            "db_restore", "applied",
                            restoreOutcome.ActorUserName,
                            correlationId: null).GetAwaiter().GetResult();
                    }
                    else if (restoreOutcome.Failed)
                    {
                        Log.Logger.ForContext("EventId", LogEvents.Backup.RestoreRolledBack)
                            .Warning("Pending DB restore did not apply: {Error}", restoreOutcome.Error ?? "unknown");
                    }

                    // No default credential is created (audit finding F2). On a
                    // fresh instance the first admin is created by the user via
                    // POST /api/v1/auth/setup; just log that setup is pending.
                    var setup = scope.ServiceProvider.GetRequiredService<FirstRunSetupService>();
                    if (setup.IsSetupRequiredAsync().GetAwaiter().GetResult())
                        Log.Logger.ForContext("EventId", LogEvents.Database.FirstRunSetupPending).Information("First-run setup required: no users exist. Create the admin via the setup screen.");
                }
                catch (Exception ex)
                {
                    Log.Logger.ForContext("EventId", LogEvents.Database.DatabaseInitDegraded).Warning(ex, "Database initialization failed; health checks will still respond");
                }
            }

            // Health endpoints (before auth so they're always accessible).
            // Each maps to its own tag so liveness stays cheap and independent
            // of readiness (see the AddHealthChecks registration above).
            app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains("live"),
            });
            app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains("ready"),
            });

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
            Log.Logger.ForContext("EventId", LogEvents.Database.FatalShutdown).Fatal(ex, "MangaPixer server terminated unexpectedly");
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
        {
            // Windows-native distribution default: a per-user, always-writable
            // profile directory rather than the install folder (which may sit
            // under a read-only Program Files). Containers/Linux always set
            // MangaPixer__Storage__*Root explicitly (see deploy/Dockerfile), so
            // this branch never fires there and that default is unchanged.
            if (OperatingSystem.IsWindows())
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, ProductName, defaultName);
            }
            return Path.Combine(AppContext.BaseDirectory, defaultName);
        }
        return Path.GetFullPath(value);
    }

    /// <summary>
    /// Resolves a configuration value, checking the test-only
    /// <see cref="TestHostStorageOverride"/>'s <c>ExtraConfiguration</c> map
    /// first (when an override is pushed) before falling back to
    /// <paramref name="configuration"/>. Always falls back to
    /// <paramref name="configuration"/> in production, where no override is
    /// ever pushed. See TestHostStorageOverride for why this exists — the
    /// values resolved here (media/storage knobs) are read synchronously
    /// before <c>builder.Build()</c>, where WebApplicationFactory's
    /// ConfigureAppConfiguration overrides are not yet visible.
    /// </summary>
    private static string? ResolveConfigValue(
        IConfiguration configuration,
        StorageRootOverride? storageOverride,
        string key)
    {
        if (storageOverride?.ExtraConfiguration is { } extra && extra.TryGetValue(key, out var overrideValue))
            return overrideValue;
        return configuration[key];
    }

    /// <summary>
    /// Reads an optional byte budget from config. Returns null when unset/invalid
    /// so the caller keeps the WorkerPoolOptions default. Accepts a plain byte
    /// count (e.g. 268435456) — deployments can compute from MiB/GiB as needed.
    /// </summary>
    private static long? ReadByteBudget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return long.TryParse(value.Trim(), out var bytes) && bytes > 0 ? bytes : null;
    }

    /// <summary>
    /// Reads an optional positive integer from config. Returns null when
    /// unset/invalid so the caller keeps the WorkerPoolOptions default (2).
    /// </summary>
    private static int? ReadPositiveInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value.Trim(), out var n) && n > 0 ? n : null;
    }
}

/// <summary>
/// Readiness check for <c>/health/ready</c>: confirms the database is
/// actually reachable, unlike the cheap in-process "self" liveness check
/// mapped to <c>/health</c>. Registered via <c>AddCheck&lt;T&gt;</c> so a new
/// scoped instance (and DbContext) is resolved per health-check execution.
/// </summary>
public sealed class DatabaseReadinessHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly com.lifepixer.mangapixer.Server.Persistence.MangaPixerDbContext _db;

    public DatabaseReadinessHealthCheck(com.lifepixer.mangapixer.Server.Persistence.MangaPixerDbContext db)
    {
        _db = db;
    }

    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken ct = default)
    {
        try
        {
            // A real (trivial) query, not just Database.CanConnectAsync: SQLite
            // opens the file lazily on connect without validating its
            // contents, so CanConnectAsync alone would not catch a corrupt or
            // unreadable database file.
            await _db.Users.AsNoTracking().AnyAsync(ct);
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Database reachable");
        }
        catch (Exception ex)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(
                "Database check failed", ex);
        }
    }
}
