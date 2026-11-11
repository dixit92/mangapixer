namespace com.lifepixer.mangapixer.Core.Trackers;

/// <summary>What "Take my scores from &lt;tracker&gt;" does with one series (1.53.0, lane T).</summary>
public enum TrackerScoreImportOutcome
{
    /// <summary>No MangaPixer rating yet: the tracker's score is set here.</summary>
    Fill = 0,

    /// <summary>The MangaPixer rating is already the tracker's score: nothing to do.</summary>
    Same = 1,

    /// <summary>The MangaPixer rating differs: it is kept, and the series is listed for the user to choose ("Use &lt;tracker&gt;'s").</summary>
    Differ = 2,
}

/// <summary>
/// The user's own tracker score next to their MangaPixer rating (1.53.0, lane T; vault note "MangaPixer - Tracker Ratings"). Both scales
/// are 1-10: MangaPixer's half-star steps, MyAnimeList's <c>score</c> (an integer, 0 = none) and MangaUpdates' series rating (a number
/// that may carry decimals). Nothing here makes a request.
/// </summary>
public static class TrackerScoreRules
{
    public const int MinScore = 1;
    public const int MaxScore = 10;

    /// <summary>
    /// A score as MangaPixer stores it, from a tracker's number: rounded to the nearest whole step (halves away from zero, so 7.5 is 8),
    /// held to 1-10; null for none (MyAnimeList's 0, anything below 0.5, not a number).
    /// </summary>
    public static int? FromTracker(double? value)
    {
        if (value is not { } v || double.IsNaN(v) || double.IsInfinity(v) || v < 0.5)
            return null;
        return (int)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), MinScore, MaxScore);
    }

    /// <summary>Whether <paramref name="score"/> can be sent (1-10); null means "no score" (a clear).</summary>
    public static bool IsSendable(int? score) => score is null or (>= MinScore and <= MaxScore);

    /// <summary>
    /// Whether a send is needed: the tracker's current score differs from the user's MangaPixer rating (null = none on either side). An equal
    /// score sends nothing (the write is idempotent by reading first).
    /// </summary>
    public static bool NeedsWrite(int? tracker, int? mine) => tracker != mine;

    /// <summary>
    /// The import's decision for one series (never overwrites silently): no rating here -> fill it; the same -> nothing; another -> keep
    /// MangaPixer's and list it.
    /// </summary>
    public static TrackerScoreImportOutcome Decide(int? mine, int theirs) =>
        mine is null ? TrackerScoreImportOutcome.Fill
        : mine == theirs ? TrackerScoreImportOutcome.Same
        : TrackerScoreImportOutcome.Differ;
}
