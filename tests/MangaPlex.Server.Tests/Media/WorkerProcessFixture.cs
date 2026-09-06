namespace com.lifepixer.mangaplex.Tests.Server.Media;

using System.Reflection;
using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.TestSupport.Fixtures;

/// <summary>
/// Fixture for worker-process integration tests (C04, D13).
/// Provides a temp scratch root, resolved worker launch path, and
/// helper methods to create synthetic archives.
/// </summary>
public sealed class WorkerProcessFixture : IDisposable
{
    public string TempRoot { get; }
    public string ScratchRoot { get; }
    public string FixtureDir { get; }
    public string WorkerExePath { get; }
    public string WorkerArguments { get; }

    public WorkerProcessFixture()
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "mangaplex-c04-" + Guid.NewGuid().ToString("N")[..8]);
        ScratchRoot = Path.Combine(TempRoot, "scratch");
        FixtureDir = Path.Combine(TempRoot, "fixtures");
        Directory.CreateDirectory(ScratchRoot);
        Directory.CreateDirectory(FixtureDir);

        // Resolve the worker DLL path — the test project references
        // MangaPlex.MediaWorker so the DLL lands in the test output dir.
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var workerDll = Path.Combine(assemblyDir, "MangaPlex.MediaWorker.dll");
        if (!File.Exists(workerDll))
        {
            // Dev fallback
            workerDll = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "MangaPlex.MediaWorker", "bin", "Debug", "net10.0", "MangaPlex.MediaWorker.dll"));
        }
        WorkerExePath = "dotnet";
        WorkerArguments = workerDll;
    }

    public WorkerPoolOptions CreatePoolOptions() => new()
    {
        MaxConcurrentJobs = 2,
        StartupHandshakeTimeout = TimeSpan.FromSeconds(15),
        AnalysisTimeout = TimeSpan.FromSeconds(30),
        SourceOpenTimeout = TimeSpan.FromSeconds(10),
        WorkerExecutablePath = WorkerArguments, // .dll path
        ScratchRoot = ScratchRoot,
    };

    public string CreateSimpleZip(string name = "simple.zip") =>
        ZipFixtureGenerator.CreateZip(FixtureDir, name, "page001.png", "page002.png", "page003.png");

    public string CreateLargeZip(string name = "large.zip", int entries = 2000)
    {
        var paths = new string[entries];
        for (int i = 0; i < entries; i++)
            paths[i] = $"page{i:D4}.png";
        return ZipFixtureGenerator.CreateZip(FixtureDir, name, paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(TempRoot, true); } catch { }
    }
}
