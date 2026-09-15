namespace com.lifepixer.mangaplex.Core.WorkerProtocol;

using com.lifepixer.mangaplex.Core.Media;
using System.Text.Json;

/// <summary>
/// Worker protocol version. Incremented when the IPC message schema changes.
/// The server checks this on worker startup and refuses incompatible workers.
/// </summary>
public static class WorkerProtocolVersion
{
    // v2: added on-demand page extraction (extract / extract_result / extract_error)
    // with worker-side WebP transcode + thumbnail variants.
    public const int Current = 2;
}

/// <summary>
/// Envelope for all worker IPC messages. Framed JSON over stdin/stdout.
/// Each message is a single JSON object terminated by a newline (JSON-lines).
/// </summary>
public sealed record WorkerEnvelope
{
    /// <summary>
    /// Message type identifier (e.g., "analyze", "cancel", "ready").
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Correlation ID matching request to response.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Protocol version.
    /// </summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>
    /// Message payload (type-specific).
    /// </summary>
    public required JsonElement Payload { get; init; }
}

// Using System.Text.Json.JsonElement for polymorphic payload.
// The server deserializes the payload based on the Type field.

/// <summary>
/// Server -> Worker: Analyze an archive and produce a manifest.
/// The worker opens the source archive directly in read-only mode.
/// </summary>
public sealed record AnalyzeRequest
{
    /// <summary>
    /// Server-assigned job ID (opaque).
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Absolute path to the source archive file on the worker's filesystem.
    /// This is a private validated source locator — the worker opens it read-only.
    /// Never appears in public HTTP DTOs or logs. The server validates containment
    /// and source identity before dispatching this request.
    /// </summary>
    public required string ArchivePath { get; init; }

    /// <summary>
    /// Content version of the source archive (for manifest stamping).
    /// </summary>
    public required long ContentVersion { get; init; }

    /// <summary>
    /// Expected source stamp (last write ticks + byte length) for pre/post validation.
    /// The worker discards results if the source changes during processing.
    /// </summary>
    public required long ExpectedLastWriteTicks { get; init; }
    public required long ExpectedByteLength { get; init; }

    /// <summary>
    /// Scratch workspace path allocated by the server for this job attempt.
    /// The worker writes only to this directory. Server-generated opaque name.
    /// </summary>
    public required string ScratchWorkspacePath { get; init; }

    /// <summary>
    /// Deadline for the analysis operation.
    /// </summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>
    /// Maximum uncompressed bytes to extract (cumulative).
    /// </summary>
    public long MaxUncompressedBytes { get; init; } = 32L * 1024 * 1024 * 1024;

    /// <summary>
    /// Maximum number of entries to enumerate.
    /// </summary>
    public int MaxEntryCount { get; init; } = 50_000;

    /// <summary>
    /// Maximum image dimensions to probe.
    /// </summary>
    public int MaxImageDimension { get; init; } = 50_000;
}

/// <summary>
/// Worker -> Server: Analysis completed successfully.
/// </summary>
public sealed record AnalyzeResult
{
    public required string JobId { get; init; }
    public required ArchiveFormat ArchiveFormat { get; init; }
    public required bool IsSolid { get; init; }
    public required bool IsEncrypted { get; init; }
    public required IReadOnlyList<AnalyzedPageEntry> Pages { get; init; }
    public required long TotalUncompressedBytes { get; init; }
    public required TimeSpan ElapsedTime { get; init; }

    /// <summary>
    /// Observed source stamp after processing. Server compares to expected stamp
    /// to detect source changes during processing.
    /// </summary>
    public required long ObservedLastWriteTicks { get; init; }
    public required long ObservedByteLength { get; init; }
}

/// <summary>
/// Worker -> Server: Analysis failed.
/// </summary>
public sealed record AnalyzeError
{
    public required string JobId { get; init; }
    public required string ErrorType { get; init; }
    public required string ErrorMessage { get; init; }

    /// <summary>
    /// Whether the error is recoverable (retry might succeed).
    /// </summary>
    public bool Recoverable { get; init; }
}

/// <summary>
/// A single page entry from worker analysis.
/// </summary>
public sealed record AnalyzedPageEntry
{
    /// <summary>
    /// Zero-based ordinal in the archive's natural entry order.
    /// </summary>
    public required int Ordinal { get; init; }

