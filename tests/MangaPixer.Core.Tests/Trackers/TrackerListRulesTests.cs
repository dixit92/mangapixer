namespace com.lifepixer.mangapixer.Tests.Core.Trackers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Trackers;
using Xunit;

/// <summary>
/// The Tracking page's rules (1.43.0 redesign, owner 2026-10-15): the filter chip of a row, the availability of the two "Set progress"
/// actions, and the golden rules of "read on &lt;tracker&gt;" (<see cref="TrackerReadOn"/>) that both "Set progress from &lt;tracker&gt; to
/// MangaPixer" and the card mark use. Synthetic names only.
/// </summary>
public sealed class TrackerListRulesTests
{
    private static TrackerRowFacts Facts(
        TrackerEntryState state = TrackerEntryState.Ok, bool dontSync = false, bool hasId = true, bool seen = true, bool missing = false,
        TrackerEntryStatus? status = TrackerEntryStatus.Reading, int? local = null, int? all = null, int? held = null, int? remote = null,
        bool addStarted = true) =>
        new(state, dontSync, hasId, seen, missing, status, new TrackerNumbers(local, null), new TrackerNumbers(all ?? local, null),
            new TrackerNumbers(held, null), new TrackerNumbers(remote, null), addStarted);

    [Fact]
    public void Buckets_FollowTheirOrder()
    {
        Assert.Equal(TrackerListBucket.NotSynced, TrackerListRules.BucketOf(Facts(dontSync: true, held: 5)));
        Assert.Equal(TrackerListBucket.NotSynced, TrackerListRules.BucketOf(Facts(TrackerEntryState.NotOnList, local: 3)));
        Assert.Equal(TrackerListBucket.NoId, TrackerListRules.BucketOf(Facts(TrackerEntryState.NoId, hasId: false, local: 3)));
        Assert.Equal(TrackerListBucket.LeftAsIs, TrackerListRules.BucketOf(Facts(status: TrackerEntryStatus.OnHold, local: 9, remote: 3)));
        Assert.Equal(TrackerListBucket.Held, TrackerListRules.BucketOf(Facts(TrackerEntryState.Held, local: 2, all: 9, held: 9, remote: 2)));
        Assert.Equal(TrackerListBucket.TrackerAhead, TrackerListRules.BucketOf(Facts(TrackerEntryState.TrackerAhead, local: 2, remote: 9)));
        Assert.Equal(TrackerListBucket.TrackerAhead, TrackerListRules.BucketOf(Facts(TrackerEntryState.LowerHere, local: 2, remote: 9)));
        Assert.Equal(TrackerListBucket.MangaPixerAhead, TrackerListRules.BucketOf(Facts(TrackerEntryState.Error, local: 9, remote: 2)));
        Assert.Equal(TrackerListBucket.MangaPixerAhead, TrackerListRules.BucketOf(Facts(TrackerEntryState.Waiting, local: 9, remote: 2)));
        Assert.Equal(TrackerListBucket.Problem, TrackerListRules.BucketOf(Facts(TrackerEntryState.Error, local: 2, remote: 2)));
        Assert.Equal(TrackerListBucket.Unchecked, TrackerListRules.BucketOf(Facts(TrackerEntryState.Pending, seen: false, local: 2)));
        Assert.Equal(TrackerListBucket.InSync, TrackerListRules.BucketOf(Facts(local: 4, remote: 4)));
    }

    [Fact]
    public void Attention_IsHeldAndTrackerAhead()
    {
        Assert.Equal([TrackerListBucket.TrackerAhead, TrackerListBucket.Held],
            Enum.GetValues<TrackerListBucket>().Where(TrackerListRules.NeedsAttention).ToArray());
    }

    [Fact]
    public void FromTracker_NeedsAReadEntryAheadOfEveryMark()
    {
        Assert.Equal(TrackerActionBlock.None, TrackerListRules.FromTrackerBlock(Facts(local: 3, remote: 9)));
        // A held jump to 9 already covers the tracker's 9: nothing to mark.
        Assert.Equal(TrackerActionBlock.NotAhead, TrackerListRules.FromTrackerBlock(Facts(local: 3, all: 9, held: 9, remote: 9)));
        Assert.Equal(TrackerActionBlock.NotChecked, TrackerListRules.FromTrackerBlock(Facts(seen: false, remote: 9)));
        Assert.Equal(TrackerActionBlock.NotOnList, TrackerListRules.FromTrackerBlock(Facts(missing: true)));
        Assert.Equal(TrackerActionBlock.NoId, TrackerListRules.FromTrackerBlock(Facts(TrackerEntryState.NoId, hasId: false)));
        // On hold on the tracker does not stop pulling its numbers HERE (nothing is written to the tracker).
        Assert.Equal(TrackerActionBlock.None, TrackerListRules.FromTrackerBlock(Facts(status: TrackerEntryStatus.OnHold, local: 1, remote: 9)));
    }

