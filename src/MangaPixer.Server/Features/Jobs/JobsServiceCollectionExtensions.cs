namespace com.lifepixer.mangapixer.Server.Features.Jobs;

using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>DI registration of the Scheduled jobs section (1.32.0): the persisted last runs and the jobs API. Idempotent.</summary>
public static class JobsServiceCollectionExtensions
{
    public static IServiceCollection AddScheduledJobs(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<JobRunRecorder>();
        services.TryAddScoped<ScheduledJobsService>();
        return services;
    }
}
