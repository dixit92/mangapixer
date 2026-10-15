namespace com.lifepixer.mangapixer.Core.Trackers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

// 1.43.0 tracking redesign (lane G, owner 2026-10-15): the Tracking page's filter chips, the two "Set progress" actions' availability,
// and "read on <tracker>" for a file. PURE - stored numbers in, answers out; no database, no clock, no request.

/// <summary>
/// The filter chip a series of the Tracking list falls in (one per row; the first rule that matches wins, in this order):
/// <see cref="NotSynced"/>, <see cref="NoId"/>, <see cref="LeftAsIs"/>, <see cref="Held"/>, <see cref="TrackerAhead"/>,
/// <see cref="Problem"/>, <see cref="MangaPixerAhead"/>, <see cref="Unchecked"/>, <see cref="InSync"/> (1.43.0 owner answer 4: a failed
/// sync is a Problem even when MangaPixer is ahead - it counts in the badge). Values are append-only (they
/// travel over the API as names).
/// </summary>
public enum TrackerListBucket
{
    /// <summary>Both sides agree (or MangaPixer has nothing to send).</summary>
    InSync = 0,

    /// <summary>MangaPixer is ahead and it was not sent yet (a rate-limited write waiting for its retry, the daily cap, or not checked since).</summary>
    MangaPixerAhead = 1,

    /// <summary>The tracker is ahead (or MangaPixer is now lower than what it sent): nothing is lowered without the user's click.</summary>
    TrackerAhead = 2,

    /// <summary>On hold, dropped, completed or rereading on the tracker: MangaPixer leaves it as it is.</summary>
    LeftAsIs = 3,

    /// <summary>A jump from a folder mark-read or an import waits for the user ("Set progress from MangaPixer to ...").</summary>
    Held = 4,

    /// <summary>No tracker id is known for the series (never found by a title search).</summary>
    NoId = 5,

    /// <summary>The user's "Don't sync", or the series is on none of the user's lists and "Add series I start" is off.</summary>
    NotSynced = 6,

    /// <summary>
    /// The last attempt failed (<c>ErrorCode</c> says why: <c>needs_reconnect</c>, <c>rejected</c>, <c>unavailable</c>, <c>rate_limited</c> after
    /// the last retry, ...), whether or not MangaPixer is ahead.
    /// </summary>
    Problem = 7,

    /// <summary>Never checked against the tracker.</summary>
    Unchecked = 8,
}

/// <summary>Why one of the two "Set progress" actions of a series cannot move anything right now (<see cref="None"/> = it can).</summary>
public enum TrackerActionBlock
{
    None = 0,

    /// <summary>The tracker was never read for this series (use Sync now first).</summary>
    NotChecked = 1,

    /// <summary>The side the action copies FROM is not ahead of the other.</summary>
    NotAhead = 2,

    /// <summary>No tracker id is known for the series.</summary>
    NoId = 3,

    /// <summary>The user turned sync off for this series on this tracker.</summary>
    DontSync = 4,

    /// <summary>On hold, dropped, completed or rereading on the tracker: never changed by MangaPixer.</summary>
    LeftAsIs = 5,

    /// <summary>The series is on none of the user's lists on the tracker (and, to the tracker, "Add series I start" is off).</summary>
    NotOnList = 6,
}

/// <summary>What the list rules read of one stored entry.</summary>
/// <param name="Automatic">MangaPixer's numbers from marks that sync on their own.</param>
/// <param name="All">MangaPixer's numbers with every mark (folder mark-reads and imports included).</param>
/// <param name="Remote">The tracker's numbers when last read (or as last written).</param>
public sealed record TrackerRowFacts(
    TrackerEntryState State,
    bool DontSync,
    bool HasRemoteId,
    bool RemoteSeen,
    bool RemoteMissing,
    TrackerEntryStatus? RemoteStatus,
    TrackerNumbers Automatic,
    TrackerNumbers All,
    TrackerNumbers Held,
    TrackerNumbers Remote,
    bool AddStartedSeries);

