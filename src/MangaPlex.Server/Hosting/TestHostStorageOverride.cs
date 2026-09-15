namespace com.lifepixer.mangaplex.Server.Hosting;

/// <summary>
/// Test-only ambient override for the storage roots (DataRoot/CacheRoot/
/// ScratchRoot), worker executable path, and login-rate-limit disable flag
/// that <c>Program.Main</c> otherwise resolves from <c>IConfiguration</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b>
/// <c>Server.Tests</c> boots real ASP.NET hosts via
/// <c>WebApplicationFactory&lt;Program&gt;</c> for HTTP integration tests.
/// Before this change, each test factory injected its own DataRoot via a
/// PROCESS-GLOBAL environment variable, because that was the only injection
/// point that worked. Environment variables are process-wide: two factories
/// booting concurrently could overwrite each other's variable between one
/// factory's constructor and its host actually reading it, so both hosts
/// resolved the SAME DataRoot/SQLite file and collided on
/// <c>CREATE TABLE audit_events</c> during EF migration (or, more subtly,
/// shared a live database and tripped each other's ASP.NET Identity account
/// lockout). The prior fix serialized "set env var" + "boot host" behind a
/// process-wide gate (<c>TestHostBootGate</c>) and, when that still proved
/// flaky under full-suite load, disabled assembly-level test parallelization
/// entirely.
/// </para>
/// <para>
/// <b>Why <c>IWebHostBuilder.ConfigureAppConfiguration</c> does not work
/// here.</b> That was tried first, since <c>Program.Main</c> already reads
/// storage roots via <c>builder.Configuration</c> rather than
/// <c>Environment.GetEnvironmentVariable</c> directly. Empirically (see the
/// lane's stress run), an in-memory configuration source added via
/// <c>ConfigureAppConfiguration</c> in
/// <c>WebApplicationFactory.ConfigureWebHost</c> is NOT visible to
/// <c>Program.Main</c>'s synchronous reads that happen between
/// <c>WebApplication.CreateBuilder(args)</c> and <c>builder.Build()</c> for
/// this explicit-<c>Main</c>, <c>WebApplicationBuilder</c>-based minimal
/// hosting model — the test-host plumbing applies it too late (it showed up
/// as every "isolated" test host silently falling back to the SAME default
/// DataRoot and sharing one database). No environment-variable-free
/// injection point existed at the <c>IConfiguration</c> layer, so this
/// ambient override is the minimal seam that keeps storage injection
/// non-global.
/// </para>
/// <para>
/// <b>Why this is safe/minimal.</b> <see cref="Current"/> is <c>null</c>
/// unless a test explicitly calls <see cref="Push"/> — production startup
/// never touches this type, so behavior is byte-for-byte unchanged when no
/// test is involved. It is read in exactly one place
/// (<c>Program.Main</c>'s four storage-root/worker/rate-limit resolution
/// lines) and written only by test factories. It is an
/// <see cref="AsyncLocal{T}"/>, not a <c>static</c> field, so it is scoped to
/// one logical call stack rather than the whole process: a test factory
/// pushes its override immediately before triggering the synchronous host
/// boot (<c>WebApplicationFactory.CreateClient()</c>/<c>Server</c>) on its
/// OWN call stack, and pops it in a <c>using</c> block around that same
/// call — no thread hop occurs between the push and
/// <c>Program.Main</c> reading it (the test-host entry-point invocation is
/// synchronous), so concurrent factories booting on different threads never
/// observe each other's override.
/// </para>
/// </remarks>
public static class TestHostStorageOverride
{
    private static readonly AsyncLocal<StorageRootOverride?> _current = new();

    /// <summary>
    /// The override in effect for the current logical call stack, or
    /// <c>null</c> in production and whenever no test has pushed one.
    /// </summary>
    public static StorageRootOverride? Current => _current.Value;

    /// <summary>
    /// Pushes <paramref name="roots"/> as the ambient override for the
    /// duration of the returned scope. Dispose the scope on the SAME
    /// synchronous call stack used to push it, wrapped around the host boot
    /// call (e.g. <c>WebApplicationFactory.CreateClient()</c>) — do not
    /// <c>await</c> between push and boot in a way that could hop threads
    /// and drop the <see cref="AsyncLocal{T}"/> flow.
    /// </summary>
    public static IDisposable Push(StorageRootOverride roots)
    {
        var previous = _current.Value;
        _current.Value = roots;
        return new PopScope(previous);
    }

    private sealed class PopScope(StorageRootOverride? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}

/// <summary>
/// Storage-root override payload for <see cref="TestHostStorageOverride"/>.
/// <see cref="WorkerExecutablePath"/> and <see cref="RateLimitDisabled"/> are
/// optional — when null, <c>Program.Main</c> falls back to its normal
/// <c>IConfiguration</c> resolution for that value. <see cref="ExtraConfiguration"/>
/// covers any other pre-<c>Build()</c> configuration read in <c>Program.Main</c>
/// (e.g. <c>MangaPlex:Media:MaxConcurrentJobs</c>) that a test needs to pin —
/// keyed exactly like the corresponding <c>IConfiguration</c> key.
/// </summary>
public sealed record StorageRootOverride(
    string DataRoot,
    string CacheRoot,
    string ScratchRoot,
    string? WorkerExecutablePath = null,
    bool? RateLimitDisabled = null,
    IReadOnlyDictionary<string, string?>? ExtraConfiguration = null);
