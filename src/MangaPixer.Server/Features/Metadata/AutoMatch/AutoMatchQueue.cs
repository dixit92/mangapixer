namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using System.Linq.Expressions;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;

/// <summary>Stored values of <c>metadata_match_queue.State</c>.</summary>
public static class QueueState
{
    public const int Pending = 0;
    public const int Leased = 1;
    public const int Done = 2;
    public const int Failed = 3;
    public const int Skipped = 4;
    public const int Cancelled = 5;
}

/// <summary>Stored values of <c>metadata_match_queue.Reason</c> (lower = picked first after reruns).</summary>
public static class QueueReason
{
    public const int NewFolder = 0;
    public const int Bulk = 1;
    public const int Retry = 2;
    public const int Rerun = 3;
    public const int CarryCheck = 4;

    /// <summary>
    /// Pick order: an admin's re-run first, then new folders, bulk, retries. An expression so the
    /// lease orders in SQL (a row limit without ORDER BY returns an arbitrary set of rows).
    /// </summary>
    public static readonly Expression<Func<MetadataMatchQueueEntity, int>> Priority = q =>
        q.Reason == Rerun ? 0
        : q.Reason == NewFolder || q.Reason == CarryCheck ? 1
        : q.Reason == Bulk ? 2
        : 3;
}

/// <summary>Re-match timing and limits (stage 2, section 3).</summary>
public static class AutoMatchPolicy
{
    /// <summary>A leased row not finished within this is picked up again.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>Unmatched works are retried after 30, then 90, then 180 days, then never.</summary>
    public static readonly TimeSpan[] UnmatchedRetry = [TimeSpan.FromDays(30), TimeSpan.FromDays(90), TimeSpan.FromDays(180)];

    /// <summary>A provider failure (not a refusal) retries after this; the third failure gives up.</summary>
    public static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(30);
    public const int MaxAttempts = 3;

    /// <summary>
    /// At most this many searches are sent per work - query variants and page-2 reads together (1.27.0: a
    /// page-2 read spends one of them; nothing else about a work's request bound changed).
    /// </summary>
    public const int MaxSearchesPerWork = 4;

    /// <summary>
    /// Page 2 of the same search text is read (1.27.0) when page 1 left the top two tied - raw title scores
    /// within this of each other and the adjusted lead below the margin - or nothing at the review floor, and
    /// the provider reports more hits than page 1 held.
    /// </summary>
    public const double PageTwoTieWithin = 0.02;

    /// <summary>A variant whose best raw title score reaches this stops the variant loop.</summary>
    public const double ConfidentTitle = 0.85;

    /// <summary>The runner-up is also fetched when it is within this of the best hit.</summary>
    public const double SecondFetchWithin = 0.10;

    /// <summary>Estimated provider requests per work (1 search + 1-2 GETs, sometimes a second search).</summary>
    public const double EstimatedRequestsPerWork = 2.5;

    /// <summary>Archives inspected per work for ComicInfo and tall-strip signals.</summary>
    public const int MaxLocalArchives = 500;
}

/// <summary>Reason chip codes of <see cref="MatchReason"/> (the review DTOs' vocabulary).</summary>
public static class MatchReasonCodes
{
    private static readonly (MatchReason Flag, string Code)[] s_codes =
    [
        (MatchReason.CloseSecond, "close_second"),
        (MatchReason.CountConflict, "count"),
        (MatchReason.YearConflict, "year"),
        (MatchReason.TypeConflict, "type"),
        (MatchReason.RelatedPair, "related_pair"),
        (MatchReason.OneShotMismatch, "one_shot"),
        (MatchReason.AuthorConflict, "author"),
        (MatchReason.NumberMismatch, "number"),
        (MatchReason.ReviewOnlyClass, "review_only"),
        (MatchReason.DeclaredTypeAgree, "declared_type"),
        (MatchReason.DeclaredTypeMismatch, "not_declared_type"),
        (MatchReason.ReachConflict, "reach"),
        (MatchReason.SubtitleFamily, "subtitle_family"),
        (MatchReason.SeriesFamily, "series_family"),
        (MatchReason.CoverDiffers, "cover_differs"),
    ];

    public static IReadOnlyList<string> Of(int reasons) => Of((MatchReason)reasons);

    public static IReadOnlyList<string> Of(MatchReason reasons) =>
        s_codes.Where(c => reasons.HasFlag(c.Flag)).Select(c => c.Code).ToList();
}
