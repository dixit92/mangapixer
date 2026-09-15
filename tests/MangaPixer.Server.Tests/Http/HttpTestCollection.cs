namespace com.lifepixer.mangapixer.Tests.Server.Http;

using Xunit;

/// <summary>
/// Serializes every WebApplicationFactory-booting Server.Tests class
/// relative to each other. Each test class gets its own factory instance
/// (and its own isolated temp DataRoot/CacheRoot/ScratchRoot).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why storage isolation is no longer the reason for this collection.</b> The
/// original motivation for this collection — factories racing on a shared,
/// process-global storage environment variable — no longer applies: storage
/// roots are now injected per-instance via <c>TestHostStorageOverride</c>,
/// an <see cref="System.Threading.AsyncLocal{T}"/>-based ambient override
/// consulted at the top of <c>Program.Main</c> (see the remarks on
/// <c>MangaPixerWebApplicationFactory</c> for why a plain
/// <c>ConfigureAppConfiguration</c> override — tried first — does not
/// work for this minimal-hosting entry point). Two factories booting
/// concurrently can therefore never collide on the same DB file, which is
/// what let <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c>
/// be removed (see TestParallelization.cs).
/// </para>
/// <para>
/// <b>Why this collection still serializes (DisableParallelization = true)
/// its members.</b> Independent of storage, EVERY host boot
/// unconditionally reassigns the process-global Serilog <c>Log.Logger</c>
/// static near the top of <c>Program.Main</c>. Two of this collection's
/// classes (<c>LogLevelHttpTests</c>, <c>HostingCorrectnessTests</c>) wrap
/// whatever logger is current at boot time with their own collecting sink
/// to capture log assertions — a concurrently booting host from ANY other
/// class (this collection or not) can clobber that wrapper, or have its own
/// boot's logger silently swallowed by an already-installed wrapper. This
/// surfaced as an intermittent <c>HostingCorrectnessTests</c> failure the
/// first time assembly-level parallelization was restored without this
/// membership change. Consequently every class that boots a
/// <c>WebApplicationFactory</c> in this test project — the ~16 original
/// HTTP classes plus <c>OpenApiSnapshotTests</c>, <c>HostingCorrectnessTests</c>,
/// and <c>WorkerConcurrencyConfigTests</c> — now shares this ONE collection,
/// so no two of them ever boot a host concurrently. Non-host-booting
/// Server.Tests classes (pure unit / service-with-DB tests that never touch
/// <c>Program.Main</c>), and the separate MangaPixer.Core.Tests /
/// MangaPixer.MediaWorker.Tests projects, still run fully in parallel with
/// this collection — parallelism is materially restored, just not inside
/// this specific group, which was never the source of runtime cost driving
/// the "a few minutes slower" complaint that prompted this lane.
/// </para>
/// </remarks>
[CollectionDefinition("HttpSerial", DisableParallelization = true)]
public sealed class HttpTestCollection
{
}
