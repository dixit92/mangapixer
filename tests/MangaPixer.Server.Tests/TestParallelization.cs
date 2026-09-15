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
