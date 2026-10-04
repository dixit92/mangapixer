namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Reach;
using Xunit;

/// <summary>
/// 1.34.0 (owner report 2026-10-04): a webtoon named <c>&lt;running index&gt; [&lt;chapter&gt; - &lt;title&gt;]</c> whose MangaDex list
/// is near-empty (volumes "0" and "1" with one chapter each, the rest unassigned) showed "Volume 1 - 1 chapter", Volumes 2-12 as
/// MISSING and every chapter loose. The rules (<see cref="VolumeListRules"/>) and the chapter mode of the grouping and the progress.
/// Synthetic names only.
/// </summary>
public sealed class WebtoonChapterModeTests
{
    private static GroupingRow Archive(string name) => new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant());

    /// <summary>The owner's shape: <c>0001 [0000]</c>, <c>0002 [0000.5]</c>, then <c>0003 [0001 - Some Title]</c> ... (index = chapter + 2).</summary>
    private static List<GroupingRow> IndexedWebtoon(int lastChapter, params int[] without)
    {
        var rows = new List<GroupingRow> { Archive("0001 [0000].cbz"), Archive("0002 [0000.5].cbz") };
        for (var c = 1; c <= lastChapter; c++)
        {
            if (!without.Contains(c))
                rows.Add(Archive($"{c + 2:0000} [{c:0000} - Some Title].cbz"));
        }
        return rows;
    }

    private static VolumeMapEntry Entry(string volume, params string[] chapters) => new(volume, chapters);

    /// <summary>The degenerate map as the server builds it (volumes "0" and "1", one chapter each, ratio 1.0, 12 English volumes).</summary>
    private static VolumeMapInput Degenerate(bool chaptersOnly) =>
        new([new VolumeMapVolume(0, [0m]), new VolumeMapVolume(1, [1m])], 1.0, 0, true, VolumeListSource.MangaDex,
            ReleasedVolumeCount: 12, ReleasedLanguage: "en", ChaptersOnly: chaptersOnly);

    [Fact]
    public void NearEmpty_IsAtMostOneRealVolume_WithMoreChaptersUnassignedThanPlaced()
    {
        var unassigned = Enumerable.Range(2, 272).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.True(VolumeListRules.IsNearEmpty([Entry("0", "0"), Entry("1", "1")], unassigned));
        Assert.Equal(1, VolumeListRules.RealVolumeCount([Entry("0", "0"), Entry("1", "1")]));
        // Two real volumes: a list, however much is unassigned.
        Assert.False(VolumeListRules.IsNearEmpty([Entry("1", "1"), Entry("2", "2")], unassigned));
        // One real volume holding most chapters (a one-volume series): a list.
        Assert.False(VolumeListRules.IsNearEmpty([Entry("1", "1", "2", "3", "4", "5")], ["6", "7"]));
        // Nothing unassigned: never near-empty.
        Assert.False(VolumeListRules.IsNearEmpty([Entry("0", "0")], []));
        // "none" and unparsable keys are not volumes.
        Assert.Equal(0, VolumeListRules.RealVolumeCount([Entry("none", "1"), Entry("x", "2"), Entry("0.5", "3")]));
    }

    [Theory]
    [InlineData(true, null, false, true)] // a MangaUpdates webtoon without a real list
    [InlineData(null, MetadataOrigin.Korea, false, true)] // a manhwa
    [InlineData(null, MetadataOrigin.ChinaTaiwan, false, true)] // a manhua
    [InlineData(true, MetadataOrigin.Korea, true, false)] // a real list keeps the volume stacks
    [InlineData(false, MetadataOrigin.Japan, false, false)] // manga: the AniList ratio fallback applies as before
    [InlineData(null, null, false, false)]
    public void ChaptersOnly_IsAWebtoonOrManhwaOrManhua_WithoutARealList(bool? webtoon, MetadataOrigin? origin, bool hasRealList, bool expected) =>
        Assert.Equal(expected, VolumeListRules.ChaptersOnly(webtoon, origin, hasRealList));

    [Fact]
    public void TheOwnersReport_TrustingTheDegenerateList_DrawsElevenMissingVolumes()
    {
        // What the owner saw (the parser fix alone does not help): volume 1 holds chapter 1, volumes 2-12 are placeholders.
        var r = VolumeGrouping.Group(IndexedWebtoon(30), Degenerate(chaptersOnly: false), markMissingVolumes: true);
        Assert.Equal(11, r.MissingVolumeCount);
        Assert.Equal(Enumerable.Range(2, 11), r.MissingVolumeNumbers);
    }

    [Fact]
    public void ChapterMode_ListsTheChaptersInChapterOrder_WithNoVolumePlaceholders()
    {
        var rows = IndexedWebtoon(30, without: 7);
        var r = VolumeGrouping.Group(rows, Degenerate(chaptersOnly: true), markMissingVolumes: true);

        Assert.Equal(0, r.StackCount);
        Assert.Equal(0, r.MissingVolumeCount);
        Assert.Empty(r.MissingVolumeNumbers);
        Assert.False(r.HasVolumes);
        Assert.All(r.Entries, e => Assert.Equal(VolumeEntryKind.Archive, e.Kind));
        Assert.Equal(rows.Count, r.Entries.Count);
        // Chapter 7 is a hole below the highest chapter here: the missing count says so.
        Assert.Equal([7m], r.MissingChapterUnits);
    }

    [Fact]
    public void ChapterMode_OrdersByChapterNumber_NotByName_AndUnnumberedFilesLast()
    {
        var rows = new List<GroupingRow>
        {
            Archive("Some Webtoon - Episode 10.cbz"), Archive("Some Webtoon - Episode 9.cbz"), Archive("Some Webtoon c011.cbz"),
            Archive("Some Webtoon Art Book.cbz"), Archive("Some Webtoon c009.5.cbz"),
        };
        var map = VolumeMapInput.Empty with { ChaptersOnly = true };
        var names = VolumeGrouping.Group(rows, map).Entries.Select(e => e.Row!.Name).ToList();
        Assert.Equal(
            ["Some Webtoon - Episode 9.cbz", "Some Webtoon c009.5.cbz", "Some Webtoon - Episode 10.cbz", "Some Webtoon c011.cbz", "Some Webtoon Art Book.cbz"],
            names);
        // Outside chapter mode loose archives keep their name order.
        Assert.Equal("Some Webtoon - Episode 10.cbz", VolumeGrouping.Group(rows, VolumeMapInput.Empty).Entries[0].Row!.Name);
    }

    [Fact]
    public void ChapterMode_FileNamesThatStateTheirVolume_StillGroup()
    {
        var rows = new List<GroupingRow> { Archive("Some Webtoon v01 c001"), Archive("Some Webtoon v01 c002"), Archive("Some Webtoon v02 c003") };
        var r = VolumeGrouping.Group(rows, VolumeMapInput.Empty with { ChaptersOnly = true, ReleasedVolumeCount = 12 }, markMissingVolumes: true);
        Assert.Equal(2, r.StackCount);
        Assert.Equal(0, r.MissingVolumeCount); // no placeholders up to the English volume count
    }

    [Fact]
    public void ChapterMode_TheResolverPlacesNothing_AndHasNoData()
    {
        Assert.True(Degenerate(chaptersOnly: false).HasData);
        // The list stays on the input (the completion reads its last chapter) but places nothing.
        var map = Degenerate(chaptersOnly: true);
        Assert.False(map.HasData);
        Assert.Equal(2, map.Volumes.Count);
    }

    [Fact]
    public void Progress_InChapterMode_SaysChapters_NeverVolumesMissing()
    {
        var rows = IndexedWebtoon(311, without: 120);
        var facts = new ProgressFacts("en", MetadataOrigin.Korea, MetadataOriginStatus.Ongoing, OfficialPublisher: "Synthetic Press",
            OfficialVolumes: 12, Licensed: true, LatestChapter: 313);

        var r = SeriesProgress.Evaluate(rows, Degenerate(chaptersOnly: true), facts);
        Assert.Empty(r.MissingVolumes);
        Assert.Equal(0, r.VolumesBehind);
        Assert.Empty(r.UpgradeVolumes);
        Assert.Equal(311, r.Reach.ReachChapter);
        Assert.Empty(r.Reach.TouchedVolumes);
        Assert.Equal([120m], r.ChapterHoles);
        Assert.Equal(2, r.ChaptersBehind); // 312 and 313 are out in English
        var dto = SeriesProgress.ToDto(r);
        Assert.Equal(0, dto.MissingVolumes);
        Assert.Equal(3, dto.MissingChapters);
        Assert.Empty(dto.Reach!.VolumeFiles);

        // The same folder against the trusted degenerate list: the owner's "11 volumes missing".
        var trusted = SeriesProgress.Evaluate(rows, Degenerate(chaptersOnly: false), facts);
        Assert.Equal(11, trusted.MissingVolumes.Count);
    }
}
