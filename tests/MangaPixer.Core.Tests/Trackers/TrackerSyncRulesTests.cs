namespace com.lifepixer.mangapixer.Tests.Core.Trackers;

using com.lifepixer.mangapixer.Core.Trackers;
using Xunit;

/// <summary>
/// Compare before write (1.43.0, owner rules of 2026-10-13): write only when MangaPixer is ahead; never lower without the user's click;
/// never touch an entry on hold / dropped / completed / rereading; Completed only at the tracker's own total of a FINISHED series (an
/// ongoing series is never set Completed); a bulk / import jump waits for "Send"; "Add series I start" decides a new entry.
/// </summary>
public sealed class TrackerSyncRulesTests
{
    private static readonly DateOnly Started = new(2026, 1, 2);
    private static readonly DateOnly Today = new(2026, 10, 13);

    private static TrackerEntry Remote(TrackerEntryStatus status = TrackerEntryStatus.Reading, int? chapters = null, int? volumes = null,
        int? totalChapters = null, int? totalVolumes = null, bool finished = false, DateOnly? started = null) =>
        new(status, chapters, volumes, totalChapters, totalVolumes, finished, started, null);

    private static TrackerSyncDecision Decide(TrackerNumbers local, TrackerEntry? remote, TrackerNumbers? all = null,
        TrackerSyncMode mode = TrackerSyncMode.Automatic, bool add = true, bool privateEntry = false, int? lastSent = null) =>
        TrackerSyncRules.Decide(new TrackerSyncInput(mode, local, all ?? local, remote, add, privateEntry, Started, Today, lastSent));

    [Fact]
    public void TrackerAhead_NoWrite()
    {
        var decision = Decide(new TrackerNumbers(5, 1), Remote(chapters: 40, volumes: 4));
        Assert.Equal((TrackerEntryState.TrackerAhead, (TrackerEntryWrite?)null), (decision.State, decision.Write));
    }

    [Fact]
    public void Unread_NoWrite()
    {
        Assert.Null(Decide(TrackerNumbers.None, Remote(chapters: 3)).Write);
        Assert.Null(Decide(TrackerNumbers.None, null).Write);
    }

    [Fact]
    public void LowerThanWhatWasSent_IsLowerHere_NoWrite()
    {
        var decision = Decide(new TrackerNumbers(2, null), Remote(chapters: 6), lastSent: 6);
        Assert.Equal((TrackerEntryState.LowerHere, (TrackerEntryWrite?)null), (decision.State, decision.Write));
    }

    [Theory]
    [InlineData(TrackerEntryStatus.OnHold)]
    [InlineData(TrackerEntryStatus.Dropped)]
    [InlineData(TrackerEntryStatus.Completed)]
    [InlineData(TrackerEntryStatus.Rereading)]
    public void AnEntryTheUserSetAside_IsLeftAsItIs(TrackerEntryStatus status)
    {
        var decision = Decide(new TrackerNumbers(50, 5), Remote(status, chapters: 3));
        Assert.Equal((TrackerEntryState.LeftAsIs, (TrackerEntryWrite?)null), (decision.State, decision.Write));
        Assert.Null(Decide(new TrackerNumbers(50, 5), Remote(status, chapters: 3), mode: TrackerSyncMode.SetTo).Write);
    }

    [Fact]
    public void Ahead_WritesTheHigherOfEachField_NeverLoweringOne_AndTheStartDayOnlyWhenEmpty()
    {
        var decision = Decide(new TrackerNumbers(12, 1), Remote(chapters: 10, volumes: 3, started: new DateOnly(2025, 5, 5)));
        Assert.Equal(new TrackerEntryWrite(TrackerEntryStatus.Reading, 12, 3, null, null, null), decision.Write);

        var fresh = Decide(new TrackerNumbers(12, null), Remote(TrackerEntryStatus.PlanToRead));
        Assert.Equal(new TrackerEntryWrite(TrackerEntryStatus.Reading, 12, null, Started, null, null), fresh.Write);
    }

    [Fact]
    public void AnOngoingSeries_AtTheTrackersTotal_IsNotCompleted()
    {
        var decision = Decide(new TrackerNumbers(100, null), Remote(chapters: 99, totalChapters: 100, finished: false));
        Assert.Equal(TrackerEntryStatus.Reading, decision.Write!.Status);
        Assert.Null(decision.Write.FinishedOn);
    }

    [Fact]
    public void AFinishedSeries_AtTheTrackersTotal_IsCompleted_WithTheFinishDay()
    {
        var decision = Decide(new TrackerNumbers(100, null), Remote(chapters: 99, totalChapters: 100, finished: true));
        Assert.Equal((TrackerEntryStatus.Completed, Today), (decision.Write!.Status, decision.Write.FinishedOn));
        // Below the total, or without a known total, it stays Reading.
        Assert.Equal(TrackerEntryStatus.Reading, Decide(new TrackerNumbers(98, null), Remote(chapters: 90, totalChapters: 100, finished: true)).Write!.Status);
        Assert.Equal(TrackerEntryStatus.Reading, Decide(new TrackerNumbers(98, null), Remote(chapters: 90, finished: true)).Write!.Status);
        // The volume total decides only when the tracker lists no chapter total.
        Assert.Equal(TrackerEntryStatus.Completed, Decide(new TrackerNumbers(80, 10), Remote(chapters: 70, totalVolumes: 10, finished: true)).Write!.Status);
    }

    [Fact]
    public void NotOnAnyList_AddedOnlyWithAddSeriesIStart_NeverCompleted_PrivateWhenAsked()
    {
        var added = Decide(new TrackerNumbers(3, null), null, privateEntry: true);
        Assert.Equal(new TrackerEntryWrite(TrackerEntryStatus.Reading, 3, null, Started, null, true), added.Write);

        var off = Decide(new TrackerNumbers(3, null), null, add: false);
        Assert.Equal((TrackerEntryState.NotOnList, (TrackerEntryWrite?)null), (off.State, off.Write));
    }

    [Fact]
    public void ABulkJump_IsHeld_WhileTheAutomaticPartIsSent()
    {
        var decision = Decide(new TrackerNumbers(3, null), Remote(chapters: 1), all: new TrackerNumbers(40, 4));
        Assert.Equal((TrackerEntryState.Held, 3, new TrackerNumbers(40, 4)), (decision.State, decision.Write!.ChaptersRead, decision.Held));

        var onlyBulk = Decide(TrackerNumbers.None, Remote(chapters: 1), all: new TrackerNumbers(40, null));
        Assert.Equal((TrackerEntryState.Held, (TrackerEntryWrite?)null), (onlyBulk.State, onlyBulk.Write));

        var send = Decide(new TrackerNumbers(40, 4), Remote(chapters: 1), mode: TrackerSyncMode.SendAll);
        Assert.Equal((TrackerEntryState.Ok, 40, 4), (send.State, send.Write!.ChaptersRead, send.Write.VolumesRead));
    }

    [Fact]
    public void SetTo_MayLower()
    {
        var decision = Decide(new TrackerNumbers(2, null), Remote(chapters: 6), mode: TrackerSyncMode.SetTo);
        Assert.Equal(2, decision.Write!.ChaptersRead);
    }
}
