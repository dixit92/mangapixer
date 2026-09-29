namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>
/// Unit tests for the keyset paging of the Volumes view (P2.4): the opaque <c>v:</c> cursor, forward and <c>before</c> pages
/// in both directions, entries that are never split, position ties between merged subfolders and cursors from another sort.
/// </summary>
public sealed class VolumePagingTests
{
    private static GroupingRow Archive(string name, string sortKey, string? id = null) =>
        new(id ?? "id:" + name, GroupingRowKind.Archive, name, sortKey);

    /// <summary>Ten volume entries: five stacks of two chapters (v01..v05) and five plain volume archives (v06..v10).</summary>
    private static IReadOnlyList<VolumeEntry> Entries()
    {
        var rows = new List<GroupingRow>();
        for (var v = 1; v <= 5; v++)
        {
            rows.Add(Archive($"S v{v:00} c{v * 2 - 1:000}", $"s v{v:00} c{v * 2 - 1:000}"));
            rows.Add(Archive($"S v{v:00} c{v * 2:000}", $"s v{v:00} c{v * 2:000}"));
        }
        for (var v = 6; v <= 10; v++)
            rows.Add(Archive($"S v{v:00}", $"s v{v:00}"));
        return VolumeGrouping.Group(rows, null).Entries;
    }

    private static string[] Ids(VolumePage page) => page.Entries.Select(e => e.Id).ToArray();

    [Fact]
    public void ForwardPages_CoverEveryEntryOnce_AscendingAndDescending()
    {
        var entries = Entries();
        foreach (var descending in new[] { false, true })
        {
            var expected = (descending ? entries.Reverse() : entries).Select(e => e.Id).ToList();
            var seen = new List<string>();
            string? cursor = null;
            for (var i = 0; i < 10; i++)
            {
                var page = VolumePaging.Page(entries, cursor, null, 3, descending);
                seen.AddRange(Ids(page));
                if (!page.HasMore)
                    break;
                cursor = page.NextCursor;
            }
            Assert.Equal(expected, seen);
        }
    }

    [Fact]
    public void ANextCursor_IsOpaqueAndPrefixed_AndRoundTrips()
    {
        var entries = Entries();
        var page = VolumePaging.Page(entries, null, null, 4, false);

        Assert.True(page.HasMore);
        Assert.StartsWith("v:", page.NextCursor);
        Assert.True(VolumePaging.IsVolumeCursor(page.NextCursor));
        var (rank, key, sort, id) = VolumePaging.Decode(page.NextCursor)!.Value;
        Assert.Equal((page.Entries[^1].Rank, page.Entries[^1].VolumeKey, page.Entries[^1].SortKey, page.Entries[^1].Id), (rank, key, sort, id));
        Assert.False(page.HasPrevious);
        Assert.Null(page.PrevCursor);
    }

    [Fact]
    public void AStackIsNeverSplitAcrossPages()
    {
        var entries = Entries();
        // Page size 1: each page is exactly one entry, so a two-chapter stack always arrives whole.
        var page = VolumePaging.Page(entries, null, null, 1, false);
        Assert.Equal(VolumeEntryKind.Stack, Assert.Single(page.Entries).Kind);
        Assert.Equal(2, page.Entries[0].Stack!.PresentCount);
        Assert.Equal(10, entries.Count);
    }

    [Fact]
    public void AMidListCursor_ReportsAPreviousPage_AndBeforeReturnsIt()
    {
        var entries = Entries();
        var first = VolumePaging.Page(entries, null, null, 4, false);
        var second = VolumePaging.Page(entries, first.NextCursor, null, 4, false);

        Assert.Equal(entries.Skip(4).Take(4).Select(e => e.Id), Ids(second));
        Assert.True(second.HasPrevious);
        Assert.NotNull(second.PrevCursor);

        var back = VolumePaging.Page(entries, null, second.PrevCursor, 4, false);
        Assert.Equal(entries.Take(4).Select(e => e.Id), Ids(back));
        Assert.False(back.HasPrevious);
        Assert.Null(back.PrevCursor);
        Assert.True(back.HasMore); // the window the client holds sits just after it
        Assert.NotNull(back.NextCursor);
    }