public static class TrackerListRules
{
    /// <summary>
    /// The chips that need the user (the account menu's badge, the list's Attention filter, <c>Counts.Attention</c>): a held jump, a tracker
    /// ahead of MangaPixer, and - 1.43.0 owner answer 4 - a problem (the last sync of the series failed).
    /// </summary>
    public static bool NeedsAttention(TrackerListBucket bucket) =>
        bucket is TrackerListBucket.Held or TrackerListBucket.TrackerAhead or TrackerListBucket.Problem;

    private static bool IsLeftAsIsStatus(TrackerEntryStatus? status) =>
        status is TrackerEntryStatus.OnHold or TrackerEntryStatus.Dropped or TrackerEntryStatus.Completed or TrackerEntryStatus.Rereading;

    public static TrackerListBucket BucketOf(TrackerRowFacts row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.DontSync || row.State is TrackerEntryState.DontSync or TrackerEntryState.NotOnList)
            return TrackerListBucket.NotSynced;
        if (!row.HasRemoteId || row.State == TrackerEntryState.NoId)
            return TrackerListBucket.NoId;
        if (row.State == TrackerEntryState.LeftAsIs || (row.RemoteSeen && !row.RemoteMissing && IsLeftAsIsStatus(row.RemoteStatus)))
            return TrackerListBucket.LeftAsIs;
        if (row.State == TrackerEntryState.Held || !row.Held.IsEmpty)
            return TrackerListBucket.Held;
        if (row.State is TrackerEntryState.TrackerAhead or TrackerEntryState.LowerHere)
            return TrackerListBucket.TrackerAhead;
        if (row.State == TrackerEntryState.Error)
            return TrackerListBucket.Problem;
        if (row.RemoteSeen && row.Automatic.IsAheadOf(row.Remote))
            return TrackerListBucket.MangaPixerAhead;
        if (!row.RemoteSeen || row.State == TrackerEntryState.Pending)
            return TrackerListBucket.Unchecked;
        return TrackerListBucket.InSync;
    }

    /// <summary>
    /// "Set progress from &lt;tracker&gt; to MangaPixer": the tracker's numbers were read, the series is on the user's list there, and the
    /// tracker is ahead of everything MangaPixer has marked. Whether a file is there to mark is known only when it runs (it answers counts).
    /// </summary>
    public static TrackerActionBlock FromTrackerBlock(TrackerRowFacts row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.HasRemoteId || row.State == TrackerEntryState.NoId)
            return TrackerActionBlock.NoId;
        if (!row.RemoteSeen)
            return TrackerActionBlock.NotChecked;
        if (row.RemoteMissing)
            return TrackerActionBlock.NotOnList;
        return row.Remote.IsAheadOf(row.All) ? TrackerActionBlock.None : TrackerActionBlock.NotAhead;
    }

    /// <summary>
    /// "Set progress from MangaPixer to &lt;tracker&gt;" (the "Send" of 1.43.0: every mark counts, a held jump too; never lowers): a known id,
    /// sync not turned off, an entry the user did not put on hold / drop / complete / reread, and MangaPixer ahead of the tracker.
    /// </summary>
    public static TrackerActionBlock ToTrackerBlock(TrackerRowFacts row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.DontSync || row.State == TrackerEntryState.DontSync)
            return TrackerActionBlock.DontSync;
        if (!row.HasRemoteId || row.State == TrackerEntryState.NoId)
            return TrackerActionBlock.NoId;
        if (row.State == TrackerEntryState.LeftAsIs || (row.RemoteSeen && !row.RemoteMissing && IsLeftAsIsStatus(row.RemoteStatus)))
            return TrackerActionBlock.LeftAsIs;
        if (row.RemoteSeen && row.RemoteMissing && !row.AddStartedSeries)
            return TrackerActionBlock.NotOnList;
        if (row.All.IsEmpty)
            return TrackerActionBlock.NotAhead;
        // Never read: Sync now / Send reads the tracker first and writes only when MangaPixer is ahead.
        if (!row.RemoteSeen)
            return TrackerActionBlock.None;
        return row.All.IsAheadOf(row.Remote) ? TrackerActionBlock.None : TrackerActionBlock.NotAhead;
    }
}

