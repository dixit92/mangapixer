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
    /// Total scratch budget in bytes. Default: 1 GiB. Override via
    /// MangaPlex:Storage:ScratchBudgetBytes. Only solid RAR/7z extraction uses
    /// meaningful scratch (sequential decompression); ZIP page extraction is
    /// random-access and uses almost none.
    /// </summary>
    public long ScratchBudgetBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Cache root directory for published derived artifacts.
    /// </summary>
    public string CacheRoot { get; set; } = string.Empty;

    /// <summary>
    /// Total derived-cache budget in bytes. Default: 1 GiB. Override via
    /// MangaPlex:Storage:CacheBudgetBytes. The cache holds individual page images
    /// (per page, per variant) under LRU eviction — never whole archives — so this
    /// bounds disk regardless of library size. At ~300 KB/page, 1 GiB is ~3,000
    /// cached pages. A byte budget is used (not a page count) because page sizes
    /// vary ~10x and each page can have several variants (original/webp/thumb/strip).
    /// </summary>
    public long CacheBudgetBytes { get; set; } = 1L * 1024 * 1024 * 1024;
}

/// <summary>
/// Configuration for the continuous thumbnail backfill (post-1.2.0). The backfill
/// generates durable thumbnails for ready archive items that lack a current
/// thumbnail, in bounded batches, yielding when the worker pool is saturated or
/// analysis work is pending so it does not starve interactive reader reads.
/// Override via <c>MangaPlex:Media:ThumbnailBackfill:*</c>.
/// </summary>
public sealed class ThumbnailBackfillOptions
{
    /// <summary>
    /// Number of items queried per batch in the continuous backfill loop. Bounded
    /// so a very large library does not load one huge list into memory. Default: 200.
    /// </summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// Delay between saturation/backoff polls (milliseconds) when the worker pool
    /// is saturated or analysis work is pending. Default: 200ms.
    /// </summary>
    public int BackoffMs { get; set; } = 200;
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