    [Fact]
    public void ToTracker_NeverLowers_AndLeavesTheUsersStatusesAlone()
    {
        Assert.Equal(TrackerActionBlock.None, TrackerListRules.ToTrackerBlock(Facts(TrackerEntryState.Held, local: 2, all: 9, held: 9, remote: 2)));
        Assert.Equal(TrackerActionBlock.NotAhead, TrackerListRules.ToTrackerBlock(Facts(TrackerEntryState.TrackerAhead, local: 2, remote: 9)));
        Assert.Equal(TrackerActionBlock.LeftAsIs, TrackerListRules.ToTrackerBlock(Facts(status: TrackerEntryStatus.Dropped, local: 9, remote: 2)));
        Assert.Equal(TrackerActionBlock.DontSync, TrackerListRules.ToTrackerBlock(Facts(dontSync: true, local: 9, remote: 2)));
        Assert.Equal(TrackerActionBlock.NoId, TrackerListRules.ToTrackerBlock(Facts(TrackerEntryState.NoId, hasId: false, local: 9)));
        Assert.Equal(TrackerActionBlock.NotOnList, TrackerListRules.ToTrackerBlock(Facts(missing: true, local: 9, addStarted: false)));
        Assert.Equal(TrackerActionBlock.None, TrackerListRules.ToTrackerBlock(Facts(missing: true, local: 9, addStarted: true)));
        Assert.Equal(TrackerActionBlock.None, TrackerListRules.ToTrackerBlock(Facts(TrackerEntryState.Pending, seen: false, local: 3)));
        Assert.Equal(TrackerActionBlock.NotAhead, TrackerListRules.ToTrackerBlock(Facts(TrackerEntryState.Pending, seen: false)));
    }

    // --- Read on the tracker: the golden rules ---

    private static UnitNumbers Units(string name, string? container = null) =>
        VolumeGrouping.UnitsOf(new GroupingRow("id", GroupingRowKind.Archive, name, name.ToLowerInvariant(), container));

    private static readonly VolumeMapInput Exact = new(
        [new VolumeMapVolume(1, [1m, 2m, 3m, 3.5m, 4m]), new VolumeMapVolume(2, [5m, 6m, 7m, 8m]), new VolumeMapVolume(3, [12.5m])],
        4.0, 3, true, VolumeListSource.MangaDex);

    [Theory]
    [InlineData("Chapter 012", 12, true)]
    [InlineData("Chapter 013", 12, false)]
    [InlineData("Chapter 010-015", 12, false)] // a range is read when its END is
    [InlineData("Chapter 010-015", 15, true)]
    [InlineData("Chapter 012.5", 12, false)] // an extra comes after its chapter
    [InlineData("Chapter 012.5", 13, true)]
    [InlineData("Chapter 011.5", 12, true)]
    [InlineData("Vol. 03 Chapter 017", 17, true)] // a chapter file counts by its chapter
    [InlineData("Chapter 001", 0, false)] // nothing read on the tracker
    [InlineData("Extras", 99, false)] // no number
    public void AChapterFile_IsReadUpToTheTrackersChapters(string name, int chapters, bool expected) =>
        Assert.Equal(expected, TrackerReadOn.Covers(Units(name), new TrackerNumbers(chapters, null), Exact, declaredEdition: false));

    [Theory]
    [InlineData("Volume 02", null, 2, true)]
    [InlineData("Volume 03", null, 2, false)]
    [InlineData("Volume 02.5", null, 2, false)] // a bonus volume comes after volume 2
    [InlineData("Volume 02.5", null, 3, true)]
    [InlineData("Volume 01-02", null, 1, false)]
    [InlineData("Vol. 01-02", 8, null, true)] // a user who tracks chapters only: every listed whole chapter of volumes 1-2 <= 8
    [InlineData("Volume 02", 7, null, false)] // chapter 8 of volume 2 is not read yet
    [InlineData("Volume 03", 13, null, false)] // volume 3 lists only an extra: never by chapters
    [InlineData("02", null, 2, true)] // a bare number in a Volumes folder is a volume (container below)
    public void AVolumeFile_CountsByItsVolume_OrByItsListedChapters(string name, int? chapters, int? volumes, bool expected) =>
        Assert.Equal(expected, TrackerReadOn.Covers(Units(name, name == "02" ? "Volumes" : null), new TrackerNumbers(chapters, volumes), Exact,
            declaredEdition: false));

    [Fact]
    public void AVolumeFile_ByChapters_NeedsAnExactList_AndNotADeclaredEdition()
    {
        var byChapters = new TrackerNumbers(8, null);
        Assert.True(TrackerReadOn.Covers(Units("Volume 02"), byChapters, Exact, false));
        Assert.False(TrackerReadOn.Covers(Units("Volume 02"), byChapters, Exact with { Source = VolumeListSource.AniList }, false));
        Assert.False(TrackerReadOn.Covers(Units("Volume 02"), byChapters, null, false));
        // A declared edition numbers its own volumes: never read by volume number or chapters; its chapter files still are.
        Assert.False(TrackerReadOn.Covers(Units("Volume 02"), new TrackerNumbers(8, 5), Exact, true));
        Assert.True(TrackerReadOn.Covers(Units("Chapter 004"), new TrackerNumbers(8, 5), Exact, true));
    }

    [Fact]
    public void AVolumeStack_IsReadByVolume_OrWhenAllItsWholeChaptersAre()
    {
        Assert.True(TrackerReadOn.CoversStack(2, [5m, 6m], new TrackerNumbers(null, 2)));
        Assert.True(TrackerReadOn.CoversStack(2, [5m, 6m, 6.5m], new TrackerNumbers(6, null))); // the extra does not decide it
        Assert.False(TrackerReadOn.CoversStack(2, [5m, 6m, 7m], new TrackerNumbers(6, 1)));
        Assert.False(TrackerReadOn.CoversStack(3, [12.5m], new TrackerNumbers(99, null))); // only an extra here
        Assert.False(TrackerReadOn.CoversStack(1, [1m], TrackerNumbers.None));
    }
}
