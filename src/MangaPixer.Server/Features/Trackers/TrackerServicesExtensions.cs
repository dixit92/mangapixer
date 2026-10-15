namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Tracker sync registrations (1.43.0). Called once from <c>Program</c>; the tracker lanes register their services here, never in
/// <c>Program.cs</c>. The engine's <see cref="TrackerRelay"/> is the <see cref="IReadMarkObserver"/> (it replaces
/// <see cref="NoReadMarkObserver"/>): committed read-mark changes are enqueued and relayed by <see cref="TrackerRelayService"/>.
/// </summary>
public static class TrackerServicesExtensions
{
    public static IServiceCollection AddTrackers(this IServiceCollection services)
    {
        services.TryAddSingleton<TrackerRelay>();
        services.TryAddSingleton<IReadMarkObserver>(sp => sp.GetRequiredService<TrackerRelay>());
        services.TryAddSingleton<ReadMarkChangeInterceptor>();
        services.TryAddSingleton<TrackerTokenProtector>();
        services.TryAddScoped<TrackerClients>();
        services.TryAddScoped<TrackerSettingsService>();
        services.TryAddScoped<TrackerAccountService>();
        services.TryAddScoped<TrackerSeriesResolver>();
        services.TryAddScoped<TrackerSyncService>();
        services.TryAddScoped<TrackerEntriesService>();
        services.TryAddScoped<TrackerAdminService>();
        services.TryAddScoped<TrackerRelayProcessor>();
        services.TryAddScoped<TrackerListService>();
        services.TryAddScoped<TrackerPullService>();
        services.TryAddScoped<TrackerCardMarks>();
        services.TryAddScoped<TrackerListCheckService>();
        services.AddHostedService<TrackerRelayService>();
        Adapters.TrackerAdapterServicesExtensions.AddTrackerAdapters(services);
        return services;
    }
}
