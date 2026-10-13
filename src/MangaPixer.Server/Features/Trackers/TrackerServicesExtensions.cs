namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Tracker sync registrations (1.43.0). Called once from <c>Program</c>; the tracker lanes register their services here, never in
/// <c>Program.cs</c>. Until the engine registers its own <see cref="IReadMarkObserver"/>, read-mark changes go to
/// <see cref="NoReadMarkObserver"/>.
/// </summary>
public static class TrackerServicesExtensions
{
    public static IServiceCollection AddTrackers(this IServiceCollection services)
    {
        services.TryAddSingleton<IReadMarkObserver, NoReadMarkObserver>();
        services.TryAddSingleton<ReadMarkChangeInterceptor>();
        Adapters.TrackerAdapterServicesExtensions.AddTrackerAdapters(services);
        return services;
    }
}
