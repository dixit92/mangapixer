namespace com.lifepixer.mangapixer.Tests.Server.Http;

using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Idle worker retirement (1.22.0) through the booted host:
/// MangaPixer:Media:WorkerIdleTimeoutSeconds and MangaPixer:Media:MinWarmWorkers
/// are bound in Program.cs into the DI-resolved <see cref="WorkerPoolOptions"/>,
/// and the DI-resolved <see cref="MediaWorkerPool"/>, started by the real
/// <see cref="MediaWorkerHostedService"/>, retires its idle worker.
///
/// The test factory removes the hosted service so ordinary HTTP tests do not
/// spawn workers; the wiring test constructs it explicitly from the host's
/// own services, which is exactly what the host would do.
/// In the "HttpSerial" collection with every other host-booting class (see
/// <see cref="WorkerConcurrencyConfigTests"/>).
/// </summary>
[Collection("HttpSerial")]
public sealed class WorkerIdleRetirementConfigTests
{
    [Fact]
    public async Task Unset_KeepsDefaults_ThreeMinutesAndNoWarmFloor()
    {
        await using var factory = new MangaPixerWebApplicationFactory();
        var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
        Assert.Equal(TimeSpan.FromMinutes(3), options.WorkerIdleTimeout);
        Assert.Equal(0, options.MinWarmWorkers);
    }

    [Fact]
    public async Task BoundFromConfig_OverridesDefaults()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?>
            {
                ["MangaPixer:Media:WorkerIdleTimeoutSeconds"] = "45",
                ["MangaPixer:Media:MinWarmWorkers"] = "1",
            });
        var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
        Assert.Equal(TimeSpan.FromSeconds(45), options.WorkerIdleTimeout);
        Assert.Equal(1, options.MinWarmWorkers);
    }

    [Fact]
    public async Task ZeroTimeout_IsAccepted_AndDisablesRetirement()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?> { ["MangaPixer:Media:WorkerIdleTimeoutSeconds"] = "0" });
        var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
        Assert.Equal(TimeSpan.Zero, options.WorkerIdleTimeout);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("soon")]
    public async Task InvalidValues_KeepDefaults(string value)
    {
        await using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?>
            {
                ["MangaPixer:Media:WorkerIdleTimeoutSeconds"] = value,
                ["MangaPixer:Media:MinWarmWorkers"] = value,
            });
        var options = factory.Services.GetRequiredService<WorkerPoolOptions>();
        Assert.Equal(TimeSpan.FromMinutes(3), options.WorkerIdleTimeout);
        Assert.Equal(0, options.MinWarmWorkers);
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task HostedService_StartsPool_AndIdleWorkerIsRetired()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?> { ["MangaPixer:Media:WorkerIdleTimeoutSeconds"] = "1" });
        var pool = factory.Services.GetRequiredService<MediaWorkerPool>();
        var hosted = new MediaWorkerHostedService(
            pool,
            NullLogger<MediaWorkerHostedService>.Instance,
            factory.Services.GetRequiredService<IHostApplicationLifetime>());

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            // The hosted service swallows pool start failures (non-fatal by
            // design), so prove a worker really started before waiting for it
            // to retire; otherwise an unstartable worker would pass vacuously.
            Assert.Equal(1, pool.WorkerCount);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (pool.WorkerCount > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.Equal(0, pool.WorkerCount);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }
}
