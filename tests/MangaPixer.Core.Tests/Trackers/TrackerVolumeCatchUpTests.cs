namespace com.lifepixer.mangapixer.Tests.Core.Trackers;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Trackers;
using Xunit;

/// <summary>
/// 1.44.0 (owner, 2026-10-17): MyAnimeList listed 271 chapters but 25 volumes, while 271 chapters finish volume 27 on the series' exact
/// volume list - MangaPixer offers "Set MyAnimeList's volumes to 27" (sent only on the user's click). The offer is raise-only, exact lists
/// only, never for an entry left as the user put it, never above the tracker's own volume total; the write carries only the volumes.
/// </summary>
public sealed class TrackerVolumeCatchUpTests
{
    /// <summary>28 volumes of 10 chapters each (volume 1 = 1-10 ... volume 28 = 271-280), plus an extra 270.5 in volume 27.</summary>
    private static VolumeMapInput List(VolumeListSource source = VolumeListSource.MangaDex, bool chaptersOnly = false) => new(
        Enumerable.Range(1, 28).Select(v => new VolumeMapVolume(v,
            Enumerable.Range((v - 1) * 10 + 1, 10).Select(c => (decimal)c).Concat(v == 27 ? [270.5m] : []).ToList())).ToList(),
        10.0, 28, true, source, ChaptersOnly: chaptersOnly);

    [Theory]
    [InlineData(TrackerEntryStatus.Reading, 271, 25, null, 27)]   // the owner's entry
    [InlineData(TrackerEntryStatus.PlanToRead, 271, 25, null, 27)]
    [InlineData(TrackerEntryStatus.Reading, 270, 25, null, 27)]   // 270 finishes volume 27 (its extra 270.5 is never needed)
    [InlineData(TrackerEntryStatus.Reading, 269, 25, null, 26)]
    [InlineData(TrackerEntryStatus.Reading, 271, 27, null, null)] // already there
    [InlineData(TrackerEntryStatus.Reading, 271, 30, null, null)] // never lowered
    [InlineData(TrackerEntryStatus.Reading, 271, null, null, 27)]
    [InlineData(TrackerEntryStatus.Reading, 271, 25, 26, null)]   // the tracker knows 26 volumes: left alone
    [InlineData(TrackerEntryStatus.Reading, 9, 0, null, null)]    // volume 1 not finished
    [InlineData(TrackerEntryStatus.Reading, 0, 0, null, null)]
    [InlineData(TrackerEntryStatus.OnHold, 271, 25, null, null)]
    [InlineData(TrackerEntryStatus.Dropped, 271, 25, null, null)]
    [InlineData(TrackerEntryStatus.Completed, 271, 25, null, null)]
    [InlineData(TrackerEntryStatus.Rereading, 271, 25, null, null)]
    public void TheOffer_IsTheVolumeTheTrackersOwnChaptersFinish(TrackerEntryStatus status, int chapters, int? volumes, int? total, int? expected)
    {
        Assert.Equal(expected, TrackerVolumeCatchUp.Offer(status, chapters, volumes, total, List()));
    }

    [Fact]
    public void OnlyAnExactList_Counts()
    {
        Assert.Equal(27, TrackerVolumeCatchUp.Offer(TrackerEntryStatus.Reading, 271, 25, null, List(VolumeListSource.Wikipedia)));
        Assert.Null(TrackerVolumeCatchUp.Offer(TrackerEntryStatus.Reading, 271, 25, null, List(VolumeListSource.AniList)));
        Assert.Null(TrackerVolumeCatchUp.Offer(TrackerEntryStatus.Reading, 271, 25, null, List(chaptersOnly: true)));
        Assert.Null(TrackerVolumeCatchUp.Offer(TrackerEntryStatus.Reading, 271, 25, null, null));
        Assert.Null(TrackerVolumeCatchUp.Offer(null, 271, 25, null, List()));
    }

    [Fact]
    public void TheDecision_WritesOnlyTheVolumes_WithTheTrackersOwnStatus()
    {
        var remote = new TrackerEntry(TrackerEntryStatus.Reading, 271, 25, 300, null, true, new DateOnly(2024, 8, 20), null);
        var decision = TrackerSyncRules.Decide(new TrackerSyncInput(TrackerSyncMode.VolumesFromChapters, TrackerNumbers.None, TrackerNumbers.None,
            remote, true, false, null, new DateOnly(2026, 10, 17), ExactMap: List()));

        var write = Assert.IsType<TrackerEntryWrite>(decision.Write);
        Assert.Equal(new TrackerEntryWrite(TrackerEntryStatus.Reading, null, 27, null, null, null), write);
        Assert.Equal(TrackerEntryState.TrackerAhead, decision.State); // nothing read here: the tracker stays ahead

        // Nothing to offer: no write. Left as is (on hold): no write either.
        Assert.Null(TrackerSyncRules.Decide(new TrackerSyncInput(TrackerSyncMode.VolumesFromChapters, TrackerNumbers.None, TrackerNumbers.None,
            remote with { VolumesRead = 27 }, true, false, null, new DateOnly(2026, 10, 17), ExactMap: List())).Write);
        Assert.Null(TrackerSyncRules.Decide(new TrackerSyncInput(TrackerSyncMode.VolumesFromChapters, TrackerNumbers.None, TrackerNumbers.None,
            remote with { Status = TrackerEntryStatus.OnHold }, true, false, null, new DateOnly(2026, 10, 17), ExactMap: List())).Write);
    }
}
