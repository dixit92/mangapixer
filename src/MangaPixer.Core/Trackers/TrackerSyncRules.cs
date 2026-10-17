namespace com.lifepixer.mangapixer.Core.Trackers;

using com.lifepixer.mangapixer.Core.Catalog;

// 1.43.0 tracker sync (lane E): compare before write. PURE - MangaPixer's numbers and the tracker's entry in, at most one write out.
// Owner rules (2026-10-13): write only when MangaPixer is ahead; never lower a tracker's number without the user's click; never touch
// an entry the user set on hold / dropped / completed / rereading on the tracker; Completed only when the tracker's own total is known
// AND reached AND the series is finished - an ongoing series is never set Completed; no pull from the tracker into read marks.

/// <summary>How a sync was asked for.</summary>
public enum TrackerSyncMode
{
    /// <summary>A new read mark (or the user's "Sync now"): the automatic numbers; a larger jump from bulk / import marks is held.</summary>
    Automatic = 0,

    /// <summary>The user's "Send": every mark counts (the held jump goes out). Still never lowers.</summary>
    SendAll = 1,

    /// <summary>"Set the tracker to N": the user's explicit numbers, which may lower the tracker (the UI asks first).</summary>
    SetTo = 2,

    /// <summary>
    /// 1.44.0: the user's "Set &lt;tracker&gt;'s volumes to N" - only the volume count the tracker's OWN chapters complete on the exact list
    /// (<see cref="TrackerVolumeCatchUp"/>), from the entry as just read; the chapters and the status stay as the tracker has them.
    /// </summary>
    VolumesFromChapters = 3,
}

/// <summary>Everything one decision reads.</summary>
/// <param name="Local">The numbers this mode may send (automatic, all, or the user's explicit ones).</param>
/// <param name="All">The numbers with every mark (for the held jump); equal to <paramref name="Local"/> outside Automatic.</param>
/// <param name="Remote">The user's entry as just read, or null when the series is on none of their lists.</param>
/// <param name="AddStartedSeries">"Add series I start": a series on no list is added.</param>
/// <param name="PrivateEntry">Add a new entry as private (the user's setting, where the tracker supports it).</param>
/// <param name="StartedOn">The day of the user's earliest mark of the series (sent only when the tracker has no start date).</param>
/// <param name="Today">Today (the user's finish date when the entry turns Completed).</param>
/// <param name="LastSentChapters">The chapters MangaPixer last wrote (tells "lower here" from "tracker ahead").</param>
/// <param name="StartedOverride">1.43.0 (owner A1): the user's own start date - it wins over the automatic one and is sent with the next
/// write (also on its own while <paramref name="DatesPending"/>). Null = automatic. Only for a tracker that keeps dates.</param>
/// <param name="FinishedOverride">The user's own finish date, sent when the entry turns Completed (instead of today).</param>
/// <param name="DatesPending">A date the user set has not reached the tracker yet.</param>
/// <param name="ExactMap">1.44.0: the series' volume list, for <see cref="TrackerSyncMode.VolumesFromChapters"/> (only an exact one counts).</param>
public sealed record TrackerSyncInput(
    TrackerSyncMode Mode,
    TrackerNumbers Local,
    TrackerNumbers All,
    TrackerEntry? Remote,
    bool AddStartedSeries,
    bool PrivateEntry,
    DateOnly? StartedOn,
    DateOnly Today,
    int? LastSentChapters = null,
    int? LastSentVolumes = null,
    DateOnly? StartedOverride = null,
    DateOnly? FinishedOverride = null,
    bool DatesPending = false,
    VolumeMapInput? ExactMap = null);

/// <summary>The outcome: the entry state, the one write to make (or null), and the jump still held for "Send".</summary>
public sealed record TrackerSyncDecision(TrackerEntryState State, TrackerEntryWrite? Write, TrackerNumbers Held);

public static class TrackerSyncRules
{
    private static bool IsLeftAsIs(TrackerEntryStatus status) =>
        status is TrackerEntryStatus.OnHold or TrackerEntryStatus.Dropped or TrackerEntryStatus.Completed or TrackerEntryStatus.Rereading;