    [Fact]
    public void Before_ReturnsAShortPage_AndReportsFurtherPreviousPages()
    {
        var entries = Entries();
        var cursor = VolumePaging.Encode(entries[6]);

        var near = VolumePaging.Page(entries, null, cursor, 4, false);
        Assert.Equal(entries.Skip(2).Take(4).Select(e => e.Id), Ids(near));
        Assert.True(near.HasPrevious);
        Assert.Equal(VolumePaging.Encode(near.Entries[0]), near.PrevCursor);

        var all = VolumePaging.Page(entries, null, cursor, 50, false);
        Assert.Equal(6, all.Entries.Count);
        Assert.False(all.HasPrevious);
    }

    [Fact]
    public void BeforeAndForward_AreSymmetric_WhenDescending()
    {
        var entries = Entries();
        var forward = VolumePaging.Page(entries, null, null, 3, true);
        var next = VolumePaging.Page(entries, forward.NextCursor, null, 3, true);
        var back = VolumePaging.Page(entries, null, next.PrevCursor, 3, true);

        Assert.Equal(Ids(forward), Ids(back));
        Assert.Equal(entries.Reverse().Take(3).Select(e => e.Id), Ids(forward));
    }

    [Fact]
    public void ACursorFromAnotherSort_IsIgnored_AndThePageStartsAtTheTop()
    {
        var entries = Entries();
        foreach (var foreign in new[] { "s v03 c005", "a:12345", "r:20", "v:not-base64!!", "v:", "" })
        {
            var page = VolumePaging.Page(entries, foreign, null, 3, false);
            Assert.Equal(entries.Take(3).Select(e => e.Id), Ids(page));
            Assert.False(page.HasPrevious);
            var backward = VolumePaging.Page(entries, null, foreign, 3, false);
            Assert.Equal(entries.Take(3).Select(e => e.Id), Ids(backward));
        }
        Assert.Null(VolumePaging.Decode("v:e30")); // "{}" is not the tuple
        Assert.Null(VolumePaging.Decode(null));
        Assert.False(VolumePaging.IsVolumeCursor("s v01"));
    }

    [Fact]
    public void EntriesWithTheSameSortKey_KeepDistinctPositions_ThroughTheIdTiebreaker()
    {
        // Two loose chapters from different merged subfolders can share a SortKey ("012.cbz" in Volumes/ and in Chapters/).
        var rows = new List<GroupingRow> { Archive("012", "012.cbz", "id:b"), Archive("012", "012.cbz", "id:a"), Archive("013", "013.cbz", "id:c") };
        var entries = VolumeGrouping.Group(rows, null).Entries;

        Assert.Equal(["id:a", "id:b", "id:c"], entries.Select(e => e.Id));
        var first = VolumePaging.Page(entries, null, null, 1, false);
        var second = VolumePaging.Page(entries, first.NextCursor, null, 1, false);
        var third = VolumePaging.Page(entries, second.NextCursor, null, 1, false);
        Assert.Equal(["id:a", "id:b", "id:c"], Ids(first).Concat(Ids(second)).Concat(Ids(third)));
        Assert.False(third.HasMore);
    }

    [Fact]
    public void CursorsSurviveUnusualSortKeys()
    {
        var rows = new List<GroupingRow>
        {
            Archive("a:b", "a:b|éü:c", "id:1"), Archive("z", "z", "id:2"), Archive("y", "y/+=", "id:3"),
        };
        var entries = VolumeGrouping.Group(rows, null).Entries;

        var page = VolumePaging.Page(entries, null, null, 1, false);
        Assert.Equal(entries[0].Id, Ids(page)[0]);
        var next = VolumePaging.Page(entries, page.NextCursor, null, 5, false);
        Assert.Equal(entries.Skip(1).Select(e => e.Id), Ids(next));
    }

    [Fact]
    public void AnEmptyList_YieldsAnEmptyPage()
    {
        var page = VolumePaging.Page([], null, null, 5, false);

        Assert.Empty(page.Entries);
        Assert.False(page.HasMore);
        Assert.False(page.HasPrevious);
        Assert.Null(page.NextCursor);
    }
}
