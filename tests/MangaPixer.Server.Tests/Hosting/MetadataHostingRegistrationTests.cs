namespace com.lifepixer.mangapixer.Tests.Server.Hosting;

using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// Wiring test (1.24.0): the ComicInfo backfill is registered as a singleton and
/// kicked by a hosted service, after the worker pool's hosted service (the HTTP
/// test factory removes the kick because it never starts workers).
/// </summary>
public sealed class MetadataHostingRegistrationTests
{
    [Fact]
    public void AddMangaPixerHosting_RegistersTheComicInfoBackfill_AfterTheWorkerPool()
    {
        var services = new ServiceCollection();
        services.AddMangaPixerHosting();

        var backfill = Assert.Single(services, d => d.ServiceType == typeof(ComicInfoBackfillService));
        Assert.Equal(ServiceLifetime.Singleton, backfill.Lifetime);

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();
        var workerIndex = hosted.IndexOf(typeof(MediaWorkerHostedService));
        var kickIndex = hosted.IndexOf(typeof(ComicInfoBackfillHostedService));
        Assert.True(workerIndex >= 0);
        Assert.True(kickIndex > workerIndex);
    }
}
