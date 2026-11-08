namespace com.lifepixer.mangapixer.Tests.Server.Hosting;

using System.Runtime.CompilerServices;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Xunit;

/// <summary>
/// A disposed test host must become garbage. The Server suite boots thousands of hosts in one process; until 1.52.0 every one stayed
/// reachable after Dispose (the never-disposed ASP.NET rate limiters' replenishment timers captured the execution context that holds
/// WebApplicationFactory's hosting listener, and so the factory and its whole host), and the test host's heap grew to .NET's container
/// cap (the "Test host process crashed" flake). Anything new that roots a host from a static or a timer fails here.
/// </summary>
public sealed class TestHostLifetimeTests
{
    [Fact]
    public async Task DisposedHost_AfterServingARequest_IsCollected()
    {
        var host = await BootServeAndDisposeAsync();

        for (var i = 0; i < 3 && host.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(host.IsAlive, "A disposed MangaPixerWebApplicationFactory is still reachable - something roots the host.");
    }

    // Async on purpose: AsyncLocal values the host build sets on this call's execution context are dropped when it returns, as they
    // are for a test method. NoInlining keeps the factory out of the caller's frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> BootServeAndDisposeAsync()
    {
        var factory = new MangaPixerWebApplicationFactory();
        using (var client = factory.CreateClient())
            Assert.True((await client.GetAsync("/health")).IsSuccessStatusCode);
        await factory.DisposeAsync();
        return new WeakReference(factory);
    }
}
