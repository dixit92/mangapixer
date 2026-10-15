namespace com.lifepixer.mangapixer.Tests.Core.Trackers;

using com.lifepixer.mangapixer.Core.Trackers;
using Xunit;

/// <summary>
/// Owner A1 (1.43.0): MyAnimeList gets start / finish dates, and the user can override them per series. The automatic start is the first
/// counted mark and goes out only when the tracker has none; a date the user set wins and is sent on the next write - also on its own when
/// nothing else changed; cleared, the automatic date applies again. The finish date goes out only when the entry turns Completed.
/// </summary>
public sealed class TrackerDateRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    private static readonly DateOnly FirstMark = new(2026, 10, 1);
    private static readonly DateOnly Own = new(2020, 5, 6);

    private static TrackerEntry Remote(int chapters, DateOnly? started = null, DateOnly? finished = null, bool finishedSeries = false, int? total = null) =>
        new(TrackerEntryStatus.Reading, chapters, null, total, null, finishedSeries, started, finished);

    private static TrackerSyncDecision Decide(int local, TrackerEntry? remote, DateOnly? startOverride = null, DateOnly? finishOverride = null,
        bool pending = false, TrackerSyncMode mode = TrackerSyncMode.Automatic) =>
        TrackerSyncRules.Decide(new TrackerSyncInput(mode, new TrackerNumbers(local, null), new TrackerNumbers(local, null), remote, true, false,
            FirstMark, Today, StartedOverride: startOverride, FinishedOverride: finishOverride, DatesPending: pending));

    [Fact]
    public void TheAutomaticStart_GoesOutOnlyWhenTheTrackerHasNone()
    {
        Assert.Equal(FirstMark, Decide(5, Remote(2)).Write!.StartedOn);
        Assert.Null(Decide(5, Remote(2, started: new DateOnly(2019, 1, 1))).Write!.StartedOn);
        Assert.Equal(FirstMark, Decide(5, null).Write!.StartedOn); // a new entry
    }

    [Fact]
    public void TheAutomaticStart_IsNeverLaterThanTheTrackersFinish()
    {
        // The tracker kept a finish but no start (an entry finished elsewhere): a first mark made after that finish would put the start
        // after the finish - left as the tracker has it.
        Assert.Null(Decide(5, Remote(2, finished: new DateOnly(2025, 10, 17))).Write!.StartedOn);
        // A first mark on or before the tracker's finish still goes out.
        Assert.Equal(FirstMark, Decide(5, Remote(2, finished: FirstMark)).Write!.StartedOn);
        Assert.Equal(FirstMark, Decide(5, Remote(2, finished: Today)).Write!.StartedOn);
    }

    [Fact]
    public void TheUsersStart_WinsOverTheAutomaticAndTheTrackers()
    {
        Assert.Equal(Own, Decide(5, Remote(2), Own).Write!.StartedOn);
        Assert.Equal(Own, Decide(5, Remote(2, started: new DateOnly(2019, 1, 1)), Own).Write!.StartedOn);
        Assert.Equal(Own, Decide(5, null, Own).Write!.StartedOn);
        // Already on the tracker: not sent again.
        Assert.Null(Decide(5, Remote(2, started: Own), Own).Write!.StartedOn);
    }

    [Fact]
    public void APendingStart_IsSentOnItsOwn_WithTheTrackersStatusAndNoNumbers()
    {
        var decision = Decide(5, Remote(5), Own, pending: true);
        Assert.Equal(TrackerEntryState.Ok, decision.State);
        Assert.Equal(new TrackerEntryWrite(TrackerEntryStatus.Reading, null, null, Own, null, null), decision.Write);

        // The tracker is ahead: its numbers are never lowered, the date still goes out.
        var ahead = Decide(5, Remote(9), Own, pending: true);
        Assert.Equal(TrackerEntryState.TrackerAhead, ahead.State);
        Assert.Equal(new TrackerEntryWrite(TrackerEntryStatus.Reading, null, null, Own, null, null), ahead.Write);

        // Not pending, or already there, or no override: nothing to write.
        Assert.Null(Decide(5, Remote(5), Own).Write);
        Assert.Null(Decide(5, Remote(5, started: Own), Own, pending: true).Write);
        Assert.Null(Decide(5, Remote(5), pending: true).Write);
    }

    [Fact]
    public void TheFinish_GoesOutWhenTheEntryCompletes_TheUsersWinsOverToday()
    {
        var finished = Remote(9, finishedSeries: true, total: 10);
        Assert.Equal(Today, Decide(10, finished).Write!.FinishedOn);
        Assert.Equal(Own, Decide(10, finished, finishOverride: Own).Write!.FinishedOn);
        // Not completing: no finish date, the user's waits.
        Assert.Null(Decide(9, Remote(5), finishOverride: Own).Write!.FinishedOn);
    }

    [Fact]
    public void SetTo_CarriesTheUsersStart()
    {
        var write = Decide(3, Remote(8), Own, mode: TrackerSyncMode.SetTo).Write!;
        Assert.Equal((3, Own), (write.ChaptersRead, write.StartedOn));
    }

    [Fact]
    public void AnEntryLeftAsIs_GetsNoDateEither()
    {
        var onHold = new TrackerEntry(TrackerEntryStatus.OnHold, 2, null, null, null, false, null, null);
        Assert.Null(Decide(5, onHold, Own, pending: true).Write);
    }
}
