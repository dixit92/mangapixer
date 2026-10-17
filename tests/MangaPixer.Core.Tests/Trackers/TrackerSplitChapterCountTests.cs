namespace com.lifepixer.mangapixer.Tests.Core.Trackers;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Trackers;
using Xunit;

/// <summary>
/// 1.44.0 (owner, 2026-10-17: "why does it say MyAnimeList is ahead?"): a folder whose newest chapters are SPLIT files (33.1 + 33.2 ...
/// 37.1 + 37.2, the whole files replaced) counted only up to its last whole file - a split part alone holds no whole chapter, and the
/// split rule (<c>MissingUnits.SplitsOf</c>: a chapter counts when every part here is marked) fed only the volume count. Chapters read now
/// take the split chapters too. Names are synthetic, in the owner's shape.
/// </summary>
public sealed class TrackerSplitChapterCountTests
{
    private static TrackerProgressRow Row(string name, bool read = true, bool held = false) =>
        new(new GroupingRow("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant()), read, held);

    /// <summary>Whole chapters 1-32, then chapters 33-37 as two parts each (.10 / .20), as in the owner's folder.</summary>
    private static List<TrackerProgressRow> OwnerShape(Func<string, bool>? read = null, Func<string, bool>? held = null)
    {
        var names = Enumerable.Range(1, 32).Select(c => $"Ch. {c:0000}.00.cbz")
            .Concat(Enumerable.Range(33, 5).SelectMany(c => new[] { $"Ch. {c:0000}.10.cbz", $"Ch. {c:0000}.20.cbz" }));
        return names.Select(n => Row(n, read?.Invoke(n) ?? true, held?.Invoke(n) ?? false)).ToList();
    }

    [Fact]
    public void AFolderEndingInSplitChapters_CountsTheLastChapterWhosePartsAreAllRead()
    {
        var result = TrackerProgress.Compute(OwnerShape(), null, false);
        Assert.Equal(37, result.Automatic.Chapters);
        Assert.Equal(37, result.All.Chapters);
    }

    [Fact]
    public void OnePartUnread_KeepsThatChapterOut()
    {
        var result = TrackerProgress.Compute(OwnerShape(read: n => n != "Ch. 0037.20.cbz"), null, false);
        Assert.Equal(36, result.Automatic.Chapters);
    }

    [Fact]
    public void HeldParts_CountOnlyInWhatSendWouldSend()
    {
        var result = TrackerProgress.Compute(OwnerShape(held: n => n.StartsWith("Ch. 0037.", StringComparison.Ordinal)), null, false);
        Assert.Equal(36, result.Automatic.Chapters);
        Assert.Equal(37, result.All.Chapters);
    }

    [Fact]
    public void AnExtraAfterTheLastWholeChapter_StillNeverCounts()
    {
        // 5.5 next to a whole 5 is an extra, not a part: the count stays 5.
        var rows = Enumerable.Range(1, 5).Select(c => Row($"Chapter {c:000}")).Append(Row("Chapter 005.5")).ToList();
        Assert.Equal(5, TrackerProgress.Compute(rows, null, false).Automatic.Chapters);
    }

    [Theory]
    [InlineData("Ch. 0037.10.cbz", 37, true)]  // a part of 37: read when 37 is
    [InlineData("Ch. 0037.20.cbz", 37, true)]
    [InlineData("Ch. 0037.10.cbz", 36, false)]
    [InlineData("Ch. 0036.20.cbz", 36, true)]
    [InlineData("Ch. 0032.00.cbz", 32, true)]
    public void ASplitPart_IsCoveredWhenItsWholeChapterIs(string name, int trackerChapters, bool expected)
    {
        var units = OwnerShape().Select(r => VolumeGrouping.UnitsOf(r.Row)).ToList();
        var parts = TrackerReadOn.SplitPartsOf(units);
        Assert.Equal(expected, TrackerReadOn.Covers(VolumeGrouping.UnitsOf(Row(name).Row), new TrackerNumbers(trackerChapters, null), null, false,
            splitParts: parts));
    }

    [Fact]
    public void AnExtra_IsNotAPart_AndStaysAboveItsChapter()
    {
        // 5.5 next to a whole 5 (no 5.1 ... 5.4) is an extra: MyAnimeList at 5 does not cover it.
        var units = Enumerable.Range(1, 5).Select(c => VolumeGrouping.UnitsOf(Row($"Chapter {c:000}").Row))
            .Append(VolumeGrouping.UnitsOf(Row("Chapter 005.5").Row)).ToList();
        var parts = TrackerReadOn.SplitPartsOf(units);
        Assert.Empty(parts);
        Assert.False(TrackerReadOn.Covers(units[^1], new TrackerNumbers(5, null), null, false, splitParts: parts));
    }
}
