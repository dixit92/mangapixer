namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes.Wikipedia;

/// <summary>DI registration of the Wikipedia companion (1.32.0). <c>Program.cs</c> calls <see cref="AddWikipediaVolumeLists"/> once.</summary>
public static class WikipediaVolumeListsExtensions
{
    /// <summary>
    /// Registers the Wikimedia API client (a singleton: its one-request-at-a-time gate is shared), the companion service and the admin
    /// service. The named HTTP clients (<see cref="MetadataHttp.WikipediaClient"/> / <see cref="MetadataHttp.WikidataClient"/>) and the one
    /// Wikimedia limiter come from step 0; the controller is discovered with the others. Needs <see cref="VolumeMapService"/> and
    /// <see cref="MetadataGateway"/> (registered with the volume-cover pass).
    /// </summary>
    public static IServiceCollection AddWikipediaVolumeLists(this IServiceCollection services)
    {
        services.AddSingleton<IWikipediaApi, WikipediaApi>();
        services.AddScoped<WikipediaVolumeService>();
        services.AddScoped<WikipediaListAdminService>();
        return services;
    }
}
