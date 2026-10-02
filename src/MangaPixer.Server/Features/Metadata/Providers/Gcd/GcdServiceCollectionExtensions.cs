namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.Gcd;

/// <summary>DI for the Grand Comics Database provider (1.32.0, lane B). The named clients are registered by <c>Program.AddMetadataNetwork</c> (step 0).</summary>
public static class GcdServiceCollectionExtensions
{
    /// <summary>
    /// Registers GCD as an <see cref="IMetadataProvider"/> next to MangaUpdates (Identify's site switch, comics routing of automatic
    /// matching, refresh of GCD-linked records) and its detail reader.
    /// </summary>
    public static IServiceCollection AddGcdProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<GcdProvider>();
        services.AddSingleton<IMetadataProvider>(sp => sp.GetRequiredService<GcdProvider>());
        services.AddScoped<GcdDetails>();
        return services;
    }
}
