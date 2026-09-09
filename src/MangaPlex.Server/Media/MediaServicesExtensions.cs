namespace com.lifepixer.mangaplex.Server.Media;

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
    public static IServiceCollection AddMangaPlexMedia(this IServiceCollection services, Action<WorkerPoolOptions> configure)
    {
        var options = new WorkerPoolOptions();
        configure(options);

        services.AddSingleton(options);
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
            return new ThumbnailGenerationService(pool, store, cache, scopeFactory,
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
}
