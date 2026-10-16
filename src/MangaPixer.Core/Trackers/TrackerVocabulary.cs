namespace com.lifepixer.mangapixer.Core.Trackers;

// 1.43.0 tracker sync engine (lane E): stored states, consent streams and limits. Pure vocabulary - no I/O.

/// <summary>A user's connected account on one tracker (<c>tracker_accounts.State</c>).</summary>
public enum TrackerAccountState
{
    /// <summary>Connected; syncs while the user reads.</summary>
    Ok = 0,

    /// <summary>The tracker no longer accepts the stored sign-in (expired, revoked, refresh lapsed, or the key ring is gone).</summary>
    NeedsReconnect = 1,

    /// <summary>An admin disconnected the account: the tokens are deleted; the user may connect again.</summary>
    DisconnectedByAdmin = 2,
}

/// <summary>One series' sync state for a user on a tracker (<c>tracker_entries.State</c>).</summary>
public enum TrackerEntryState
{
    /// <summary>The tracker shows what MangaPixer has (or more of nothing); nothing to send.</summary>
    Ok = 0,

    /// <summary>The tracker is ahead of MangaPixer: nothing is sent (never lowered without the user's click).</summary>
    TrackerAhead = 1,

    /// <summary>MangaPixer is now lower than what it sent (the user marked chapters unread): nothing is sent until the user asks.</summary>
    LowerHere = 2,

    /// <summary>A jump from a folder mark-read or an import waits for the user's "Send".</summary>
    Held = 3,

    /// <summary>No tracker id is known for the series (never found by a title search).</summary>
    NoId = 4,

    /// <summary>The user turned sync off for this series.</summary>
    DontSync = 5,

    /// <summary>The last attempt failed (<c>ErrorCode</c> says why).</summary>
    Error = 6,

    /// <summary>The series is on none of the user's lists and "Add series I start" is off.</summary>
    NotOnList = 7,

    /// <summary>The user set the entry on hold, dropped, completed or rereading on the tracker: MangaPixer leaves it as it is.</summary>
    LeftAsIs = 8,

    /// <summary>The tracker asked to slow down; the write is retried shortly (or on the next read / Sync now).</summary>
    Waiting = 9,

    /// <summary>Not checked against the tracker yet.</summary>
    Pending = 10,
}

/// <summary>Where a series' tracker id came from (<c>tracker_entries.RemoteIdSource</c>).</summary>
public enum TrackerIdSource
{
    /// <summary>The linked record itself (a MangaUpdates-linked series on MangaUpdates).</summary>
    Link = 0,

    /// <summary>The series' MangaDex record names it (<c>links.mal</c>, stored as <c>CrossIdsJson["mal"]</c>).</summary>
    MangaDex = 1,

    /// <summary>An admin entered it for the series.</summary>
    Admin = 2,
}

/// <summary>
/// The two consent streams of tracker sync (1.43.0), versioned per tracker like <c>MetadataConsent</c>: the ADMIN text an admin
/// accepts to allow a tracker for the server, and the USER text each user accepts to connect their own account. A change of what
/// is sent bumps the stream; an older acceptance stops syncing until it is accepted again. The texts live in the web client.
/// </summary>
public static class TrackerConsent
{
    /// <summary>The current admin consent version of <paramref name="tracker"/> (1 for both trackers in 1.43.0).</summary>
    public static int AdminVersion(string tracker) => tracker switch
    {
        TrackerSlugs.MyAnimeList => 1,
        TrackerSlugs.MangaUpdates => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(tracker)),
    };

    /// <summary>The current user consent version of <paramref name="tracker"/> (1 for both trackers in 1.43.0).</summary>
    public static int UserVersion(string tracker) => tracker switch
    {
        TrackerSlugs.MyAnimeList => 1,
        TrackerSlugs.MangaUpdates => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(tracker)),
    };
}

/// <summary>Fixed facts and limits of the trackers MangaPixer syncs to.</summary>
public static class TrackerFacts
{
    /// <summary>
    /// Requests one user may make to one tracker per UTC day (D2 4.5), so a bulk send cannot drain a shared budget. 1.43.0 owner E5: an
    /// admin setting per tracker (Administration > Trackers), default <see cref="DefaultDailyRequestCap"/>, from
    /// <see cref="MinDailyRequestCap"/> to <see cref="MaxDailyRequestCap"/>.
    /// </summary>
    public const int DefaultDailyRequestCap = 2000;

    public const int MinDailyRequestCap = 100;

    public const int MaxDailyRequestCap = 20_000;

    /// <summary>The cap in effect for a stored setting (null or out of range = the default).</summary>
    public static int DailyRequestCapOf(int? stored) =>
        stored is { } cap && cap >= MinDailyRequestCap && cap <= MaxDailyRequestCap ? cap : DefaultDailyRequestCap;

    /// <summary>A refresh is attempted when the access token expires within this window.</summary>
    public static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(10);

    /// <summary>An OAuth sign-in must finish within this time.</summary>
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Rate-limited writes are retried at most this many times before the entry shows an error.</summary>
    public const int MaxRetryAttempts = 5;

    /// <summary>Series read from the tracker per preview call.</summary>
    public const int PreviewBatch = 50;

    /// <summary>The display name of a tracker.</summary>
    public static string NameOf(string tracker) => tracker switch
    {
        TrackerSlugs.MyAnimeList => "MyAnimeList",
        TrackerSlugs.MangaUpdates => "MangaUpdates",
        _ => tracker,
    };

    public static bool IsKnown(string? tracker) => tracker is not null && TrackerSlugs.All.Contains(tracker, StringComparer.Ordinal);

    /// <summary>
    /// Whether the tracker can keep an entry private. MyAnimeList's API v2 documents no private flag for a list entry (lane A
    /// confirms); MangaUpdates lists have none. The "Private entries" setting is stored for both and sent only where this is true.
    /// </summary>
    public static bool SupportsPrivateEntries(string tracker) => false;

    /// <summary>
    /// Whether the tracker keeps a start / finish date on a list entry that MangaPixer may send (1.43.0 owner A1: MyAnimeList, whose
    /// list update takes <c>start_date</c> / <c>finish_date</c>; MangaUpdates lists keep no dates).
    /// </summary>
    public static bool SupportsDates(string tracker) => tracker == TrackerSlugs.MyAnimeList;

    /// <summary>Series per page of the Tracking list (owner redesign, 2026-10-15).</summary>
    public const int ListPageSize = 50;

    /// <summary>The longest search text the Tracking list accepts.</summary>
    public const int MaxSearchLength = 200;

    /// <summary>1.43.2: series per batch request of the Tracking list's Select mode (the page sends larger selections in chunks).</summary>
    public const int MaxBatchSeries = 100;

    /// <summary>1.43.2: the most series ids "Select all in this chip" returns (the answer says when it stopped there).</summary>
    public const int MaxSelectableSeries = 2000;

    /// <summary>Whether the instance must register an app with the tracker (MyAnimeList: Client ID + redirect URL).</summary>
    public static bool NeedsAppRegistration(string tracker) => tracker == TrackerSlugs.MyAnimeList;

    /// <summary>A tracker series id as MangaPixer accepts it (from a record, MangaDex or an admin): a positive decimal integer.</summary>
    public static bool IsValidRemoteId(string? id) =>
        id is { Length: >= 1 and <= 20 } && id.All(char.IsAsciiDigit) && id.Any(c => c != '0');
}
