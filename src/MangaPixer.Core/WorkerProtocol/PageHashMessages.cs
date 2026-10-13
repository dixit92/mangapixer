namespace com.lifepixer.mangapixer.Core.WorkerProtocol;

// PROTOTYPE (1.43.0 lane V, phase 1 - measurement only). The page-hash op that the volume contents scan needs: hash every page
// of ONE archive. Not wired into the server and not covered by a protocol bump yet (the design gate decides the final shape;
// phase 2 bumps WorkerProtocolVersion.Current to 6 with it). The Probe* fields exist only to measure match quality and cost.

/// <summary>
/// Server -> Worker: hash every page of one archive, in reading order (the same entries and order as <c>analyze</c>, so
/// <see cref="PageHashEntry.Ordinal"/> is the stored page ordinal). The worker opens the archive READ-ONLY by the same private
/// validated source locator <c>analyze</c> / <c>extract</c> take, checks the source stamp before and after, writes nothing,
/// and answers a <c>page_hash_result</c> or a <c>page_hash_error</c> (<see cref="PageHashErrors"/>).
/// </summary>
public sealed record PageHashRequest
{
    public required string JobId { get; init; }

    /// <summary>Absolute path of the source archive (private validated source locator, worker-side; never logged).</summary>
    public required string ArchivePath { get; init; }

    public required long ExpectedLastWriteTicks { get; init; }
    public required long ExpectedByteLength { get; init; }

    /// <summary>The worker stops and answers <c>deadline</c> after this.</summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>An archive with more image pages is refused (<c>too_many_pages</c>) before any page is read.</summary>
    public int MaxPages { get; init; } = PageHashLimits.MaxPages;

    /// <summary>A single page larger than this (declared or read) is not decoded; its entry carries <c>too_large</c>.</summary>
    public long MaxPageBytes { get; init; } = PageHashLimits.MaxPageBytes;

    /// <summary>The archive's image pages together (declared sizes) above this are refused (<c>too_large</c>).</summary>
    public long MaxTotalBytes { get; init; } = PageHashLimits.MaxTotalBytes;

    /// <summary>Pages wider or taller than this are not decoded (<c>too_large</c> on the entry).</summary>
    public int MaxDimension { get; init; } = PageHashLimits.MaxDimension;

    /// <summary>SHA-256 of each page's bytes (exact copies).</summary>
    public bool Exact { get; init; } = true;

    /// <summary>The 64-bit perceptual hash of each page (<c>CoverHash</c>; re-encoded copies).</summary>
    public bool Perceptual { get; init; } = true;

    /// <summary>
    /// Decode JPEG pages at a reduced scale (libjpeg DCT scaling to at least 128 px) before the 32x32 reduction. Much cheaper;
    /// the prototype measures how far its hashes land from the full decode's.
    /// </summary>
    public bool FastDecode { get; init; }

    /// <summary>PROTOTYPE: also hash the page both ways (full and fast decode) and report both (measurement).</summary>
    public bool ProbeBothDecodes { get; init; }

    /// <summary>
    /// PROTOTYPE: also hash a re-encoded copy of each page (simulating another release of the same scan): scale factor, JPEG
    /// quality and a crop share per edge, e.g. <c>0.8:70:0.02</c>. Null = no probe.
    /// </summary>
    public string? ProbeTransform { get; init; }
}

/// <summary>Worker -> Server: the hashes of every page, in reading order.</summary>
public sealed record PageHashResult
{
    public required string JobId { get; init; }
    public required IReadOnlyList<PageHashEntry> Pages { get; init; }
    public required long ObservedLastWriteTicks { get; init; }
    public required long ObservedByteLength { get; init; }

    /// <summary>Wall time of the whole op, the worker process's CPU time during it, and its peak working set (diagnostics).</summary>
    public int ElapsedMs { get; init; }

    public int CpuMs { get; init; }
    public int ReadMs { get; init; }
    public int ExactMs { get; init; }
    public int DecodeMs { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>One page's hashes. <see cref="Error"/> set (and the hashes null) when this page could not be hashed.</summary>
public sealed record PageHashEntry
{
    public required int Ordinal { get; init; }
    public long ByteSize { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Hex SHA-256 of the entry's bytes.</summary>
    public string? Sha256 { get; init; }

    /// <summary>64-bit perceptual hash (<c>CoverHash</c>).</summary>
    public ulong? Hash { get; init; }

    /// <summary>PROTOTYPE: the other decode's hash (<see cref="PageHashRequest.ProbeBothDecodes"/>).</summary>
    public ulong? OtherDecodeHash { get; init; }

    /// <summary>PROTOTYPE: the perceptual hash of the re-encoded copy (<see cref="PageHashRequest.ProbeTransform"/>).</summary>
    public ulong? ProbeHash { get; init; }

    public string? Error { get; init; }
}

/// <summary>Worker -> Server: the archive could not be hashed (<see cref="PageHashErrors"/>).</summary>
public sealed record PageHashError
{
    public required string JobId { get; init; }
    public required string ErrorType { get; init; }
}

/// <summary>Bounds of one <c>page_hash</c> request (the worker clamps a request's own values to these).</summary>
public static class PageHashLimits
{
    public const int MaxPages = 2_000;
    public const long MaxPageBytes = 64L * 1024 * 1024;
    public const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxDimension = 30_000;
}

/// <summary>Error codes of <c>page_hash_error</c> (archive level) and of <see cref="PageHashEntry.Error"/> (page level).</summary>
public static class PageHashErrors
{
    public const string Missing = "missing";
    public const string SourceChanged = "source_changed";
    public const string Encrypted = "encrypted";
    public const string UnsupportedSolid = "unsupported_solid";
    public const string TooManyPages = "too_many_pages";
    public const string TooLarge = "too_large";
    public const string Deadline = "deadline";
    public const string ReadFailed = "read_failed";
    public const string DecodeFailed = "decode_failed";
}
