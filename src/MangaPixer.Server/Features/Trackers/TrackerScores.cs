namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Core.Trackers;

/// <summary>One series' score in the user's own list: the tracker's id of the series and the score (1-10, never a title).</summary>
public sealed record TrackerScoreItem(string MediaId, int Score);

/// <summary>One page of the user's scores and the opaque cursor of the next page (null = the last one). Each page is ONE request.</summary>
public sealed record TrackerScorePage(IReadOnlyList<TrackerScoreItem> Items, string? NextCursor);

/// <summary>
/// 1.53.0 (lane T, vault note "MangaPixer - Tracker Ratings"): a tracker client that can read and write the user's own score of a series.
/// Optional, so <see cref="ITrackerClient"/> stays as it is. Every call is ONE request (or none - <see cref="ScoreInEntry"/>), so the
/// engine counts each against the user's daily cap before it makes it. Only the tracker's series id and the score are ever sent.
/// </summary>
public interface ITrackerScores
{
    /// <summary>
    /// True when the user's score arrives with the entry read (<see cref="TrackerEntry.Score"/>, MyAnimeList) - then
    /// <see cref="ReadScoreAsync"/> makes no request; false when it is a request of its own (MangaUpdates).
    /// </summary>
    bool ScoreInEntry { get; }

    /// <summary>The user's score (1-10) of the series, or null when they gave it none. <paramref name="entry"/> is the entry just read.</summary>
    Task<int?> ReadScoreAsync(TrackerTokens tokens, string mediaId, TrackerEntry entry, CancellationToken ct);

    /// <summary>Sets the user's score of the series to <paramref name="score"/> (1-10), or removes it (null). The entry must exist.</summary>
    Task WriteScoreAsync(TrackerTokens tokens, string mediaId, int? score, CancellationToken ct);

    /// <summary>
    /// One page of the user's scored list entries for "Take my scores from &lt;tracker&gt;" (<paramref name="cursor"/> null = the first
    /// request). Entries without a score are left out.
    /// </summary>
    Task<TrackerScorePage> ReadScorePageAsync(TrackerTokens tokens, string? cursor, CancellationToken ct);
}
