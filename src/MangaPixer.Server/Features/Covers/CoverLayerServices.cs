namespace com.lifepixer.mangapixer.Server.Features.Covers;

/// <summary>DI registration of the 1.29.0 cover layer (one call from <c>Program</c>).</summary>
public static class CoverLayerServices
{
    public static IServiceCollection AddCoverLayer(this IServiceCollection services, string dataRoot)
    {
        services.AddSingleton(new CoverFiles(dataRoot));
        services.AddScoped<CoverResolutionService>();
        // Every card cover (browse, search, favourites, Home, Identify, Missing, review) asks this one resolver.
        services.AddScoped<ICoverResolver, LayeredCoverResolver>();
        services.AddScoped<CoverCropService>();
        services.AddScoped<CoverPickerService>();
        services.AddScoped<StackCoverService>();
        services.AddScoped<FolderCoverPreferenceService>();
        services.AddScoped<CoverDirectionResolver>();
        services.AddScoped<CoverDecisionService>();
        services.AddSingleton<CoverDecisionQueue>();
        services.AddHostedService<CoverDecisionHostedService>();
        return services;
    }
}
