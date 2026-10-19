namespace com.lifepixer.mangapixer.Server.Persistence.Entities;

// 1.45.0 new-chapter freshness (lane R, migration AddMangaDexLatestUpload). Configured in Persistence/FreshnessModel.cs. Ids, provider
// ids, times and counts only - never a title (the MangaUpdates releases feed's titles are never stored).

/// <summary>
/// MangaDex's <c>latestUploadedChapter</c> of one stored MangaDex record (a companion of a linked series, or a series linked to MangaDex
/// itself), read by the latest-upload sweep. <see cref="SeenAt"/> moves only when <see cref="ChapterId"/> changes (the export's
/// <c>companions.mangadexLatestUpload.fetchedAt</c>); <see cref="CheckedAt"/> is the last sweep that confirmed it.
/// </summary>
public sealed class MangaDexLatestUploadEntity
{
    /// <summary>The MangaDex record (<c>metadata_records.Id</c>, provider <c>mangadex</c>).</summary>
    public long RecordId { get; set; }

    /// <summary>The chapter's MangaDex id (a UUID), or null when MangaDex lists no chapter.</summary>
    public string? ChapterId { get; set; }

    /// <summary>When MangaPixer first read this <see cref="ChapterId"/>.</summary>
    public DateTimeOffset SeenAt { get; set; }

    /// <summary>The last sweep that read it (the same value or a new one).</summary>
    public DateTimeOffset CheckedAt { get; set; }

    /// <summary>
    /// 1.45.0 owner follow-up (migration <c>AddLatestUploadMissing</c>): the first sweep of the current run that MangaDex left this record
    /// out of its answer (taken down); null while it is returned. The stored <see cref="ChapterId"/> is kept meanwhile.
    /// </summary>
    public DateTimeOffset? MissingSince { get; set; }

    /// <summary>How many sweeps in a row left the record out (0 while it is returned).</summary>
    public int MissingCount { get; set; }
}

/// <summary>Per library: the latest-upload sweep that confirmed every MangaDex companion of the library (its start time).</summary>
public sealed class LibraryFreshnessEntity
{
    public long LibraryId { get; set; }

    /// <summary>The START time of the most recent sweep that completed for every MangaDex companion of the library; null = none yet.</summary>
    public DateTimeOffset? MangaDexSweepAt { get; set; }

    /// <summary>
    /// <see cref="MangaDexSweepAt"/> as it was when the library's export snapshot was last rebuilt (read before the items) - the page
    /// envelope's <c>sweeps.mangadexLatestUpload</c>, the same on every page of the snapshot.
    /// </summary>
    public DateTimeOffset? ExportMangaDexSweepAt { get; set; }
}

/// <summary>The new-chapter checks' setting and last runs (one row, <see cref="SingletonId"/>). Counts only.</summary>
public sealed class FreshnessStateEntity
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>"Check for new chapters every N hours" (1-6); null = 6.</summary>
    public int? CheckHours { get; set; }

    public DateTimeOffset? SweepStartedAt { get; set; }
    public DateTimeOffset? SweepFinishedAt { get; set; }

    /// <summary><c>ok</c>, or the code that stopped the last sweep (max 32).</summary>
    public string? SweepOutcome { get; set; }

    public int SweepRequests { get; set; }
    public int SweepRecords { get; set; }
    public int SweepChanged { get; set; }

    /// <summary>The last poll of MangaUpdates' releases feed.</summary>
    public DateTimeOffset? FeedAt { get; set; }

    /// <summary><c>ok</c>, or the code that stopped it (max 32).</summary>
    public string? FeedOutcome { get; set; }

    public int FeedRequests { get; set; }

    /// <summary>Linked MangaUpdates records the last poll made due now.</summary>
    public int FeedMatched { get; set; }

    /// <summary>The last refresh pass between the daily ones (every <c>NewChapterChecks.DuePassInterval</c>).</summary>
    public DateTimeOffset? DuePassAt { get; set; }

    /// <summary>The last day the readable backfill pulled MangaDex lists in (UTC); null = never.</summary>
    public DateTimeOffset? BackfillAt { get; set; }

    /// <summary>Set once no MangaDex list without the readable chapters is left.</summary>
    public DateTimeOffset? BackfillDoneAt { get; set; }
}

/// <summary>A linked record made due now by a new-chapter signal (MangaUpdates' releases feed); removed when the record is refreshed.</summary>
public sealed class MetadataRefreshNudgeEntity
{
    public long RecordId { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary><c>MetadataRefreshNudgeReason</c> int value.</summary>
    public int Reason { get; set; }
}
