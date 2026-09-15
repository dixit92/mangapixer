namespace com.lifepixer.mangaplex.TestSupport.Hosting;

/// <summary>
/// Process-wide gate that serializes <c>WebApplicationFactory</c> host boots
/// across parallel test classes.
/// </summary>
/// <remarks>
/// <para>
/// MangaPlex's <c>Program.Main</c> resolves the storage roots
/// (<c>DataRoot</c>/<c>CacheRoot</c>/<c>ScratchRoot</c>) from
/// <c>builder.Configuration</c> at the very top of <c>Main</c>, before any
/// <c>ConfigureWebHost</c>/<c>ConfigureAppConfiguration</c> override is
/// applied. The only injection point for those roots in the minimal-host
/// model is therefore the process environment, which the test factories set
/// via <c>Environment.SetEnvironmentVariable</c>.
/// </para>
/// <para>
/// Environment variables are process-global. When two factories boot in
/// parallel, the second factory's constructor can overwrite the storage env
/// vars between the first factory's constructor and the first factory's lazy
/// <c>CreateClient</c>/<c>Services</c> access, so both hosts resolve the same
/// <c>DataRoot</c> and the same SQLite file. Both then run the EF migration
/// (<c>InitialCreate</c>) against that shared fresh database and collide on
/// <c>CREATE TABLE audit_events</c> with "audit_events already exists". This
/// was the carried <c>OpenApiSnapshot</c>/<c>HostingCorrectness</c> flake.
/// </para>
/// <para>
/// The fix is to make "set my storage env vars" and "boot my host" an atomic
/// critical section per factory. Each test factory holds this gate while it
/// sets its env vars and forces its host to boot, so no other factory can
/// interleave and the env vars in effect at <c>Main</c>'s config read are
/// always the booting factory's own. After boot the host has captured its
/// <c>databasePath</c> into DI, so later env-var changes by other factories
/// do not affect it. Test bodies still run in parallel; only the boot phase
/// is serialized.
/// </para>
/// </remarks>
public static class TestHostBootGate
{
    private static readonly object _gate = new();

    /// <summary>
    /// Acquires the process-wide boot gate. Call <see cref="IDisposable.Dispose"/>
    /// on the returned handle to release it. The gate is held across the
    /// synchronous host boot, so a plain <see cref="Monitor"/> lock is correct
    /// (no async flow crosses it).
    /// </summary>
    public static IDisposable Acquire()
    {
        System.Threading.Monitor.Enter(_gate);
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        public void Dispose() => System.Threading.Monitor.Exit(_gate);
    }
}
