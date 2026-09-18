namespace com.lifepixer.mangapixer.Server.Media;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// DI registration extensions for media worker services.
/// </summary>
public static class MediaServicesExtensions
{
    /// <summary>
    /// Registers the media worker pool, job scheduler, scratch workspace manager,
    /// cache service, and page delivery service.
    /// </summary>
    public static IServiceCollection AddMangaPixerMedia(this IServiceCollection services, Action<WorkerPoolOptions> configure)
    {
        var options = new WorkerPoolOptions();
        configure(options);

        services.AddSingleton(options);

        // Continuous thumbnail backfill options (post-1.2.0). Bound from
        // MangaPixer:Media:ThumbnailBackfill:*; a default instance is used when
        // the section is absent so the backfill works without explicit config.
        services.AddSingleton(sp =>
        {
            var config = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
            var opts = new ThumbnailBackfillOptions();
            if (config is null)
                return opts;
            var section = config.GetSection("MangaPixer:Media:ThumbnailBackfill");
            if (int.TryParse(section["BatchSize"], out var batchSize) && batchSize > 0)
                opts.BatchSize = batchSize;
            if (int.TryParse(section["BackoffMs"], out var backoffMs) && backoffMs > 0)
                opts.BackoffMs = backoffMs;
            return opts;
        });
        // Downscaled page-variant ladder (1.19.0). Bound from
        // MangaPixer:Media:PageVariants:*; a default instance is used when the
        // section is absent so sized page variants work without explicit config.
        // The ladder accepts either indexed children
        // (MangaPixer__Media__PageVariants__MaxDimensions__0=1080) or a single
        // comma-separated value, because env-var arrays are awkward to write.
        services.AddSingleton(sp =>
        {
            var config = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
            var opts = new PageVariantOptions();
            if (config is null)
                return opts;
            var section = config.GetSection("MangaPixer:Media:PageVariants");
            var ladder = ReadLadder(section);
            if (ladder is not null)
                opts.MaxDimensions = ladder;
            if (int.TryParse(section["WebpQuality"], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var quality))
                opts.WebpQuality = quality;
            // Default resampling filter for sized page variants (1.20.0).
            // Taken verbatim; Validate() below normalises the case and rejects
            // an unknown name, so a typo fails at startup rather than silently
            // falling back to a filter the owner did not choose.
            var defaultFilter = section["DefaultFilter"];
            if (!string.IsNullOrWhiteSpace(defaultFilter))
                opts.DefaultFilter = defaultFilter;
            // Fail fast at startup: a malformed ladder must not silently serve
            // the wrong sizes for the lifetime of the process.
            opts.Validate();
            return opts;
        });
        services.AddSingleton<ScratchWorkspaceManager>(sp =>
            new ScratchWorkspaceManager(options.ScratchRoot, options.ScratchBudgetBytes,
                sp.GetService<ILogger<ScratchWorkspaceManager>>()));
        services.AddSingleton<JobScheduler>(sp =>
            new JobScheduler(options, sp.GetService<ILogger<JobScheduler>>()));
        services.AddSingleton<CacheService>(sp =>
            new CacheService(options.CacheRoot, options.CacheBudgetBytes,
                sp.GetService<ILogger<CacheService>>()));
        services.AddSingleton<AnalysisResultPersister>();

        // Durable thumbnail store under DataRoot/thumbnails (1.2.0). Lives in
        // the persistent data root — NOT the evictable cache — so thumbnails
        // survive restarts and cache-clears. Resolved from AppRootOptions so
        // it tracks the configured DataRoot (not the cache root).
        services.AddSingleton<ThumbnailStore>(sp =>
        {
            var appRoots = sp.GetService<Storage.AppRootOptions>();
            var dataRoot = !string.IsNullOrWhiteSpace(appRoots?.DataRoot)
                ? appRoots.DataRoot
                : Path.Combine(AppContext.BaseDirectory, "data");
            return new ThumbnailStore(
                Path.Combine(dataRoot, "thumbnails"),
                sp.GetService<ILogger<ThumbnailStore>>());
        });

        // Thumbnail generation service — depends on the worker pool (resolved
        // lazily by the pool via IServiceScopeFactory after a job completes, so
        // there is no circular constructor dependency).
        services.AddSingleton<ThumbnailGenerationService>(sp =>
        {
            var pool = sp.GetRequiredService<MediaWorkerPool>();
            var store = sp.GetRequiredService<ThumbnailStore>();
            var cache = sp.GetRequiredService<CacheService>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var backfillOptions = sp.GetRequiredService<ThumbnailBackfillOptions>();
            return new ThumbnailGenerationService(pool, store, cache, scopeFactory, backfillOptions,
                sp.GetService<ILogger<ThumbnailGenerationService>>());
        });

        services.AddSingleton<MediaWorkerPool>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<MediaWorkerPool>>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var scheduler = sp.GetRequiredService<JobScheduler>();
            var scratch = sp.GetRequiredService<ScratchWorkspaceManager>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var persister = sp.GetRequiredService<AnalysisResultPersister>();
            return new MediaWorkerPool(options, scheduler, scratch, logger, loggerFactory, scopeFactory, persister);
        });

        return services;
    }

    /// <summary>
    /// Reads the page-variant ladder from configuration, accepting either an
    /// indexed array section or a single comma-separated string. Returns null
    /// when the key is absent (keep the defaults). A present-but-unparseable
    /// entry throws so a typo surfaces at startup rather than silently dropping
    /// a bucket.
    /// </summary>
    private static int[]? ReadLadder(Microsoft.Extensions.Configuration.IConfiguration section)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var arraySection = section.GetSection("MaxDimensions");

        var children = arraySection.GetChildren().ToList();
        if (children.Count > 0)
        {
            var values = new int[children.Count];
            for (var i = 0; i < children.Count; i++)
            {
                if (!int.TryParse(children[i].Value, System.Globalization.NumberStyles.Integer, invariant, out values[i]))
                    throw new InvalidOperationException(
                        "MangaPixer:Media:PageVariants:MaxDimensions contains a non-integer entry.");
            }
            return values;
        }

        var raw = arraySection.Value;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.Integer, invariant, out parsed[i]))
                throw new InvalidOperationException(
                    "MangaPixer:Media:PageVariants:MaxDimensions contains a non-integer entry.");
        }
        return parsed;
    }
}
