namespace com.lifepixer.mangapixer.Tests.Core.Metadata;

using com.lifepixer.mangapixer.Core.Metadata;
using Xunit;

/// <summary>
/// Unit tests for the stored volume -> chapters map (1.29.0): canonical unit numbers, the JSON round trip, and the
/// builder rules (numbered volumes only, the none bucket, the same chapter under two volumes, noise clean-up,
/// fractional extras, the chapters-per-volume average). Synthetic numbers only.
/// </summary>
public sealed class VolumeMapTests
{
    private static VolumeListBuilder.RawVolume V(string volume, params string[] chapters) =>
        new(volume, chapters.Select(c => (c, 1)).ToList());

    [Theory]
    [InlineData("3", "3")]
    [InlineData("03", "3")]
    [InlineData("45.5", "45.5")]
    [InlineData("45.50", "45.5")]
    [InlineData("0", "0")]
    [InlineData("10.0", "10")]
    public void Canonical_StripsLeadingAndTrailingZeros(string text, string expected) =>
        Assert.Equal(expected, VolumeMapJson.Canonical(VolumeMapJson.Parse(text)!.Value));

    [Theory]
    [InlineData("none")]
    [InlineData("10a")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_RefusesNonNumbers(string? text) => Assert.Null(VolumeMapJson.Parse(text));

    [Fact]
    public void Json_RoundTrips_AndReadsMalformedAsEmpty()
    {
        var map = new[] { new VolumeMapEntry("1", ["1", "2", "2.5"]), new VolumeMapEntry("2", ["3"]) };
        var json = VolumeMapJson.Write(map);
        Assert.Equal("[{\"v\":\"1\",\"c\":[\"1\",\"2\",\"2.5\"]},{\"v\":\"2\",\"c\":[\"3\"]}]", json);
        var back = VolumeMapJson.Read(json);
        Assert.Equal(["1", "2"], back.Select(v => v.Volume));
        Assert.Equal(["1", "2", "2.5"], back[0].Chapters);
        Assert.Empty(VolumeMapJson.Read("{nope"));
        Assert.Empty(VolumeMapJson.Read(null));
        Assert.Equal(["7", "8"], VolumeMapJson.ReadChapters(VolumeMapJson.WriteChapters(["7", "8"])));
    }

    [Fact]
    public void Build_OrdersVolumesNumerically_AndChaptersAscending()
    {
        var result = VolumeListBuilder.Build([V("10", "19", "18"), V("2", "4", "3"), V("1", "2", "1")]);
        Assert.Equal(["1", "2", "10"], result.Volumes.Select(v => v.Volume));
        Assert.Equal(["18", "19"], result.Volumes[2].Chapters);
        Assert.Equal(10, result.HighestVolume);
        Assert.Empty(result.Unassigned);
    }

    [Fact]
    public void Build_TheNoneBucket_IsNotAVolume_ButItsOwnChaptersAreUnassigned()
    {
        var result = VolumeListBuilder.Build([V("none", "3", "4", "5"), V("1", "1", "2", "3")]);
        Assert.Equal(["1"], result.Volumes.Select(v => v.Volume));
        Assert.Equal(["1", "2", "3"], result.Volumes[0].Chapters); // a numbered volume wins over none
        Assert.Equal(["4", "5"], result.Unassigned);
    }

    [Fact]
    public void Build_AChapterUnderTwoVolumes_GoesToTheOneWithMoreUploads_ThenTheLowerVolume()
    {
        var more = VolumeListBuilder.Build([
            new("1", [("1", 1), ("2", 1), ("3", 1)]),
            new("2", [("3", 4), ("4", 1), ("5", 1)]),
        ]);
        Assert.Equal(["1", "2"], more.Volumes[0].Chapters);
        Assert.Equal(["3", "4", "5"], more.Volumes[1].Chapters);

        var tie = VolumeListBuilder.Build([V("1", "1", "2", "3"), V("2", "3", "4", "5")]);
        Assert.Equal(["1", "2", "3"], tie.Volumes[0].Chapters);
        Assert.Equal(["4", "5"], tie.Volumes[1].Chapters);
    }

    [Fact]
    public void Build_DropsAMisTaggedUpload_AndKeepsHealthyVolumes()
    {
        // Volume 1 lists a far-away chapter (a mis-tagged upload): it leaves volume 1 and becomes unassigned.
        var result = VolumeListBuilder.Build([V("1", "1", "2", "3", "4", "232"), V("2", "5", "6", "7", "8"), V("3", "9", "10", "11")]);
        Assert.Equal(["1", "2", "3", "4"], result.Volumes[0].Chapters);
        Assert.Equal(["232"], result.Unassigned);

        // One tiny noisy neighbour cannot empty a healthy volume.
        var noisy = VolumeListBuilder.Build([V("2", "5", "6", "7", "8", "9", "10"), V("3", "2")]);
        Assert.Equal(["5", "6", "7", "8", "9", "10"], noisy.Volumes[0].Chapters);
    }

    [Fact]
    public void Build_KeepsExtrasAndVolumeZero_AndSkipsUnnumberedChapters()
    {
        var result = VolumeListBuilder.Build([V("0", "0"), V("1", "1", "1.5", "2", "none", "2.1")]);
        Assert.Equal(["0", "1"], result.Volumes.Select(v => v.Volume));
        Assert.Equal(["1", "1.5", "2", "2.1"], result.Volumes[1].Chapters);
    }

    [Fact]
    public void ChaptersOf_ListsEveryNumberedChapter_OfEveryBucket_Canonical_Ascending()
    {
        var chapters = VolumeListBuilder.ChaptersOf([V("none", "12", "11.5", "none"), V("2", "05", "4.2"), V("1", "1", "4.1", "5")]);
        Assert.Equal(["1", "4.1", "4.2", "5", "11.5", "12"], chapters);
        Assert.Empty(VolumeListBuilder.ChaptersOf([]));
    }

    [Fact]
    public void ChaptersPerVolume_AveragesWholeChapters_OfVolumesOneAndUp()
    {
        var result = VolumeListBuilder.Build([V("0", "0"), V("1", "1", "2", "2.5"), V("2", "3", "4", "5", "6")]);
        Assert.Equal(3.0, result.ChaptersPerVolume); // (2 + 4) / 2; volume 0 and the extra do not count
        Assert.Null(VolumeListBuilder.Build([]).ChaptersPerVolume);
        Assert.Null(VolumeListBuilder.Build([]).HighestVolume);
    }
}
