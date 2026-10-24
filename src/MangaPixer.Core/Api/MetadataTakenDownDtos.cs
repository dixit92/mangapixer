namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// 1.47.0 (owner decision 10a): MangaDex records the latest-upload sweep found missing from MangaDex's answer (taken down), read from
/// what the sweep already stored - no request, no fallback lookup. Admin page only (Metadata Manager, Runs tab).
/// </summary>
public sealed record TakenDownRecordsDto
{
    /// <summary>How many records are missing now (the list below holds at most <see cref="Limit"/> of them, oldest first).</summary>
    public required int Total { get; init; }

    /// <summary>The most records listed.</summary>
    public required int Limit { get; init; }

    public required IReadOnlyList<TakenDownRecordDto> Items { get; init; }
}

/// <summary>One MangaDex record the sweep stopped finding.</summary>
public sealed record TakenDownRecordDto
{
    /// <summary>The stored MangaDex record's public id.</summary>
    public required string RecordId { get; init; }

    /// <summary>The record's title as MangaPixer stored it.</summary>
    public required string Title { get; init; }

    /// <summary>The first sweep of the current run that left the record out of MangaDex's answer.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>The last sweep that still found it, or null when it never did.</summary>
    public DateTimeOffset? LastSeen { get; init; }

    /// <summary>How many sweeps in a row left it out.</summary>
    public required int Sweeps { get; init; }

    /// <summary>The series here that use the record (at most <see cref="TakenDownSeriesDto"/> listed; see <see cref="MoreSeries"/>).</summary>
    public required IReadOnlyList<TakenDownSeriesDto> Series { get; init; }

    /// <summary>Series that use the record beyond the ones listed.</summary>
    public int MoreSeries { get; init; }
}

/// <summary>A linked series folder whose MangaDex record is missing.</summary>
public sealed record TakenDownSeriesDto
{
    public required string NodeId { get; init; }
    public required string LibraryId { get; init; }
    public required string DisplayName { get; init; }
}
