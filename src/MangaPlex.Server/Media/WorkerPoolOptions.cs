namespace com.lifepixer.mangaplex.Server.Media;

/// <summary>
/// Configuration options for the media worker pool.
/// All timeouts are storage-aware and configurable for different deployment targets
/// (e.g., Unraid servers with spun-down hard drives).
/// </summary>
public sealed class WorkerPoolOptions
{
    /// <summary>
    /// Maximum number of concurrent worker jobs. Default: 2.
    /// A one-worker configuration is supported for low-memory NAS systems.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>
    /// Worker startup handshake timeout. Default: 15 seconds (no media access).
    /// </summary>
    public TimeSpan StartupHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Initial source open/read allowance for cold-drive spin-up. Default: 120 seconds.
    /// Applies once per job attempt, not per entry/page.
    /// </summary>
    public TimeSpan SourceOpenTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Quick metadata probe timeout after source access. Default: 30 seconds.
    /// </summary>
    public TimeSpan QuickProbeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Archive analysis/image processing timeout. Default: 120 seconds.
    /// </summary>
    public TimeSpan AnalysisTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Solid archive preparation timeout. Default: 300 seconds.
    /// </summary>
    public TimeSpan SolidPreparationTimeout { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Cooperative cancellation grace period before force-killing. Default: 5 seconds.
    /// </summary>
    public TimeSpan CancellationGracePeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum consecutive startup failures before pausing automatic launches. Default: 5.
    /// </summary>
    public int MaxConsecutiveStartupFailures { get; set; } = 5;

    /// <summary>
    /// Path to the media worker executable. If null, the server attempts to discover it.
    /// </summary>
    public string? WorkerExecutablePath { get; set; }

    /// <summary>
    /// Scratch root directory for worker workspaces.
    /// </summary>
    public string ScratchRoot { get; set; } = string.Empty;

    /// <summary>
    /// Total scratch budget in bytes. Default: 2 GiB.
    /// </summary>
    public long ScratchBudgetBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Cache root directory for published derived artifacts.
    /// </summary>
    public string CacheRoot { get; set; } = string.Empty;

    /// <summary>
    /// Total cache budget in bytes. Default: 10 GiB.
    /// </summary>
    public long CacheBudgetBytes { get; set; } = 10L * 1024 * 1024 * 1024;
}

/// <summary>
/// Job priority levels. Higher priority jobs are dispatched first.
/// Reader demand always has priority over background analysis.
/// </summary>
public enum JobPriority
{
    /// <summary>
    /// Background analysis or thumbnail generation. Lowest priority.
    /// </summary>
    Background = 0,

    /// <summary>
    /// Near-future page prefetch. Medium priority.
    /// </summary>
    Prefetch = 1,

    /// <summary>
    /// Current page request from an active reader. Highest priority.
    /// </summary>
    CurrentPage = 2,
}

/// <summary>
/// Job operation types.
/// </summary>
public enum JobOperation
{
    /// <summary>
    /// Analyze an archive and produce a manifest.
    /// </summary>
    Analyze = 0,

    /// <summary>
    /// Extract and probe a single page image.
    /// </summary>
    ExtractPage = 1,

    /// <summary>
    /// Generate a cover thumbnail.
    /// </summary>
    GenerateCover = 2,

    /// <summary>
    /// Prepare a solid archive for sequential extraction.
    /// </summary>
    PrepareSolid = 3,
}
