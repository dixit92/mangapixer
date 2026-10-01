namespace com.lifepixer.mangapixer.Tests.Server.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

/// <summary>
/// Test seam for capturing a host's log events without any process-global state.
/// </summary>
/// <remarks>
/// <c>Program.Main</c> builds its Serilog logger as a local and registers it as <see cref="Serilog.ILogger"/> and as
/// the host's <see cref="ILoggerFactory"/>. A factory that wants to see the events calls
/// <see cref="Wrap"/> from <c>ConfigureTestServices</c>: the host's own logger is wrapped by one the test builds
/// (typically adding a <see cref="CollectingSink"/>), and both services are replaced, so everything the host logs
/// (hosted services, controllers, the startup code in Program) goes through the wrapper. Nothing here touches
/// <c>Serilog.Log.Logger</c>, which is why host-booting test classes can run in parallel.
/// </remarks>
public static class TestHostLogging
{
    /// <summary>Replaces the host's logger with <paramref name="wrap"/> applied to the host's own logger.</summary>
    public static void Wrap(IServiceCollection services, Func<Serilog.ILogger, Serilog.ILogger> wrap)
    {
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(Serilog.ILogger))?.ImplementationInstance as Serilog.ILogger
            ?? throw new InvalidOperationException("The host did not register its Serilog logger as an instance.");
        var wrapped = wrap(inner);

        services.RemoveAll<ILoggerFactory>();
        services.RemoveAll<Serilog.ILogger>();
        // The inner logger stays owned by Program.Main; the wrapper holds no resources of its own.
        services.AddSingleton<ILoggerFactory>(_ => new SerilogLoggerFactory(wrapped, dispose: false));
        services.AddSingleton(wrapped);
    }
}
