namespace com.lifepixer.mangapixer.Core.WorkerProtocol;

/// <summary>
/// Outcome codes of a ComicInfo.xml read (protocol v3). Persisted as the
/// <c>embedded_metadata.State</c> integer via <see cref="ToState"/>, so every
/// archive is read once per content version, including "absent".
/// </summary>
public static class ComicInfoStatus
{
    public const string Absent = "absent";
    public const string Parsed = "parsed";
    public const string Malformed = "malformed";
    public const string TooLarge = "too_large";
    public const string SkippedSolid = "skipped_solid";
    public const string ReadError = "read_error";

    /// <summary>Maps a status code to the stored integer (0 absent ... 5 read_error).</summary>
    public static int ToState(string? status) => status switch
    {
        Absent => 0,
        Parsed => 1,
        Malformed => 2,
        TooLarge => 3,
        SkippedSolid => 4,
        _ => 5,
    };
}

/// <summary>
/// Result of reading one archive's ComicInfo.xml inside the worker (the
/// untrusted-input boundary). <see cref="Payload"/> is set only for
/// <see cref="ComicInfoStatus.Parsed"/>.
/// </summary>
public sealed record ComicInfoOutcome
{
    public required string Status { get; init; }
    public ComicInfoPayload? Payload { get; init; }
}

/// <summary>
/// Bounded, sanitized ComicInfo fields. Every string is trimmed, stripped of
/// control characters and length-capped by the worker; it is data, never markup.
/// Not stored: ScanInformation, Pages, Characters/Teams/Locations (no consumer).
/// </summary>
public sealed record ComicInfoPayload
{
    public const int MaxShortText = 512;
    public const int MaxCodeText = 32;
    public const int MaxPublisherText = 256;
    public const int MaxSummaryText = 16_384;
    public const int MaxNotesText = 2_048;
    public const int MaxListItems = 100;
    public const int MaxListItemText = 256;
    public const int MaxWebUrls = 20;

    public string? Series { get; init; }
    public string? Title { get; init; }
    public string? AlternateSeries { get; init; }
    public string? SeriesGroup { get; init; }
    public string? StoryArc { get; init; }

    /// <summary>Kept as text ("12.5", "Extra").</summary>
    public string? Number { get; init; }
    public string? Format { get; init; }
    public string? AgeRating { get; init; }
    public string? LanguageIso { get; init; }

    /// <summary>
    /// Raw ComicInfo Volume. For western comics it is often the run's start year;
    /// it is kept raw and interpreted at read time.
    /// </summary>
    public int? Volume { get; init; }
    public int? Count { get; init; }
    public int? Year { get; init; }
    public int? Month { get; init; }

    public string? Summary { get; init; }
    public IReadOnlyList<ComicInfoCreator> Creators { get; init; } = [];
    public string? Publisher { get; init; }
    public string? Imprint { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>ComicInfo <c>Web</c>, split on whitespace; only http(s) URLs kept.</summary>
    public IReadOnlyList<string> WebUrls { get; init; } = [];

    /// <summary>ComicInfo <c>Manga</c>: 0 No, 1 Yes, 2 YesAndRightToLeft; null = Unknown/absent.</summary>
    public int? MangaDirection { get; init; }
    public string? Gtin { get; init; }
    public string? Notes { get; init; }
}

/// <summary>One creator credit: a name and its ComicInfo role (Writer, Penciller, ...).</summary>
public sealed record ComicInfoCreator
{
    public required string Name { get; init; }
    public required string Role { get; init; }
}

/// <summary>
/// Server -> Worker (v3): read only an archive's ComicInfo.xml (the backfill for
/// archives analysed before 1.24.0). The worker opens the archive read-only,
/// reads the directory and inflates at most one entry of <see cref="MaxXmlBytes"/>.
/// Solid archives are answered <see cref="ComicInfoStatus.SkippedSolid"/> without
/// decompressing anything.
/// </summary>
public sealed record ComicInfoRequest
{
    public required string JobId { get; init; }

    /// <summary>Absolute path to the source archive (private locator, worker-side only).</summary>
    public required string ArchivePath { get; init; }

    public required long ExpectedLastWriteTicks { get; init; }
    public required long ExpectedByteLength { get; init; }
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>Maximum uncompressed size of the ComicInfo.xml entry (default 1 MiB).</summary>
    public long MaxXmlBytes { get; init; } = ComicInfoLimits.MaxXmlBytes;
}

/// <summary>Worker -> Server (v3): the ComicInfo read finished (any status, including absent).</summary>
public sealed record ComicInfoResult
{
    public required string JobId { get; init; }
    public required ComicInfoOutcome Outcome { get; init; }
    public required long ObservedLastWriteTicks { get; init; }
    public required long ObservedByteLength { get; init; }
}

/// <summary>
/// Worker -> Server (v3): the ComicInfo read could not run at all (source changed
/// or missing, archive unreadable). Nothing is stored for a source change, so the
/// next pass retries.
/// </summary>
public sealed record ComicInfoError
{
    public required string JobId { get; init; }
    public required string ErrorType { get; init; }
    public required string ErrorMessage { get; init; }
}

/// <summary>Shared ComicInfo limits (worker and server agree on them).</summary>
public static class ComicInfoLimits
{
    /// <summary>1 MiB cap on the uncompressed ComicInfo.xml entry.</summary>
    public const long MaxXmlBytes = 1024 * 1024;

    /// <summary>Cap on characters in the parsed XML document (defence against expansion).</summary>
    public const long MaxCharactersInDocument = 2_000_000;
}