    public static TrackerSyncDecision Decide(TrackerSyncInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var remote = input.Remote;
        var remoteNumbers = remote is null ? TrackerNumbers.None : new TrackerNumbers(remote.ChaptersRead, remote.VolumesRead);

        if (remote is not null && IsLeftAsIs(remote.Status))
            return new TrackerSyncDecision(TrackerEntryState.LeftAsIs, null, TrackerNumbers.None);

        if (input.Mode == TrackerSyncMode.SetTo)
            return SetTo(input, remote);
        if (input.Mode == TrackerSyncMode.VolumesFromChapters)
            return VolumesFromChapters(input, remote);

        if (remote is null && !input.AddStartedSeries)
        {
            // "Add series I start" is off: a series on no list is never added (and nothing is held for it).
            var anything = !input.Local.IsEmpty || !input.All.IsEmpty;
            return new TrackerSyncDecision(anything ? TrackerEntryState.NotOnList : TrackerEntryState.Ok, null, TrackerNumbers.None);
        }

        // What the tracker will show after this decision, to tell whether a bulk / import jump is still waiting.
        TrackerNumbers Held(TrackerNumbers afterWrite) =>
            input.Mode == TrackerSyncMode.Automatic && input.All.IsAheadOf(Combine(afterWrite, remoteNumbers)) ? input.All : TrackerNumbers.None;

        if (input.Local.IsEmpty || !input.Local.IsAheadOf(remoteNumbers))
        {
            // Nothing to send: unread, equal, or the tracker is ahead (never lowered automatically). A start date the user set still goes
            // out on its own (1.43.0 owner A1): the entry's status and numbers stay as the tracker has them.
            var datesOnly = remote is not null && input.DatesPending && input.StartedOverride is { } own && own != remote.StartedOn
                ? new TrackerEntryWrite(remote.Status, null, null, own, null, null)
                : null;
            var held = Held(TrackerNumbers.None);
            if (!held.IsEmpty)
                return new TrackerSyncDecision(TrackerEntryState.Held, datesOnly, held);
            if (remote is null)
                return new TrackerSyncDecision(input.Local.IsEmpty ? TrackerEntryState.Ok : TrackerEntryState.NotOnList, null, TrackerNumbers.None);
            if (remoteNumbers.IsAheadOf(input.Local))
            {
                var lower = (input.LastSentChapters ?? 0) > (input.Local.Chapters ?? 0) || (input.LastSentVolumes ?? 0) > (input.Local.Volumes ?? 0);
                return new TrackerSyncDecision(lower ? TrackerEntryState.LowerHere : TrackerEntryState.TrackerAhead, datesOnly, TrackerNumbers.None);
            }
            return new TrackerSyncDecision(TrackerEntryState.Ok, datesOnly, TrackerNumbers.None);
        }

        if (remote is null)
        {
            if (!input.AddStartedSeries)
                return new TrackerSyncDecision(TrackerEntryState.NotOnList, null, TrackerNumbers.None);
            // A new entry: the tracker's totals are unknown here, so it is never created as Completed.
            var create = new TrackerEntryWrite(TrackerEntryStatus.Reading, input.Local.Chapters, input.Local.Volumes,
                input.StartedOverride ?? input.StartedOn, null, input.PrivateEntry ? true : null);
            var heldAfterCreate = Held(input.Local);
            return new TrackerSyncDecision(heldAfterCreate.IsEmpty ? TrackerEntryState.Ok : TrackerEntryState.Held, create, heldAfterCreate);
        }

        // MangaPixer is ahead: write the higher of both numbers for each field (never lower one while raising the other).
        var chapters = Higher(input.Local.Chapters, remote.ChaptersRead);
        var volumes = Higher(input.Local.Volumes, remote.VolumesRead);
        var completed = Completes(remote, chapters, volumes);
        var write = new TrackerEntryWrite(
            completed ? TrackerEntryStatus.Completed : TrackerEntryStatus.Reading,
            chapters,
            volumes,
            StartOf(input, remote),
            completed ? FinishOf(input, remote) : null,
            null);
        var heldAfter = Held(new TrackerNumbers(chapters, volumes));
        return new TrackerSyncDecision(heldAfter.IsEmpty ? TrackerEntryState.Ok : TrackerEntryState.Held, write, heldAfter);
    }

