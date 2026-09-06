namespace com.lifepixer.mangaplex.Core.WorkerProtocol;

using com.lifepixer.mangaplex.Core.Media;
using System.Text.Json;

/// <summary>
/// Worker protocol version. Incremented when the IPC message schema changes.
/// The server checks this on worker startup and refuses incompatible workers.
/// </summary>
public static class WorkerProtocolVersion
{
    public const int Current = 1;
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
/// </summary>
public sealed record AnalyzeRequest
{
    /// <summary>
    /// Server-assigned job ID (opaque).
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Absolute path to the archive file on the worker's filesystem.
    /// This is a scratch-side path, never a source media path.
    /// In production, the server copies or mounts the file for the worker.
    /// </summary>
    public required string ArchivePath { get; init; }

    /// <summary>
    /// Content version of the source archive (for manifest stamping).
    /// </summary>
    public required long ContentVersion { get; init; }

    /// <summary>
    /// Deadline for the analysis operation.
    /// </summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>
    /// Maximum uncompressed bytes to extract.
    /// </summary>
    public long MaxUncompressedBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Maximum number of entries to enumerate.
    /// </summary>
    public int MaxEntryCount { get; init; } = 10_000;

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
