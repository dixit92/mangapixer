namespace com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The tracker adapters' registrations (1.43.0: the MyAnimeList and MangaUpdates <c>ITrackerClient</c>s and their HTTP clients), called
/// from <see cref="TrackerServicesExtensions.AddTrackers"/>. The adapter lane owns this file; the engine lane owns the caller.
/// </summary>
public static class TrackerAdapterServicesExtensions
{
    public static IServiceCollection AddTrackerAdapters(this IServiceCollection services) => services;
}
