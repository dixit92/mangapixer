namespace com.lifepixer.mangaplex.Server;

using com.lifepixer.mangaplex.Server.Media;

/// <summary>
/// Server entry point. Wires up health checks, media worker pool, and API endpoints.
/// The media worker pool supervises local worker processes for archive/image processing.
/// </summary>
public sealed partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Health checks
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("MangaPlex server is running"));

        // Media worker pool — supervised local worker processes for archive/image processing.
        // Configurable scratch root and worker executable path.
        var scratchRoot = builder.Configuration["Media:ScratchRoot"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "scratch");
        var workerExe = builder.Configuration["Media:WorkerExecutablePath"];
        var cacheRoot = builder.Configuration["Media:CacheRoot"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "cache");

        builder.Services.AddMangaPlexMedia(options =>
        {
            options.ScratchRoot = scratchRoot;
            options.CacheRoot = cacheRoot;
            options.WorkerExecutablePath = workerExe;
        });

        var app = builder.Build();

        // Version prefix reserved for future API endpoints.
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/ready");

        app.Run();
    }
}