    /// <summary>
    /// Completed only when the tracker's own total is known AND reached AND the series is finished (owner Q12: an ongoing series is
    /// never set Completed). Chapters decide when the tracker lists a chapter total; the volume total only when it lists none.
    /// </summary>
    public static bool Completes(TrackerEntry remote, int? chapters, int? volumes)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (!remote.SeriesFinished)
            return false;
        if (remote.TotalChapters is > 0 and var totalChapters)
            return chapters >= totalChapters;
        if (remote.TotalVolumes is > 0 and var totalVolumes)
            return volumes >= totalVolumes;
        return false;
    }

    private static TrackerSyncDecision SetTo(TrackerSyncInput input, TrackerEntry? remote)
    {
        if (input.Local.IsEmpty)
            return new TrackerSyncDecision(TrackerEntryState.Ok, null, TrackerNumbers.None);
        var chapters = input.Local.Chapters ?? remote?.ChaptersRead;
        var volumes = input.Local.Volumes ?? remote?.VolumesRead;
        var completed = remote is not null && Completes(remote, chapters, volumes);
        var write = new TrackerEntryWrite(
            completed ? TrackerEntryStatus.Completed : TrackerEntryStatus.Reading,
            chapters,
            volumes,
            StartOf(input, remote),
            completed ? FinishOf(input, remote!) : null,
            remote is null && input.PrivateEntry ? true : null);
        return new TrackerSyncDecision(TrackerEntryState.Ok, write, TrackerNumbers.None);
    }

    /// <summary>
    /// 1.44.0: only the volumes, raised to what the tracker's own chapters complete (nothing else in the write - the status is the tracker's
    /// own, so never Completed, never on hold / dropped / re-read: those are left as is above). No offer, nothing written. The state is what
    /// the list shows next: the tracker ahead of MangaPixer's automatic numbers, or in step.
    /// </summary>
    private static TrackerSyncDecision VolumesFromChapters(TrackerSyncInput input, TrackerEntry? remote)
    {
        if (remote is null)
            return new TrackerSyncDecision(TrackerEntryState.NotOnList, null, TrackerNumbers.None);
        var remoteNumbers = new TrackerNumbers(remote.ChaptersRead, remote.VolumesRead);
        if (TrackerVolumeCatchUp.Offer(remote, input.ExactMap) is not { } volumes)
            return new TrackerSyncDecision(remoteNumbers.IsAheadOf(input.Local) ? TrackerEntryState.TrackerAhead : TrackerEntryState.Ok, null,
                TrackerNumbers.None);
        var write = new TrackerEntryWrite(remote.Status, null, volumes, null, null, null);
        var after = remoteNumbers with { Volumes = volumes };
        return new TrackerSyncDecision(after.IsAheadOf(input.Local) ? TrackerEntryState.TrackerAhead : TrackerEntryState.Ok, write, TrackerNumbers.None);
    }

    /// <summary>
    /// The start date a write carries: the user's own when it differs from the tracker's (owner A1: it wins), else the automatic one (the
    /// first counted mark) only when the tracker has none - and never after a finish date the tracker keeps (an entry finished elsewhere:
    /// a later first mark here would put its start after its finish); null = left as the tracker has it.
    /// </summary>
    private static DateOnly? StartOf(TrackerSyncInput input, TrackerEntry? remote)
    {
        if (input.StartedOverride is { } own)
            return own != remote?.StartedOn ? own : null;
        if (remote?.StartedOn is not null)
            return null;
        return remote?.FinishedOn is { } finished && input.StartedOn > finished ? null : input.StartedOn;
    }

    /// <summary>The finish date of a write that completes the entry: the user's own (when it differs), else today when the tracker has none.</summary>
    private static DateOnly? FinishOf(TrackerSyncInput input, TrackerEntry remote)
    {
        if (input.FinishedOverride is { } own)
            return own != remote.FinishedOn ? own : null;
        return remote.FinishedOn is null ? input.Today : null;
    }

    private static int? Higher(int? local, int? remote) => local is null ? remote : remote is null ? local : Math.Max(local.Value, remote.Value);

    private static TrackerNumbers Combine(TrackerNumbers a, TrackerNumbers b) =>
        new(Higher(a.Chapters, b.Chapters), Higher(a.Volumes, b.Volumes));
}
