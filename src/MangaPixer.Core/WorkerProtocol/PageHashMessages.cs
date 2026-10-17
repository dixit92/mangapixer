namespace com.lifepixer.mangapixer.Core.WorkerProtocol;

// Worker protocol v6 (1.44.0): the volume contents scan. The server asks the worker for the 64-bit perceptual hash of the pages of
// ONE archive - a chunk of them for a random-access archive, the rest of the archive in one pass for a solid one - to prove which
// chapter files a volume file holds (VolumeContentMatcher). Read-only: the worker writes nothing.

/// <summary>
/// Server -> Worker: hash the image pages of one archive, in reading order (the same entries and order as <c>analyze</c>, so
/// <see cref="PageHashEntry.Ordinal"/> is the stored page ordinal), from <see cref="FromOrdinal"/> for <see cref="Count"/> pages. The
/// worker opens the archive READ-ONLY by the same private validated source locator <c>analyze</c> / <c>extract</c> take, checks the
/// source stamp before and after, writes nothing, and answers a <c>page_hash_result</c> or a <c>page_hash_error</c>
/// (<see cref="PageHashErrors"/>). Every limit is clamped by the worker to <see cref="PageHashLimits"/>.
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

    /// <summary>The first page ordinal to hash (0 = the first page).</summary>
    public int FromOrdinal { get; init; }

    /// <summary>
    /// How many pages to hash (0 = to the end). A SOLID archive can only be read front to back: the worker then hashes every page from
    /// <see cref="FromOrdinal"/> to the end in one pass (<see cref="PageHashResult.Solid"/>), and only when the archive is at most
    /// <see cref="PageHashLimits.MaxSolidBytes"/>.
    /// </summary>
    public int Count { get; init; }

    /// <summary>An archive with more image pages is refused (<c>too_many_pages</c>) before any page is read.</summary>
    public int MaxPages { get; init; } = PageHashLimits.MaxPages;

    /// <summary>A single page larger than this (declared or read) is not decoded; its entry carries <c>too_large</c>.</summary>
    public long MaxPageBytes { get; init; } = PageHashLimits.MaxPageBytes;

    /// <summary>The archive's image pages together (declared sizes) above this are refused (<c>too_large</c>).</summary>
    public long MaxTotalBytes { get; init; } = PageHashLimits.MaxTotalBytes;

    /// <summary>Pages wider or taller than this are not decoded (<c>too_large</c> on the entry).</summary>
    public int MaxDimension { get; init; } = PageHashLimits.MaxDimension;
}

/// <summary>Worker -> Server: the perceptual hashes of the requested pages, in reading order.</summary>
public sealed record PageHashResult
{
    public required string JobId { get; init; }

    /// <summary>The archive's image page count (the server discards a result whose count differs from the stored one).</summary>
    public required int PageCount { get; init; }

    /// <summary>The hashed pages, ascending by ordinal (a chunk; for a solid archive every page from the requested ordinal).</summary>
    public required IReadOnlyList<PageHashEntry> Pages { get; init; }

    /// <summary>True when the archive is solid and was read front to back in one pass.</summary>
    public bool Solid { get; init; }

    public required long ObservedLastWriteTicks { get; init; }
    public required long ObservedByteLength { get; init; }

    /// <summary>Wall time of the op (diagnostics: ids, counts and timings only).</summary>
    public int ElapsedMs { get; init; }
}

/// <summary>One page: its 64-bit perceptual hash (<c>CoverHash</c>), or <see cref="Error"/> (<c>too_large</c> / <c>decode_failed</c> /
/// <c>read_failed</c>) and no hash.</summary>
public sealed record PageHashEntry
{
    public required int Ordinal { get; init; }
    public ulong? Hash { get; init; }
    public string? Error { get; init; }
}

/// <summary>Worker -> Server: the archive could not be hashed (<see cref="PageHashErrors"/>).</summary>
public sealed record PageHashError
{
    public required string JobId { get; init; }
    public required string ErrorType { get; init; }
}

/// <summary>Hard bounds of one <c>page_hash</c> request (the worker clamps a request's own values to these).</summary>
public static class PageHashLimits
{
    public const int MaxPages = 2_000;
    public const long MaxPageBytes = 64L * 1024 * 1024;
    public const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxDimension = 30_000;

    /// <summary>A solid archive is read front to back in ONE request - only up to this size (larger: <c>too_large</c>).</summary>
    public const long MaxSolidBytes = 600L * 1024 * 1024;

    /// <summary>The pages one request of a random-access archive hashes (the server's chunk: a Background slot is held for seconds).</summary>
    public const int ChunkPages = 48;
}

/// <summary>Error codes of <c>page_hash_error</c> (archive level) and of <see cref="PageHashEntry.Error"/> (page level).</summary>
public static class PageHashErrors
{
    public const string Missing = "missing";
    public const string SourceChanged = "source_changed";
    public const string Encrypted = "encrypted";
    public const string TooManyPages = "too_many_pages";
    public const string TooLarge = "too_large";
    public const string Deadline = "deadline";
    public const string ReadFailed = "read_failed";
    public const string InvalidRequest = "invalid_request";
    public const string DecodeFailed = "decode_failed";
}
