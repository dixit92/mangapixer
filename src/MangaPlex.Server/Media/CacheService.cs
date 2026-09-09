namespace com.lifepixer.mangaplex.Server.Media;

using System.Collections.Concurrent;
using System.IO;

/// <summary>
/// Bounded derived-cache service with atomic publication, LRU eviction,
/// and active-file pinning. Cache entries are file-backed (not BLOBs).
///
/// Rules:
/// - Atomic publish: write to temp file, then rename within the cache filesystem.
/// - Cross-filesystem: copy to cache-local temp, then rename.
/// - LRU eviction: evict least-recently-accessed entries when over quota.
/// - Active-file pinning: never evict a file being actively streamed.
/// - Cache miss is recoverable: eviction only affects derived entries.
/// - Never evict source media.
/// - Access writes are coalesced, never one SQLite write per image hit.
/// </summary>
public sealed class CacheService
{
    private readonly string _cacheRoot;
    private readonly long _budgetBytes;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly ConcurrentDictionary<string, int> _pinnedFiles = new();
    private readonly object _evictionLock = new();
    private readonly ILogger<CacheService>? _logger;

    public CacheService(string cacheRoot, long budgetBytes, ILogger<CacheService>? logger = null)
    {
        _cacheRoot = Path.GetFullPath(cacheRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _budgetBytes = budgetBytes;
        _logger = logger;
    }

    /// <summary>
    /// Directory for short-lived temp bytes staged before publish. Lives UNDER
    /// the app-managed cache root — never the system temp dir — so page bytes
    /// stay within the scratch/cache boundary (audit finding A2).
    /// </summary>
    public string ScratchDirectory => Path.Combine(_cacheRoot, "_tmp");

    /// <summary>
    /// Initializes the cache root and scratch directories.
    /// </summary>
    public void Initialize()
    {
        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(ScratchDirectory);
    }

    /// <summary>
    /// Builds a cache key from item/content version/entry key/variant components.
    /// </summary>
    public static string BuildCacheKey(long itemId, long contentVersion, string entryKey, string variant)
    {
        return $"{itemId}:{contentVersion}:{entryKey}:{variant}";
    }

    /// <summary>
    /// Returns the cache file path for a given key. Does not check existence.
    /// </summary>
    public string GetCacheFilePath(string cacheKey)
    {
        // Use a hash to avoid filesystem path length issues
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant();
        return Path.Combine(_cacheRoot, hash[..2], hash);
    }

    /// <summary>
    /// Checks if a cache entry exists and is not evicted/corrupt.
    /// Updates last-accessed time (coalesced — not written to DB on every hit).
    /// </summary>
    public bool TryGet(string cacheKey, out CacheEntry? entry)
    {
        if (_entries.TryGetValue(cacheKey, out var e) && e.State == CacheEntryState.Active)
        {
            var filePath = GetCacheFilePath(cacheKey);
            if (File.Exists(filePath))
            {
                e.LastAccessedAt = DateTimeOffset.UtcNow;
                entry = e;
                _logger?.LogDebug("Cache hit (key {CacheKey}, {Bytes} bytes)", cacheKey, e.ByteSize);
                return true;
            }

            // File missing — mark as evicted
            _logger?.LogDebug("Cache entry present but file missing (key {CacheKey}); marking evicted", cacheKey);
            e.State = CacheEntryState.Evicted;
        }

        entry = null;
        return false;
    }

    /// <summary>
    /// Opens a cache file for reading. Pins the file while the stream is open.
    /// Returns null if the cache entry doesn't exist.
    /// </summary>
    public Stream? OpenRead(string cacheKey)
    {
        if (!TryGet(cacheKey, out var entry) || entry is null)
            return null;

        var filePath = GetCacheFilePath(cacheKey);
        try
        {
            var stream = new PinnedFileStream(filePath, cacheKey, this);
            return stream;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Atomically publishes a file into the cache. Writes to a temp file first,
    /// then renames within the cache filesystem. If the cache and source are on
    /// different filesystems, copies to a cache-local temp first.
    /// </summary>
    public async Task PublishAsync(string cacheKey, string sourceFilePath, string mediaType, CancellationToken ct = default)
    {
        var cacheFilePath = GetCacheFilePath(cacheKey);
        var cacheDir = Path.GetDirectoryName(cacheFilePath)!;

        Directory.CreateDirectory(cacheDir);

        // Write to a temp file in the same directory (same filesystem for atomic rename)
        var tempPath = cacheFilePath + ".tmp";

        try
        {
            // Copy from source to temp file in cache directory
            using var source = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(dest, ct);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        // Atomic rename within the cache filesystem
        if (File.Exists(cacheFilePath))
            File.Delete(cacheFilePath);

        File.Move(tempPath, cacheFilePath);

        // Record the entry
        var fileInfo = new FileInfo(cacheFilePath);
        _entries[cacheKey] = new CacheEntry
        {
            CacheKey = cacheKey,
            CacheFilePath = cacheFilePath,
            ByteSize = fileInfo.Length,
            MediaType = mediaType,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            State = CacheEntryState.Active,
        };

        _logger?.LogDebug("Cache published (key {CacheKey}, {Bytes} bytes); usage now {Usage} bytes",
            cacheKey, fileInfo.Length, GetCurrentUsageBytes());

        // Check if we need to evict
        TryEvict();
    }

    /// <summary>
    /// Publishes a stream directly into the cache (for worker output).
    /// </summary>
    public async Task PublishStreamAsync(string cacheKey, Stream source, string mediaType, CancellationToken ct = default)
    {
        var cacheFilePath = GetCacheFilePath(cacheKey);
        var cacheDir = Path.GetDirectoryName(cacheFilePath)!;

        Directory.CreateDirectory(cacheDir);

        var tempPath = cacheFilePath + ".tmp";

        try
        {
            using var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(dest, ct);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        if (File.Exists(cacheFilePath))
            File.Delete(cacheFilePath);

        File.Move(tempPath, cacheFilePath);

        var fileInfo = new FileInfo(cacheFilePath);
        _entries[cacheKey] = new CacheEntry
        {
            CacheKey = cacheKey,
            CacheFilePath = cacheFilePath,
            ByteSize = fileInfo.Length,
            MediaType = mediaType,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            State = CacheEntryState.Active,
        };

        _logger?.LogDebug("Cache published from stream (key {CacheKey}, {Bytes} bytes); usage now {Usage} bytes",
            cacheKey, fileInfo.Length, GetCurrentUsageBytes());

        TryEvict();
    }

    /// <summary>
    /// Pins a cache file so it won't be evicted while being actively streamed.
    /// </summary>
    internal void PinFile(string cacheKey)
    {
        _pinnedFiles.AddOrUpdate(cacheKey, 1, (_, count) => count + 1);
    }

    /// <summary>
    /// Unpins a cache file after streaming completes.
    /// </summary>
    internal void UnpinFile(string cacheKey)
    {
        _pinnedFiles.AddOrUpdate(cacheKey, 0, (_, count) => Math.Max(0, count - 1));
        if (_pinnedFiles.TryGetValue(cacheKey, out var count) && count <= 0)
        {
            _pinnedFiles.TryRemove(cacheKey, out _);
        }
    }

    /// <summary>
    /// Returns the current total cache usage in bytes.
    /// </summary>
    public long GetCurrentUsageBytes()
    {
        return _entries.Values
            .Where(e => e.State == CacheEntryState.Active)
            .Sum(e => e.ByteSize);
    }

    /// <summary>
    /// Number of active cache entries.
    /// </summary>
    public int ActiveCount => _entries.Values.Count(e => e.State == CacheEntryState.Active);

    /// <summary>
    /// Attempts to evict least-recently-accessed entries to stay under budget.
    /// Never evicts pinned files.
    /// </summary>
    private void TryEvict()
    {
        lock (_evictionLock)
        {
            var usage = GetCurrentUsageBytes();
            if (usage <= _budgetBytes)
                return;

            _logger?.LogDebug("Cache over budget: {Usage} bytes > {Budget} bytes; evicting LRU entries",
                usage, _budgetBytes);

            // Sort by last accessed (oldest first), exclude pinned
            var candidates = _entries.Values
                .Where(e => e.State == CacheEntryState.Active && !_pinnedFiles.ContainsKey(e.CacheKey))
                .OrderBy(e => e.LastAccessedAt)
                .ToList();

            var evictedCount = 0;
            foreach (var entry in candidates)
            {
                if (usage <= _budgetBytes)
                    break;

                try
                {
                    if (File.Exists(entry.CacheFilePath))
                        File.Delete(entry.CacheFilePath);

                    entry.State = CacheEntryState.Evicted;
                    usage -= entry.ByteSize;
                    evictedCount++;
                }
                catch
                {
                    // Failed to delete — skip, try next
                    _logger?.LogDebug("Cache eviction failed for entry (key {CacheKey}); skipping",
                        entry.CacheKey);
                }
            }

            if (evictedCount > 0)
            {
                _logger?.LogDebug("Cache evicted {Count} entries; usage now {Usage} bytes", evictedCount, usage);
            }
        }
    }

    /// <summary>
    /// Handles disk-full errors by stopping new derived work safely.
    /// Does not corrupt the database or delete unrelated files. Reserved for
    /// real <see cref="IOException"/> disk-full paths; routine over-budget
    /// eviction should use <see cref="EvictOverBudget"/> instead (audit defect D27).
    /// </summary>
    public void HandleDiskFull()
    {
        _logger?.LogWarning("Cache disk full — evicting least-recently-accessed entries");
        TryEvict();
    }

    /// <summary>
    /// Routine over-budget eviction pass. Evicts least-recently-accessed
    /// entries until usage is at or below the configured budget. Returns the
    /// number of bytes freed. Does not log at <c>Warning</c> level — this is
    /// the normal maintenance path, not a disk-full event (audit defect D27).
    /// </summary>
    public long EvictOverBudget()
    {
        var usageBefore = GetCurrentUsageBytes();
        if (usageBefore <= _budgetBytes)
            return 0;

        TryEvict();

        var usageAfter = GetCurrentUsageBytes();
        var freed = usageBefore - usageAfter;
        if (freed > 0)
        {
            _logger?.LogInformation(
                "Cache over-budget eviction freed {Bytes} bytes ({Before} -> {After})",
                freed, usageBefore, usageAfter);
        }
        return freed;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}

/// <summary>
/// Cache entry metadata.
/// </summary>
public sealed class CacheEntry
{
    public required string CacheKey { get; init; }
    public required string CacheFilePath { get; init; }
    public required long ByteSize { get; init; }
    public required string MediaType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastAccessedAt { get; set; }
    public CacheEntryState State { get; set; }
}

/// <summary>
/// Cache entry state.
/// </summary>
public enum CacheEntryState
{
    Active = 0,
    Evicted = 1,
    Corrupt = 2,
}

/// <summary>
/// A file stream that pins the cache entry while open and unpins on dispose.
/// This prevents LRU eviction from removing a file being actively streamed.
/// </summary>
internal sealed class PinnedFileStream : FileStream
{
    private readonly string _cacheKey;
    private readonly CacheService _cache;
    private bool _disposed;

    public PinnedFileStream(string path, string cacheKey, CacheService cache)
        : base(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    {
        _cacheKey = cacheKey;
        _cache = cache;
        _cache.PinFile(_cacheKey);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _cache.UnpinFile(_cacheKey);
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