/// <summary>
/// Whether a file (or a volume of the Volumes view) is read on a tracker by the tracker's own numbers - the golden rules of
/// "Set progress from &lt;tracker&gt; to MangaPixer" and of the cards' "read on &lt;tracker&gt;" mark (owner, 2026-10-15). A file's units are
/// <see cref="VolumeGrouping.UnitsOf"/>:
/// <list type="bullet">
/// <item>A chapter file (it may also state its volume): read when the END of its number is at or below the tracker's chapters read -
/// <c>Chapter 012</c> at 12, a range <c>Chapter 010-015</c> only at 15, an extra <c>Chapter 012.5</c> only at 13 (it comes after chapter 12),
/// <c>Chapter 011.5</c> at 12.</item>
/// <item>A volume file (no chapter in its name) counts by its volume: read when the END of its volume number is at or below the tracker's
/// volumes read (<c>Volume 02.5</c> only at 3). Also - only through an EXACT list (MangaDex / Wikipedia; never an estimate or the AniList
/// ratio) - when every whole chapter the list gives its volume(s) is at or below the tracker's chapters read (a user who tracks chapters
/// only); a volume the list gives no whole chapter never counts this way.</item>
/// <item>A declared edition (omnibus, "Volumes in this edition") numbers its own volumes: its volume files are never read this way (its
/// chapter files are). A file whose name states no number is never read this way. A tracker number of 0 or none reads nothing.</item>
/// </list>
/// </summary>
public static class TrackerReadOn
{
    public static bool Covers(UnitNumbers units, TrackerNumbers remote, VolumeMapInput? exactMap, bool declaredEdition)
    {
        if (units.Chapter is { } start)
        {
            var end = units.ChapterEnd is { } e && e > start ? e : start;
            return remote.Chapters is > 0 and var chapters && end <= chapters;
        }
        if (units.Volume is not { } volume || declaredEdition)
            return false;
        var volumeEnd = units.VolumeEnd is { } ve && ve > volume ? ve : volume;
        if (remote.Volumes is > 0 and var volumes && volumeEnd <= volumes)
            return true;
        return remote.Chapters is > 0 and var read && exactMap is { } map && IsExact(map)
            && ListedWholes(map, volume, volumeEnd) is { Count: > 0 } wholes && wholes.Max() <= read;
    }

    /// <summary>
    /// A volume stack of the Volumes view (its volume number and the chapters on disk in it): read when its volume is at or below the
    /// tracker's volumes read, or when it holds at least one whole chapter and every whole chapter in it is at or below the tracker's
    /// chapters read. Extras never decide it.
    /// </summary>
    public static bool CoversStack(decimal volume, IReadOnlyCollection<decimal> chapters, TrackerNumbers remote)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        if (remote.Volumes is > 0 and var volumes && volume <= volumes)
            return true;
        var wholes = chapters.Where(c => decimal.Truncate(c) == c && c >= 1).ToList();
        return remote.Chapters is > 0 and var read && wholes.Count > 0 && wholes.Max() <= read;
    }

    /// <summary>An exact list: MangaDex's aggregate or Wikipedia's chapter list, with volumes, not in chapter mode.</summary>
    public static bool IsExact(VolumeMapInput map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return !map.ChaptersOnly && map.Volumes.Count > 0 && map.Source is VolumeListSource.MangaDex or VolumeListSource.Wikipedia;
    }

    private static List<decimal> ListedWholes(VolumeMapInput map, decimal from, decimal to) =>
        map.Volumes.Where(v => v.Volume >= from && v.Volume <= to)
            .SelectMany(v => v.Chapters).Where(c => decimal.Truncate(c) == c && c >= 1).ToList();
}
