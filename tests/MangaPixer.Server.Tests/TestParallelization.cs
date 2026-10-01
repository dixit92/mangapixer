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
// [assembly: CollectionBehavior(DisableTestParallelization = true)]), and every
// host-booting class is its own xUnit collection, so up to one test per core runs at
// once and host-booting classes boot hosts concurrently.
//
// What makes that safe (each point is a rule for new tests):
//   * Logging is per host. Program.Main builds its Serilog logger as a local and
//     registers it in DI; nothing reads or writes the process-global Serilog.Log.Logger.
//     A test that wants to see log events wraps the HOST's logger through
//     TestHostLogging.Wrap (Hosting/TestHostLogging.cs); it never assigns Log.Logger.
//   * Storage is per host (TestHostStorageOverride, see above).
//   * Configuration is per host (MangaPixerWebApplicationFactory.WithExtraConfiguration);
//     a test never sets an environment variable or the current directory.
//   * Hosts use the in-memory TestServer, so there are no ports to collide on.
//
// The "HttpSerial" collection (Http/HttpTestCollection.cs) is kept only so that a class
// carrying [Collection("HttpSerial")] still compiles: the members of one collection run
// one after another, so such a class is merely scheduled behind its collection mates. New
// test classes do not need it.
//
// Run the tests with TMPDIR on a RAM disk when the temp folder is on a slow or network
// disk: every host creates and migrates its own SQLite database, so a boot costs ~0.5 s
// on tmpfs and 3-9 s on a btrfs/overlay disk with expensive fsyncs.
