namespace com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// What each <see cref="SeriesLinkState"/> means to the readers of <c>node_series_links</c> (1.34.0). Every predicate is an exhaustive
/// switch that throws on a value it does not know, so a new state cannot be added without deciding what it means here (the sweep tests
/// enumerate every value through every predicate).
/// </summary>
public static class SeriesLinkStates
{
    /// <summary>The node IS the linked series: its numbers, companions, volume lists, refresh and Completion apply (Confirmed / Auto).</summary>
    public static bool IsSeries(SeriesLinkState state) => state switch
    {
        SeriesLinkState.Confirmed or SeriesLinkState.Auto => true,
        SeriesLinkState.NeedsReview or SeriesLinkState.DontMatch or SeriesLinkState.CollectionAbout => false,
        _ => throw Unknown(state),
    };

    /// <summary>The nearest-row walk (self, then ancestors) stops at this row; Needs review is skipped (not a decision yet).</summary>
    public static bool StopsInheritance(SeriesLinkState state) => state switch
    {
        SeriesLinkState.Confirmed or SeriesLinkState.Auto or SeriesLinkState.DontMatch or SeriesLinkState.CollectionAbout => true,
        SeriesLinkState.NeedsReview => false,
        _ => throw Unknown(state),
    };

    /// <summary>The node's OWN series information shows the linked record (a collection: as "Collection about", without numbers).</summary>
    public static bool ShowsRecordOnSelf(SeriesLinkState state) => state switch
    {
        SeriesLinkState.Confirmed or SeriesLinkState.Auto or SeriesLinkState.CollectionAbout => true,
        SeriesLinkState.NeedsReview or SeriesLinkState.DontMatch => false,
        _ => throw Unknown(state),
    };

    /// <summary>An admin decision: on a move it wins over an automatic suggestion, a different one is a conflict.</summary>
    public static bool IsAdminDecision(SeriesLinkState state) => state switch
    {
        SeriesLinkState.Confirmed or SeriesLinkState.DontMatch or SeriesLinkState.CollectionAbout => true,
        SeriesLinkState.Auto or SeriesLinkState.NeedsReview => false,
        _ => throw Unknown(state),
    };

    /// <summary>How the row covers automatic matching below its node (see <see cref="MatchingCover"/>).</summary>
    public static MatchingCoverKind CoverBelow(SeriesLinkState state) => state switch
    {
        SeriesLinkState.Confirmed or SeriesLinkState.Auto => MatchingCoverKind.Covers,
        SeriesLinkState.DontMatch or SeriesLinkState.NeedsReview => MatchingCoverKind.Blocks,
        SeriesLinkState.CollectionAbout => MatchingCoverKind.Opens,
        _ => throw Unknown(state),
    };

    private static ArgumentOutOfRangeException Unknown(SeriesLinkState state) =>
        new(nameof(state), state, "A series link state without a decision in SeriesLinkStates.");
}

/// <summary>How a link row affects automatic matching of the works below its node.</summary>
public enum MatchingCoverKind
{
    /// <summary>The node speaks for its subtree (a linked series) - unless a nearer collection re-opens it.</summary>
    Covers = 0,

    /// <summary>Nothing below is matched, whatever is nearer (Don't match: privacy; Needs review: undecided).</summary>
    Blocks = 1,

    /// <summary>A collection: its items are works of their own, matched automatically.</summary>
    Opens = 2,
}

/// <summary>
/// Whether automatic matching may look up a work below a chain of link rows (1.34.0, owner: "nearest wins" - the archives in a collection
/// folder inherit from themselves, then their direct parent; a collection disregards the series above it). Pure.
/// </summary>
public static class MatchingCover
{
    /// <summary>
    /// <paramref name="ancestorStates"/>: the link states of the work's ANCESTORS that have a row, nearest first (the work's own row is the
    /// caller's business). A Don't match or Needs-review row anywhere above blocks; otherwise the nearest row decides - a linked series
    /// covers, a collection re-opens. No row above: open.
    /// </summary>
    public static bool IsCovered(IEnumerable<SeriesLinkState> ancestorStates)
    {
        ArgumentNullException.ThrowIfNull(ancestorStates);
        var states = ancestorStates.ToList();
        if (states.Any(s => SeriesLinkStates.CoverBelow(s) == MatchingCoverKind.Blocks))
            return true;
        return states.Count > 0 && SeriesLinkStates.CoverBelow(states[0]) == MatchingCoverKind.Covers;
    }
}
