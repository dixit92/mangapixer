// Test parallelization for the MangaPixer.Server.Tests assembly.
//
// Most test classes here boot an ASP.NET host via WebApplicationFactory
// (MangaPixerWebApplicationFactory / C00WebApplicationFactory /
// LogLevelWebApplicationFactory). Each factory instance injects its own
// storage roots (DataRoot/CacheRoot/ScratchRoot) via TestHostStorageOverride,
// a test-only ambient (AsyncLocal<T>-based) override consulted at the top of
// Program.Main — see the remarks on MangaPixerWebApplicationFactory
// (tests/MangaPixer.Server.Tests/Http/MangaPixerWebApplicationFactory.cs) for
// the full seam and why a plain IWebHostBuilder.ConfigureAppConfiguration
// override does not work for this minimal-hosting entry point. Because each
// factory pushes its own unique DataRoot/CacheRoot/ScratchRoot immediately
// before triggering its host's boot, two factories booting concurrently can
// never resolve the same SQLite file, so they can't collide on schema
// migration (previously a source of intermittent "table already exists"
// failures when storage roots were shared across concurrently-booting
// hosts).
//
// Assembly-level parallelization is therefore enabled (no
// [assembly: CollectionBehavior(DisableTestParallelization = true)]).
// Classes that boot a WebApplicationFactory still share the "HttpSerial"
// collection (see HttpTestCollection.cs) so that no two of them boot a host
// at the same time — this exists only because every host boot reassigns the
// process-global Serilog Log.Logger, not because of storage. Non-host-booting
// Server.Tests classes, and the separate MangaPixer.Core.Tests /
// MangaPixer.MediaWorker.Tests projects, run fully in parallel with this
// collection.

namespace com.lifepixer.mangapixer.Tests.Server;

/// <summary>
/// Thread-pool floor for the test assembly. xUnit runs up to one test per core at once, and every host-booting
/// test blocks its worker thread synchronously while the host boots (the factory constructors call
/// <c>CreateClient()</c>). With the pool at its default minimum (one thread per core) those blocked workers
/// starve the async continuations of the other tests, and the pool only adds a thread every ~0.5 s, so
/// long-running async tests slowed several-fold under full parallelism. A higher floor makes the pool create
/// threads on demand.
/// </summary>
internal static class TestThreadPool
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Configure()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        ThreadPool.SetMinThreads(Math.Max(worker, 256), Math.Max(io, 256));
    }
}
