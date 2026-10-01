namespace com.lifepixer.mangapixer.Server.Features.Trash;

/// <summary>DI registration of Empty trash + Clean bundles (1.31.0).</summary>
public static class TrashServices
{
    public static IServiceCollection AddTrash(this IServiceCollection services)
    {
        services.AddScoped<NodePurger>();
        services.AddScoped<BundleCleaner>();
        services.AddScoped<TrashService>();
        services.AddSingleton<TrashRunGate>();
        services.AddSingleton<TrashHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<TrashHostedService>());
        return services;
    }
}
