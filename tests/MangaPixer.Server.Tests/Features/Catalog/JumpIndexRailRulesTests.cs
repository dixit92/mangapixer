using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

/// <summary>
/// 1.49.0 lane J - pure builder rules on synthetic names: doujin (bracketed) levels label by the letter inside the leading tag (J2, owner
/// decision 11), a mixed level's smaller kind gets its own chips from 20 rows (J5, decision 14), and the date rail's chips and boundaries (J4).
/// Every case also checks the contiguity invariant the rail rests on.
/// </summary>
public sealed class JumpIndexRailRulesTests
{
    private static List<JumpRow> Rows(params (CatalogNodeKind Kind, string Name)[] nodes) =>
        nodes.Select((n, i) => new JumpRow("n" + i, n.Kind, n.Name, SortKey.ForNode(n.Kind, n.Name)))
            .OrderBy(r => r.SortKey, StringComparer.Ordinal)
            .ToList();

    /// <summary>A letters-only suffix (digits would read as chapter / volume numbers and turn the rail numeric).</summary>
    private static string Tag(int i) => $"{(char)('a' + (i / 26))}{(char)('a' + (i % 26))}";

    private static List<JumpRow> Archives(IEnumerable<string> names) => Rows(names.Select(n => (CatalogNodeKind.Archive, n)).ToArray());

    /// <summary>Each bucket's cursor (browse's keyset rule, ascending) lands on its first row and the buckets tile the level.</summary>
    private static void AssertContiguous(IReadOnlyList<JumpRow> rows, JumpRail rail)
    {
        var position = 0;
        foreach (var bucket in rail.Buckets)
        {
            var landed = bucket.FirstCursor is null ? 0 : rows.ToList().FindIndex(r => string.CompareOrdinal(r.SortKey, bucket.FirstCursor) > 0);
            Assert.Equal(position, landed);
            Assert.Equal(rows[position].Id, bucket.FirstNodeId);
            position += bucket.Count;
        }
        Assert.Equal(rows.Count, position);
    }

    // ---- J2: bracketed levels ----------------------------------------------------------------------------------------------------