    /// <summary>
    /// Safe source entry key (the archive entry path/key). Private IPC data only.
    /// The server maps this to an opaque EntryKey for public DTOs.
    /// </summary>
    public required string SourceEntryKey { get; init; }

    /// <summary>
    /// Image media type, if the entry is an image.
    /// </summary>
    public required string MediaType { get; init; }

    /// <summary>
    /// Pixel width, if known.
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Pixel height, if known.
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// Animation state.
    /// </summary>
    public AnimationState AnimationState { get; init; }

    /// <summary>
    /// Source entry byte size.
    /// </summary>
    public long ByteSize { get; init; }

    /// <summary>
    /// Whether this entry is a supported, readable image page.
    /// </summary>
    public bool IsSupported { get; init; } = true;
}

/// <summary>
/// Server -> Worker: extract a single page image and encode a variant on demand.
/// The worker opens the source archive read-only, extracts exactly the one entry,
/// transcodes it, and writes the encoded bytes to <see cref="OutputPath"/>. The
/// server never opens the archive itself — this keeps image decoding (the
/// untrusted-input risk) inside the worker's fault-isolation boundary.
/// </summary>
public sealed record ExtractRequest
{
    public required string JobId { get; init; }

    /// <summary>Absolute path to the source archive (private locator, worker-side).</summary>
    public required string ArchivePath { get; init; }

    /// <summary>The archive-internal entry path/key to extract (private locator).</summary>
    public required string SourceEntryKey { get; init; }

    /// <summary>
    /// Variant to produce: "webp" (full-size WebP), "thumbnail" (≤ThumbnailMaxDimension
    /// WebP), or "original" (verbatim source bytes, no transcode).
    /// </summary>
    public required string Variant { get; init; }

    /// <summary>Expected source stamp; the worker refuses if the source changed.</summary>
    public required long ExpectedLastWriteTicks { get; init; }
    public required long ExpectedByteLength { get; init; }

    /// <summary>Absolute path the worker writes the encoded output to (server-owned).</summary>
    public required string OutputPath { get; init; }

    /// <summary>Deadline for the extraction.</summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>Longest edge (px) for the "thumbnail" variant.</summary>
    public int ThumbnailMaxDimension { get; init; } = 320;

    /// <summary>WebP quality (0–100) for lossy transcode.</summary>
    public int WebpQuality { get; init; } = 82;
}

/// <summary>
/// Worker -> Server: extraction succeeded; the encoded image is at OutputPath.
/// </summary>
public sealed record ExtractResult
{
    public required string JobId { get; init; }
    public required string OutputPath { get; init; }
    public required string MediaType { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long ByteSize { get; init; }
}

/// <summary>
/// Worker -> Server: extraction failed (unsupported format, solid archive not yet
/// supported, missing entry, decode error, or source changed).
/// </summary>
public sealed record ExtractError
{
    public required string JobId { get; init; }
    public required string ErrorType { get; init; }
    public required string ErrorMessage { get; init; }
    public bool Recoverable { get; init; }
}

/// <summary>
/// Server -> Worker: Cancel an in-progress job.
/// </summary>
public sealed record CancelRequest
{
    public required string JobId { get; init; }
}

/// <summary>
/// Worker -> Server: Heartbeat/progress update.
/// </summary>
public sealed record WorkerProgress
{
    public required string JobId { get; init; }
    public required int EntriesProcessed { get; init; }
    public required int TotalEntries { get; init; }
    public required long BytesProcessed { get; init; }
}

/// <summary>
/// Worker -> Server: State change notification (e.g., "waiting_for_storage", "processing").
/// Used to surface internal/user-facing readiness state before and during processing.
/// </summary>
public sealed record WorkerState
{
    public required string JobId { get; init; }
    public required string State { get; init; }
}

/// <summary>
/// Worker -> Server: Ready handshake on startup.
/// </summary>
public sealed record WorkerReady
{
    public required int ProtocolVersion { get; init; }
    public required string WorkerVersion { get; init; }
    public required string Runtime { get; init; }
}

/// <summary>
/// Server -> Worker: Shutdown signal.
/// </summary>
public sealed record WorkerShutdown
{
    public required string Reason { get; init; }
}
