namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

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

    /// <summary>Pick order: an admin's re-run first, then new folders, bulk, retries.</summary>
    public static int Priority(int reason) => reason switch
    {
        Rerun => 0,
        NewFolder => 1,
        CarryCheck => 1,
        Bulk => 2,
        _ => 3,
    };
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

    /// <summary>At most this many query variants are sent per work.</summary>
    public const int MaxSearchesPerWork = 4;

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
    ];

    public static IReadOnlyList<string> Of(int reasons) => Of((MatchReason)reasons);

    public static IReadOnlyList<string> Of(MatchReason reasons) =>
        s_codes.Where(c => reasons.HasFlag(c.Flag)).Select(c => c.Code).ToList();
}