    [Fact]
    public void DoujinLevel_LabelsByTheFirstLetterInsideTheLeadingTag()
    {
        // Mixed leaders, so no shared prefix: 24 circle-led names with six distinct circle letters, plus a few plain ones.
        var names = new List<string>();
        foreach (var circle in new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot" })
            for (var i = 1; i <= 4; i++)
                names.Add(i % 2 == 0 ? $"[{circle}] Synthetic Title {Tag(i)}" : $"({circle}) Synthetic Title {Tag(i)}");
        names.Add("Zulu Plain One");
        names.Add("Zulu Plain Two");
        var rows = Archives(names);

        var rail = JumpIndexBuilder.Build(rows, descending: false);

        Assert.Equal(JumpIndexBuilder.Letters, rail.Mode);
        // `(` sorts before `[`: the parenthesised group first, then the bracketed one, then the letters.
        Assert.Equal(["(A", "(B", "(C", "(D", "(E", "(F", "[A", "[B", "[C", "[D", "[E", "[F", "Z"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal(2, rail.Buckets[0].Count);
        AssertContiguous(rows, rail);
    }

    [Fact]
    public void DoujinLevel_EveryNameOpensWithATag_BracketChipsNameTheCircleLetters()
    {
        // The common case: "[Circle] Title" names. The shared prefix is only the bracket, so it is kept for the labels.
        var circles = new[] { "Alder", "Birch", "Cedar", "Dogwood", "Elm", "Fir" };
        var rows = Archives(Enumerable.Range(0, 24).Select(i => $"[{circles[i % 6]} Circle] Synthetic Tale {Tag(i)}"));

        var rail = JumpIndexBuilder.Build(rows, descending: false);

        Assert.Equal(JumpIndexBuilder.Letters, rail.Mode);
        Assert.Equal(["[A", "[B", "[C", "[D", "[E", "[F"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.All(rail.Buckets, b => Assert.Equal(4, b.Count));
        AssertContiguous(rows, rail);
        // Descending reverses the chips and keeps each one a contiguous run.
        var desc = JumpIndexBuilder.Build(rows, descending: true);
        Assert.Equal(["[F", "[E", "[D", "[C", "[B", "[A"], desc.Buckets.Select(b => b.Label).ToArray());
        Assert.Null(desc.Buckets[0].FirstCursor);
    }

    [Fact]
    public void DoujinLevel_ASharedEventTagIsCutAndTheCircleLettersCarryTheRail()
    {
        // All names open with the same event tag and a circle bracket: the shared prefix (through the bracket) is cut as before, so
        // the chips are the circles' plain letters. (The bracket labels cover the levels whose prefix does not reach the bracket.)
        var circles = new[] { "Able", "Baker", "Cast", "Dover", "Easy", "Fox" };
        var rows = Archives(Enumerable.Range(0, 30).Select(i => $"(Synthetic Event) [{circles[i % 6]} {Tag(i)}] Title"));

        var rail = JumpIndexBuilder.Build(rows, descending: false);

        Assert.Equal(["A", "B", "C", "D", "E", "F"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.All(rail.Buckets, b => Assert.Equal(5, b.Count));
        AssertContiguous(rows, rail);
    }

    [Theory]
    [InlineData("[Circle] Title", "[C")]
    [InlineData("(Event) Title", "(E")]
    [InlineData("[circle (Artist)] Title", "[C")]
    [InlineData("[ Spaced] Title", "[S")]
    [InlineData("[2nd Circle] Title", "[#")]
    [InlineData("[(Nested)] Title", "[#")]
    [InlineData("[] Empty", "[#")]
    [InlineData("[", "[#")]
    [InlineData("[あ] Kana", "[Kana")]
    [InlineData("[Élan] Accented", "[Other")]
    [InlineData("Plain Name", "P")]
    public void TagLetterLabel_IsTheBracketPlusTheLetterInsideTheTag(string name, string expected)
    {
        Assert.Equal(expected, JumpIndexBuilder.TagLetterLabelFor(name));
    }

    [Fact]
    public void FewBracketNames_KeepThePlainLabels()
    {
        // Under two thirds bracketed: the letters rule stays as it was (`#` for the bracketed ones).
        var rows = Archives(["[One] A", "[Two] B", "Alpha", "Bravo", "Charlie", "Delta", "Echo"]);

        var rail = JumpIndexBuilder.Build(rows, descending: false);

        Assert.Equal(["#", "A", "B", "C", "D", "E"], rail.Buckets.Select(b => b.Label).ToArray());
        AssertContiguous(rows, rail);
    }

    // ---- J5: the smaller kind at a mixed level -------------------------------------------------------------------------------------

    private static List<(CatalogNodeKind, string)> Named(CatalogNodeKind kind, int count, string stem) =>
        Enumerable.Range(0, count).Select(i => (kind, $"{(char)('A' + (i % 6))}{stem} {Tag(i)}")).ToList();

    [Fact]
    public void MixedLevel_TheSmallerKindGetsItsOwnLettersFromTwentyRows()
    {
        var nodes = Named(CatalogNodeKind.Folder, 24, "folder");
        nodes.AddRange(Named(CatalogNodeKind.Archive, 36, "file"));
        var rows = Rows(nodes.ToArray());

        var rail = JumpIndexBuilder.Build(rows, descending: false);

        Assert.Equal(JumpIndexBuilder.Letters, rail.Mode);
        Assert.Equal(["A", "B", "C", "D", "E", "F", "A", "B", "C", "D", "E", "F"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal(Enumerable.Repeat(JumpIndexBuilder.FoldersGroup, 6).Concat(Enumerable.Repeat(JumpIndexBuilder.FilesGroup, 6)),
            rail.Buckets.Select(b => b.Group ?? string.Empty));
        Assert.DoesNotContain(rail.Buckets, b => b.Label is JumpIndexBuilder.FoldersLabel or JumpIndexBuilder.FilesLabel);
        AssertContiguous(rows, rail);
    }

    [Fact]
    public void MixedLevel_ExactlyTwentyRowsIsEnough_NineteenKeepsTheSingleChip()
    {
        foreach (var (minor, ranked) in new[] { (19, false), (20, true) })
        {
            var nodes = Named(CatalogNodeKind.Folder, minor, "folder");
            nodes.AddRange(Named(CatalogNodeKind.Archive, 40, "file"));
            var rows = Rows(nodes.ToArray());

            var rail = JumpIndexBuilder.Build(rows, descending: false);

            Assert.Equal(ranked, rail.Buckets.All(b => b.Label != JumpIndexBuilder.FoldersLabel));
            Assert.Equal(!ranked, rail.Buckets.Any(b => b.Label == JumpIndexBuilder.FoldersLabel));
            AssertContiguous(rows, rail);
        }
    }

    [Fact]
    public void MixedLevel_NoGroupsWhenOneKindOnlyOrTheMinorIsSmall()
    {
        var rows = Rows(Named(CatalogNodeKind.Archive, 30, "file").ToArray());
        Assert.All(JumpIndexBuilder.Build(rows, descending: false).Buckets, b => Assert.Null(b.Group));

        var mixed = Named(CatalogNodeKind.Folder, 3, "folder");
        mixed.AddRange(Named(CatalogNodeKind.Archive, 30, "file"));
        Assert.All(JumpIndexBuilder.Build(Rows(mixed.ToArray()), descending: false).Buckets, b => Assert.Null(b.Group));
    }

    [Fact]
    public void MixedLevel_Descending_ReversesBothSegmentsAsListed()
    {
        var nodes = Named(CatalogNodeKind.Folder, 24, "folder");
        nodes.AddRange(Named(CatalogNodeKind.Archive, 24, "file"));
        var rows = Rows(nodes.ToArray());

        var asc = JumpIndexBuilder.Build(rows, descending: false);
        var desc = JumpIndexBuilder.Build(rows, descending: true);

        Assert.Equal(asc.Buckets.Select(b => (b.Label, b.Count, b.Group)).Reverse(), desc.Buckets.Select(b => (b.Label, b.Count, b.Group)));
        Assert.Null(desc.Buckets[0].FirstCursor);
    }

    // ---- J4: date chips ------------------------------------------------------------------------------------------------------------

    // Wednesday 2026-10-28 12:00 UTC: Monday of its week is 2026-10-26, the month starts 2026-10-01.
    private static readonly DateTimeOffset s_now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static JumpRow DatedRow(int id, CatalogNodeKind kind, string name, DateTimeOffset? created, DateTimeOffset? latest = null) =>
        new("n" + id, kind, name, SortKey.ForNode(kind, name), 100 + id, created, latest);

    private static List<JumpRow> NewestFirstArchives(params string[] timestamps) =>
        timestamps.Select((t, i) => DatedRow(i, CatalogNodeKind.Archive, $"Row {Tag(i)}", DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture))).ToList();

    [Fact]
    public void Dates_ChipsAreTodayWeekMonthYearThenYears_WithBrowseCursors()
    {
        var rows = NewestFirstArchives(
            "2026-10-28T09:00:00Z", "2026-10-28T01:00:00Z",   // today
            "2026-10-27T10:00:00Z", "2026-10-26T00:30:00Z",   // this week (Monday 26th)
            "2026-10-20T10:00:00Z", "2026-10-02T10:00:00Z",   // this month
            "2026-03-01T10:00:00Z", "2026-01-01T00:00:00Z",   // this year
            "2025-12-31T23:00:00Z", "2025-02-02T10:00:00Z",   // 2025
            "2023-05-05T10:00:00Z");                          // 2023

        var rail = JumpIndexBuilder.BuildByDate(rows, JumpIndexBuilder.RecentlyAddedSort, s_now, TimeSpan.Zero);

        Assert.Equal(JumpIndexBuilder.Dates, rail.Mode);
        Assert.Equal(["Today", "This week", "This month", "This year", "2025", "2023"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([2, 2, 2, 2, 2, 1], rail.Buckets.Select(b => b.Count).ToArray());
        Assert.Null(rail.Buckets[0].FirstCursor);
        Assert.Equal(rows[0].Id, rail.Buckets[0].FirstNodeId);
        // The cursor of a chip is browse's recentlyAdded cursor of the row just before its first row.
        Assert.Equal($"a:1:{rows[1].CreatedAt!.Value.UtcDateTime.ToBinary()}:{rows[1].InternalId}", rail.Buckets[1].FirstCursor);
        Assert.Equal(rows[2].Id, rail.Buckets[1].FirstNodeId);
        Assert.All(rail.Buckets, b => Assert.Null(b.Group));
    }

    [Fact]
    public void Dates_TheViewersDayMovesTheBoundaries()
    {
        // 23:30 UTC on the 27th is already the 28th at UTC+2.
        var rows = NewestFirstArchives("2026-10-27T23:30:00Z", "2026-10-27T10:00:00Z", "2026-10-26T10:00:00Z");

        var utc = JumpIndexBuilder.BuildByDate(rows, JumpIndexBuilder.RecentlyAddedSort, s_now, TimeSpan.Zero);
        var east = JumpIndexBuilder.BuildByDate(rows, JumpIndexBuilder.RecentlyAddedSort, s_now, TimeSpan.FromHours(2));

        Assert.Empty(utc.Buckets); // all three rows are "This week": one period, no rail
        Assert.Equal(["Today", "This week"], east.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([1, 2], east.Buckets.Select(b => b.Count).ToArray());
    }

    [Fact]
    public void Dates_NoRailWhenEveryRowFallsInOneChip()
    {
        var rows = NewestFirstArchives("2026-10-28T09:00:00Z", "2026-10-28T08:00:00Z", "2026-10-28T07:00:00Z");

        Assert.Empty(JumpIndexBuilder.BuildByDate(rows, JumpIndexBuilder.RecentlyAddedSort, s_now, TimeSpan.Zero).Buckets);
        Assert.Empty(JumpIndexBuilder.BuildByDate([], JumpIndexBuilder.RecentlyAddedSort, s_now, TimeSpan.Zero).Buckets);
        Assert.Empty(JumpIndexBuilder.BuildByDate(rows, "name", s_now, TimeSpan.Zero).Buckets);
    }

    [Fact]
    public void Dates_WeekAcrossAMonthBoundary_StaysMonotonic()
    {
        // Now = Friday 2026-10-02: its week began Monday 2026-09-28 (last month), so "This week" holds days of September.
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var rows = NewestFirstArchives("2026-10-02T01:00:00Z", "2026-10-01T10:00:00Z", "2026-09-29T10:00:00Z", "2026-09-20T10:00:00Z", "2026-02-01T10:00:00Z");

        var rail = JumpIndexBuilder.BuildByDate(rows, JumpIndexBuilder.RecentlyUpdatedSort, now, TimeSpan.Zero);

        Assert.Equal(["Today", "This week", "This year"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([1, 2, 2], rail.Buckets.Select(b => b.Count).ToArray());
    }

    [Fact]
    public void Dates_RecentlyAdded_BothKindsEachGetTheirOwnChipsWithGroups()
    {
        var rows = new List<JumpRow>
        {
            DatedRow(0, CatalogNodeKind.Folder, "F1", DateTimeOffset.Parse("2026-10-28T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            DatedRow(1, CatalogNodeKind.Folder, "F2", DateTimeOffset.Parse("2025-10-28T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            DatedRow(2, CatalogNodeKind.Archive, "A1", DateTimeOffset.Parse("2026-10-28T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            DatedRow(3, CatalogNodeKind.Archive, "A2", DateTimeOffset.Parse("2026-10-27T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
        };

        var rail = JumpIndexBuilder.BuildByDate(rows, JumpIndexBuilder.RecentlyAddedSort, s_now, TimeSpan.Zero);

        Assert.Equal(["Today", "2025", "Today", "This week"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal(["folders", "folders", "files", "files"], rail.Buckets.Select(b => b.Group ?? string.Empty).ToArray());
        // The files' first chip starts after the last folder: browse's cursor of that folder (kind 0).
        Assert.Equal($"a:0:{rows[1].CreatedAt!.Value.UtcDateTime.ToBinary()}:{rows[1].InternalId}", rail.Buckets[2].FirstCursor);
    }

    [Fact]
    public void Dates_RecentlyAddedWithBothKindsOfTheSamePeriod_HasNoRail()
    {
        var at = DateTimeOffset.Parse("2026-10-28T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var rows = new List<JumpRow> { DatedRow(0, CatalogNodeKind.Folder, "F1", at), DatedRow(1, CatalogNodeKind.Archive, "A1", at) };

        Assert.Empty(JumpIndexBuilder.BuildByDate(rows, JumpIndexBuilder.RecentlyAddedSort, s_now, TimeSpan.Zero).Buckets);
    }

    [Fact]
    public void Dates_RecentlyUpdated_InterleavesAndPutsFoldersWithoutADateLast()
    {
        var rows = new List<JumpRow>
        {
            DatedRow(0, CatalogNodeKind.Folder, "Series", DateTimeOffset.Parse("2020-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                latest: DateTimeOffset.Parse("2026-10-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            DatedRow(1, CatalogNodeKind.Archive, "File", DateTimeOffset.Parse("2026-10-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            DatedRow(2, CatalogNodeKind.Folder, "Old", DateTimeOffset.Parse("2020-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                latest: DateTimeOffset.Parse("2024-04-04T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            DatedRow(3, CatalogNodeKind.Folder, "Empty", DateTimeOffset.Parse("2020-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
        };

        var rail = JumpIndexBuilder.BuildByDate(rows, JumpIndexBuilder.RecentlyUpdatedSort, s_now, TimeSpan.Zero);

        Assert.Equal(["Today", "This month", "2024", "Other"], rail.Buckets.Select(b => b.Label).ToArray());
        // u:{hasValue}:{effectiveBinary}:{id}:{sortKey} of the row before the chip; the empty folder's cursor is the null form.
        Assert.Equal($"u:1:{rows[0].LatestDescendantAddedAt!.Value.UtcDateTime.ToBinary()}:{rows[0].InternalId}:{rows[0].SortKey}", rail.Buckets[1].FirstCursor);
        Assert.Equal($"u:1:{rows[2].LatestDescendantAddedAt!.Value.UtcDateTime.ToBinary()}:{rows[2].InternalId}:{rows[2].SortKey}", rail.Buckets[3].FirstCursor);
        Assert.All(rail.Buckets, b => Assert.Null(b.Group));
    }
}
