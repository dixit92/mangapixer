namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

/// <summary>The new-chapter freshness services (1.45.0, lane R), added with the metadata fetcher's network services.</summary>
public static class FreshnessServicesExtensions
{
    public static IServiceCollection AddNewChapterFreshness(this IServiceCollection services)
    {
        services.AddSingleton<Providers.MangaUpdates.MangaUpdatesReleasesFeed>();
        services.AddScoped<LatestUploadSweep>();
        services.AddScoped<ReleasesFeedPoll>();
        services.AddScoped<ReadableBackfill>();
        services.AddScoped<NewChapterChecksRunner>();
        services.AddScoped<TakenDownRecordsService>(); // 1.47.0: the Runs tab's taken-down list (stored rows only)
        return services;
    }
}
