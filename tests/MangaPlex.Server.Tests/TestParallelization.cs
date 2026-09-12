// Test parallelization for the MangaPlex.Server.Tests assembly.
//
// HISTORY (1.8.0 -> 1.9.0 Lane C "test-host-isolation"):
// Most test classes here boot an ASP.NET host via WebApplicationFactory
// (MangaPlexWebApplicationFactory / C00WebApplicationFactory /
// LogLevelWebApplicationFactory). Before 1.9.0, MangaPlex's storage roots
// (DataRoot/CacheRoot/ScratchRoot) were injected into each test host via
// PROCESS-GLOBAL environment variables (Environment.SetEnvironmentVariable),
// because those are what builder.Configuration picks up by default. When two
// hosts booted concurrently the env vars raced: both could resolve the same
// SQLite file and both ran the EF InitialCreate migration against it,
// colliding on "CREATE TABLE audit_events" -> SQLite Error 1 "table
// audit_events already exists". This was the long-carried
// OpenApiSnapshot/HostingCorrectness flake (it moved between whichever two
// host-booting tests happened to overlap). A per-factory boot gate
// (TestHostBootGate, 1.8.0) reduced but did not eliminate it under full-suite
// load, so 1.8.0 disabled assembly-level parallelization entirely as a
// can't-miss fix (a few minutes slower, but no test class could be forgotten,
// unlike per-class [Collection] annotations).
//
// FIX (1.9.0): each WebApplicationFactory instance now injects its storage
// roots via IWebHostBuilder.ConfigureAppConfiguration — an in-memory
// configuration source scoped to that instance's own IConfiguration/builder —
// instead of a shared environment variable. Program.Main resolves
// DataRoot/CacheRoot/ScratchRoot from builder.Configuration (see ResolveRoot
// in Program.cs), and the ASP.NET Core minimal-hosting test-host plumbing
// applies each factory's ConfigureAppConfiguration additions to that same
// factory's ConfigurationManager before Program.Main reads it. Two factories
// booting concurrently now always resolve two different, unique DataRoot
// paths, so they can never collide on the same SQLite file — the boot race
// is eliminated by construction, not by serializing it away. See the
// remarks on MangaPlexWebApplicationFactory (tests/MangaPlex.Server.Tests/
// Http/MangaPlexWebApplicationFactory.cs) for the full seam.
//
// No product code changed to make this possible — Program.cs already read
// storage roots through IConfiguration rather than Environment directly, so
// ConfigureAppConfiguration overrides were visible to it all along; only the
// test factories' injection method changed. TestHostBootGate
// (tests/MangaPlex.TestSupport/Hosting/TestHostBootGate.cs) is no longer
// used by any factory in this assembly but is left in place as a public
// TestSupport type in case another in-flight lane still references it.
//
// Assembly-level parallelization is therefore restored (no
// [assembly: CollectionBehavior(DisableTestParallelization = true)]) — see
// the lane's vault note (MangaPlex - Server Test Host Isolation, 1.9.0) for
// the multi-run stress results confirming zero audit_events collisions.
