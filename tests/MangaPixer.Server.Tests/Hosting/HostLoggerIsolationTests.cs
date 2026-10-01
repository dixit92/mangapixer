namespace com.lifepixer.mangapixer.Tests.Server.Hosting;

using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xunit;

/// <summary>
/// Pins the per-host logging seam the parallel test suite relies on: booting a host must neither read nor
/// replace the process-global <see cref="Log.Logger"/>, and each host owns its logger.
/// </summary>
public sealed class HostLoggerIsolationTests
{
    [Fact]
    public async Task BootingHosts_LeavesTheProcessGlobalLoggerAlone_AndEachHostHasItsOwn()
    {
        var before = Log.Logger;

        await using var first = new MangaPixerWebApplicationFactory();
        await using var second = new MangaPixerWebApplicationFactory();

        Assert.Same(before, Log.Logger);
        var firstLogger = first.Services.GetRequiredService<Serilog.ILogger>();
        var secondLogger = second.Services.GetRequiredService<Serilog.ILogger>();
        Assert.NotSame(firstLogger, secondLogger);
        Assert.NotSame(before, firstLogger);
    }

    [Fact]
    public async Task CapturingSink_SeesTheHostsOwnEvents_WithoutTouchingAnotherHost()
    {
        var sink = new CollectingSink();
        await using var capturing = new C00WebApplicationFactory(sink);
        await using var other = new MangaPixerWebApplicationFactory();

        capturing.Services.GetRequiredService<Serilog.ILogger>().Information("probe from the capturing host");
        other.Services.GetRequiredService<Serilog.ILogger>().Information("probe from the other host");

        Assert.True(sink.ContainsMessage("probe from the capturing host"));
        Assert.False(sink.ContainsMessage("probe from the other host"));
    }
}
