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
            new ScratchWorkspaceManager(options.ScratchRoot, options.ScratchBudgetBytes));
        services.AddSingleton<JobScheduler>(sp =>
            new JobScheduler(options));
        services.AddSingleton<CacheService>(sp =>
            new CacheService(options.CacheRoot, options.CacheBudgetBytes,
                sp.GetService<ILogger<CacheService>>()));
        services.AddSingleton<MediaWorkerPool>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<MediaWorkerPool>>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var scheduler = sp.GetRequiredService<JobScheduler>();
            var scratch = sp.GetRequiredService<ScratchWorkspaceManager>();
            return new MediaWorkerPool(options, scheduler, scratch, logger, loggerFactory);
        });

        return services;
    }
}
