namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// Per-library jump index for multilingual collation-aware jump navigation.
/// A coarse A–Z/script rail computed server-side from the existing persisted
/// <c>SortKey</c>s. Each bucket carries a <c>FirstCursor</c> that is a valid
/// keyset cursor for the name sort of the browse endpoint
/// (<c>GET /api/v1/libraries/{id}/browse</c> with <c>sort=name</c>).
///
/// The cursor is the raw <c>SortKey</c> of the catalog node immediately
/// preceding the bucket's first node in <c>SortKey</c> (ordinal) order, so the
/// browse endpoint's exclusive <c>SortKey > cursor</c> filter lands on the
/// bucket's first node. The first bucket (whose first node is the global first
/// node) carries a <c>null</c> cursor, matching "first page".
///
/// Bucketing is by the first collation element of the display name using
/// Unicode/ICU-aware grapheme and script detection (.NET on Linux uses ICU by
/// default). Bucket labels are stable strings; the rail order is a fixed,
/// UI-sensible order (Latin A–Z, digits "#", then script groups) independent of
/// the cursor semantics. Folders and archives that share a first letter are
/// merged into one bucket; because folders sort before archives in
/// <c>SortKey</c> order, the cursor lands on the first folder of the letter and
/// the count covers both kinds.
/// </summary>
public sealed record JumpIndexDto
{
    /// <summary>
    /// Opaque public id of the library this index was computed for.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    /// Buckets in rail order, each with a non-zero count. Empty buckets are
    /// omitted so the rail only shows letters/scripts that actually occur.
    /// </summary>
    public required IReadOnlyList<JumpIndexBucketDto> Buckets { get; init; }
}

/// <summary>
/// One bucket of the per-library jump index.
/// </summary>
public sealed record JumpIndexBucketDto
{
    /// <summary>
    /// Stable bucket label: a single Latin letter "A"–"Z", "#" for digits, or a
    /// script-group name ("Kana", "Hangul", "CJK", "Cyrillic", "Other").
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Number of catalog nodes (folders + archives) in this library level whose
    /// display name falls into this bucket.
    /// </summary>
    public required int Count { get; init; }

    /// <summary>
    /// Keyset cursor to pass to the browse endpoint (<c>sort=name</c>) to land on
    /// the first node of this bucket. <c>null</c> for the first bucket (start of
    /// the listing). This is a raw <c>SortKey</c>, so it is only honoured by the
    /// name sort; other sorts ignore it (the rail is a name-sort navigation aid).
    /// </summary>
    public string? FirstCursor { get; init; }
}
