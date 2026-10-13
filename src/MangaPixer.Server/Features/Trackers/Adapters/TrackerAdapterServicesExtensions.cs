namespace com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;

using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// The tracker adapters' registrations (1.43.0: the MyAnimeList and MangaUpdates <c>ITrackerClient</c>s and their HTTP clients), called
/// from <see cref="TrackerServicesExtensions.AddTrackers"/>. The adapter lane owns this file; the engine lane owns the caller.
/// The engine resolves <c>IEnumerable&lt;ITrackerClient&gt;</c> and picks a client by <see cref="ITrackerClient.Slug"/>.
/// </summary>
public static class TrackerAdapterServicesExtensions
{
    public static IServiceCollection AddTrackerAdapters(this IServiceCollection services)
    {
        services.TryAddSingleton<TrackerRateLimitOptions>();
        services.TryAddSingleton<TrackerPacing>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITrackerClient, MyAnimeListTrackerClient>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITrackerClient, MangaUpdatesTrackerClient>());
        AddTrackerClient(services, TrackerHttp.MyAnimeListClient, TrackerHttp.MyAnimeListApiHost, TrackerHttp.MyAnimeListSiteHost);
        AddTrackerClient(services, TrackerHttp.MangaUpdatesClient, MetadataHttp.MangaUpdatesApiHost);
        return services;
    }

    /// <summary>
    /// One tracker's named client: only its own hosts (<see cref="HostAllowlistHandler"/>, outermost), the token added after that check
    /// (<see cref="TrackerBearerHandler"/>), no redirects, no cookies, the fixed User-Agent, a timeout, no request logging (the default
    /// logger writes full URIs). Tests replace the primary handler.
    /// </summary>
    internal static IHttpClientBuilder AddTrackerClient(IServiceCollection services, string name, params string[] hosts) =>
        services.AddHttpClient(name, client =>
            {
                client.Timeout = TrackerHttp.Timeout;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(MetadataHttp.UserAgent);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            })
            .AddHttpMessageHandler(() => new HostAllowlistHandler(hosts))
            .AddHttpMessageHandler(() => new TrackerBearerHandler())
            .RemoveAllLoggers();
}
