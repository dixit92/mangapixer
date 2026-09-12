using Xunit;

// Serialize the MangaPlex.Server.Tests assembly.
//
// Most test classes here boot an ASP.NET host via WebApplicationFactory
// (MangaPlexWebApplicationFactory / C00WebApplicationFactory /
// LogLevelWebApplicationFactory). MangaPlex resolves its storage roots
// (DataRoot/CacheRoot/ScratchRoot) from PROCESS-GLOBAL environment variables at
// the top of Program.Main, and each host runs the EF InitialCreate migration at
// startup. When two hosts boot concurrently the env vars race: both can resolve
// the same SQLite file and both run InitialCreate, colliding on
// "CREATE TABLE audit_events" -> SQLite Error 1 "table audit_events already
// exists". This was the long-carried OpenApiSnapshot/HostingCorrectness flake
// (it moves between whichever two host-booting tests happen to overlap).
//
// The 1.8.0 per-factory boot gate (TestHostBootGate) reduced but did not
// eliminate it under full-suite load. Disabling assembly-level parallelization
// removes the concurrency entirely, which is a can't-miss fix for a boot race
// (no test class can be forgotten, unlike per-class [Collection] annotations).
// Trade-off: the Server suite runs serially (a few minutes slower). A
// parallelism-preserving fix (e.g. a process-config injection point that does
// not go through global env vars) is tracked in the backlog for a later cycle.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
