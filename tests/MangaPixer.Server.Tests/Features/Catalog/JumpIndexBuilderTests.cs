using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

/// <summary>
/// Unit tests for <see cref="JumpIndexBuilder"/> (1.41.0 smart jump rail): the common prefix, number ranges with round boundaries,
/// letters on stripped names, folders vs archives, descending order - and, for every case, the contiguity invariant: each bucket's
/// cursor lands on its first row (by the browse keyset rule) and its rows are exactly the next <c>Count</c> rows.
/// </summary>
public sealed class JumpIndexBuilderTests
{
    private static List<JumpRow> Rows(params (CatalogNodeKind Kind, string Name)[] nodes) =>
        nodes.Select((n, i) => new JumpRow("n" + i, n.Kind, n.Name, SortKey.ForNode(n.Kind, n.Name)))
            .OrderBy(r => r.SortKey, StringComparer.Ordinal)
            .ToList();

    private static List<JumpRow> Archives(IEnumerable<string> names) =>
        Rows(names.Select(n => (CatalogNodeKind.Archive, n)).ToArray());

    /// <summary>
    /// The contiguity invariant, checked like the browse endpoint pages: in display order, a bucket's cursor (`> cursor` ascending,
    /// `< cursor` descending, null = the start) lands on its FirstNodeId, the buckets tile the whole level in order, and none is empty.
    /// </summary>
    private static void AssertContiguous(IReadOnlyList<JumpRow> rows, JumpRail rail, bool descending)
    {
        var display = descending ? rows.Reverse().ToList() : rows.ToList();
        var position = 0;
        foreach (var bucket in rail.Buckets)
        {
            Assert.True(bucket.Count > 0, $"empty bucket {bucket.Label}");
            var landed = bucket.FirstCursor is null
                ? 0
                : display.FindIndex(r => descending
                    ? string.CompareOrdinal(r.SortKey, bucket.FirstCursor) < 0
                    : string.CompareOrdinal(r.SortKey, bucket.FirstCursor) > 0);
            Assert.Equal(position, landed);
            Assert.Equal(display[position].Id, bucket.FirstNodeId);
            position += bucket.Count;
        }
        Assert.Equal(rows.Count, position);
    }

    private static JumpRail BuildBoth(IReadOnlyList<JumpRow> rows, string? container = null)
    {
        var asc = JumpIndexBuilder.Build(rows, descending: false, container);
        var desc = JumpIndexBuilder.Build(rows, descending: true, container);
        AssertContiguous(rows, asc, descending: false);
        AssertContiguous(rows, desc, descending: true);
        Assert.Equal(asc.Mode, desc.Mode);
        Assert.Equal(asc.Buckets.Select(b => (b.Label, b.Count, b.Range)).Reverse(), desc.Buckets.Select(b => (b.Label, b.Count, b.Range)));
        return asc;
    }

    [Fact]
    public void Empty_HasNoBuckets()
    {
        var rail = JumpIndexBuilder.Build([], descending: false);
        Assert.Equal(JumpIndexBuilder.Letters, rail.Mode);
        Assert.Empty(rail.Buckets);
    }

    [Theory]
    [InlineData("Ch. {0:0000}.cbz")]
    [InlineData("Vol. {0:000}.cbz")]
    [InlineData("{0:0000}.cbz")]
    [InlineData("Some Series - {0:000}.cbz")]
    [InlineData("[Group] Some Series v{0:000}.cbz")]
    [InlineData("Some Series - Chapter {0:000} - A Title.cbz")]
    public void NumberedNames_0To321_GetRanges0_50_To300(string format)
    {
        var rows = Archives(Enumerable.Range(0, 322).Select(n => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, n)));

        var rail = BuildBoth(rows);

        Assert.Equal(JumpIndexBuilder.Numbers, rail.Mode);
        Assert.Equal(["0", "50", "100", "150", "200", "250", "300"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal(["0-49", "50-99", "100-149", "150-199", "200-249", "250-299", "300-349"], rail.Buckets.Select(b => b.Range ?? "").ToArray());
        Assert.Equal([50, 50, 50, 50, 50, 50, 22], rail.Buckets.Select(b => b.Count).ToArray());
    }

    [Theory]
    [InlineData(33, 5, new[] { "0", "5", "10", "15", "20", "25", "30" })]
    [InlineData(103, 10, new[] { "0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "100" })]
    [InlineData(374, 50, new[] { "0", "50", "100", "150", "200", "250", "300", "350" })]
    [InlineData(12, 5, new[] { "0", "5", "10" })]
    [InlineData(60, 10, new[] { "0", "10", "20", "30", "40", "50", "60" })]
    public void StepIsRound_AndTheRailHasAtMostAChipPerFourRows(int chapters, int step, string[] labels)
    {
        // Census sizes of numeric folders (median 33, p90 103, max 374); chapters numbered from 1.
        var rows = Archives(Enumerable.Range(1, chapters).Select(n => $"Ch. {n:000}.cbz"));

        var rail = BuildBoth(rows);

        Assert.Equal(labels, rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal($"{step}-{2 * step - 1}", rail.Buckets[1].Range);
        Assert.True(rail.Buckets.Count <= Math.Min(12, Math.Max(2, chapters / 4)));
    }

    [Fact]
    public void StepOne_ShowsTheSingleNumberAsRange()
    {
        var rows = Archives(Enumerable.Range(1, 10).Select(n => $"{n:00}.cbz").Concat(Enumerable.Range(1, 10).Select(n => $"{n:00}.5.cbz")));

        var rail = BuildBoth(rows);

        // 20 rows -> at most 5 chips: 1..10 by 2 would be 6, by 5 -> 0, 5, 10.
        Assert.Equal(["0", "5", "10"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([8, 10, 2], rail.Buckets.Select(b => b.Count).ToArray());
    }

    [Fact]
    public void Decimals_FallInTheirWholeNumbersRange()
    {
        var names = Enumerable.Range(1, 40).Select(n => $"Ch. {n:000}.cbz").ToList();
        names.Add("Ch. 009.5.cbz");
        names.Add("Ch. 010.5.cbz");

        var rail = BuildBoth(Archives(names));

        // 42 rows -> at most 10 chips: step 5; 9.5 is in 5-9, 10.5 in 10-14.
        Assert.Equal(["0", "5", "10", "15", "20", "25", "30", "35", "40"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([4, 6, 6, 5, 5, 5, 5, 5, 1], rail.Buckets.Select(b => b.Count).ToArray());
    }

    [Fact]
    public void ChapterNumbers_WinOverVolumeNumbers_WhenNamesStateBoth()
    {
        var names = new List<string>();
        for (var c = 1; c <= 60; c++)
            names.Add($"Some Series v{(c - 1) / 10 + 1:00} c{c:000}.cbz");

        var rail = BuildBoth(Archives(names));

        Assert.Equal(JumpIndexBuilder.Numbers, rail.Mode);
        Assert.Equal(["0", "10", "20", "30", "40", "50", "60"], rail.Buckets.Select(b => b.Label).ToArray());
    }

    [Fact]
    public void VolumeNumbers_WhenNoNameStatesAChapter()
    {
        var rows = Archives(Enumerable.Range(1, 30).Select(v => $"Some Series v{v:00}.cbz"));

        var rail = BuildBoth(rows);

        Assert.Equal(["0", "5", "10", "15", "20", "25", "30"], rail.Buckets.Select(b => b.Label).ToArray());
    }

    [Fact]
    public void ExtrasAtEitherEnd_GetOwnChipsAtTheirPosition()
    {
        var names = Enumerable.Range(1, 50).Select(n => $"Some Series - Ch. {n:000}.cbz").ToList();
        names.Add("Some Series - Oneshot.cbz");
        names.Add("Some Series - Special.cbz");
        names.Add("Artbook.cbz");

        var rail = BuildBoth(Archives(names));

        Assert.Equal(JumpIndexBuilder.Numbers, rail.Mode);
        // Listing: Artbook, Ch. 001 .. 050, Oneshot, Special. 53 rows -> at most 12 chips: step 5.
        Assert.Equal(["Other", "0", "5", "10", "15", "20", "25", "30", "35", "40", "45", "50", "Other"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([1, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 1, 2], rail.Buckets.Select(b => b.Count).ToArray());
        Assert.Null(rail.Buckets[0].Range);
    }

    [Fact]
    public void AnUnnumberedRowInsideOneRange_StaysInThatRange()
    {
        var names = Enumerable.Range(1, 50).Select(n => $"Some Series - Ch. {n:000}.cbz").ToList();
        // Sorts between Ch. 012 and Ch. 013, but its number (2, from "#2") is out of order: it counts as unnumbered.
        names.Add("Some Series - Ch. 012 #2.cbz");

        var rail = BuildBoth(Archives(names));

        // 51 rows -> step 5; the row stays in 10-14 (both neighbours are there) instead of getting a chip inside that range.
        Assert.Equal(["0", "5", "10", "15", "20", "25", "30", "35", "40", "45", "50"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal(6, rail.Buckets[2].Count);
    }

    [Fact]
    public void AnOutOfOrderNumber_DoesNotBreakTheRanges()
    {
        // "100 Days" sorts first (a leading 100) and would put every later chapter "out of order" for a greedy check.
        var names = Enumerable.Range(1, 40).Select(n => $"Some Series {n:000}.cbz").ToList();
        names.Add("100 Days After.cbz");

        var rail = BuildBoth(Archives(names));

        Assert.Equal(JumpIndexBuilder.Numbers, rail.Mode);
        Assert.Equal(["Other", "0", "5", "10", "15", "20", "25", "30", "35", "40"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal(1, rail.Buckets[0].Count);
    }

    [Fact]
    public void MixedShapes_FallBackToLetters_NeverAChipInTheMiddleOfAnotherRange()
    {
        // Two numbering shapes whose numbers restart: chapter files then volume files (c... sorts before v...).
        var names = Enumerable.Range(1, 20).Select(n => $"Some Series c{n:000}.cbz")
            .Concat(Enumerable.Range(1, 20).Select(v => $"Some Series v{v:00}.cbz"));

        var rail = BuildBoth(Archives(names));

        Assert.Equal(JumpIndexBuilder.Letters, rail.Mode);
        Assert.Equal(["C", "V"], rail.Buckets.Select(b => b.Label).ToArray());
    }

    [Theory]
    [InlineData(3, "numbers")]
    [InlineData(4, "letters")]
    public void MoreThanThreeExtrasChips_FallBackToLetters(int extras, string mode)
    {
        // "010 #1" sorts just before "010" (between the ranges 5-9 and 10-14) and its number (1) is out of order: one extras chip each.
        var names = Enumerable.Range(1, 40).Select(n => $"Some Series - {n:000}.cbz").ToList();
        for (var k = 1; k <= extras; k++)
            names.Add($"Some Series - {k * 10:000} #1.cbz");

        var rail = BuildBoth(Archives(names));

        Assert.Equal(mode, rail.Mode);
        if (mode == JumpIndexBuilder.Numbers)
            Assert.Equal(extras, rail.Buckets.Count(b => b.Label == JumpIndexBuilder.OtherLabel));
    }

    [Fact]
    public void FewNumbers_StayLetters()
    {
        var rows = Archives(["Alpha 1.cbz", "Beta.cbz", "Gamma.cbz", "Delta 2.cbz", "Epsilon.cbz", "Zeta.cbz"]);

        var rail = BuildBoth(rows);

        Assert.Equal(JumpIndexBuilder.Letters, rail.Mode);
        Assert.Equal(["A", "B", "D", "E", "G", "Z"], rail.Buckets.Select(b => b.Label).ToArray());
    }

    [Fact]
    public void LettersIgnoreTheCommonPrefix()
    {
        var rows = Archives(["Some Series - Alpha.cbz", "Some Series - Beta.cbz", "Some Series - Bravo.cbz", "Some Series - Gamma.cbz"]);

        var rail = BuildBoth(rows);

        Assert.Equal(JumpIndexBuilder.Letters, rail.Mode);
        Assert.Equal(["A", "B", "G"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([1, 2, 1], rail.Buckets.Select(b => b.Count).ToArray());
        Assert.All(rail.Buckets, b => Assert.Null(b.Range));
    }

    [Fact]
    public void LettersWithAPrefixMostNamesShare_KeepTheOthersAtTheirPosition()
    {
        var rows = Archives([
            "Some Series - Alpha.cbz", "Some Series - Beta.cbz", "Some Series - Delta.cbz", "Some Series - Epsilon.cbz",
            "Some Series - Gamma.cbz", "Some Series - Zeta.cbz", "Artbook.cbz", "Zine.cbz",
        ]);

        var rail = BuildBoth(rows);

        // Listing: Artbook, Some Series - Alpha ... Zeta, Zine: the outsiders join the runs next to them.
        Assert.Equal(["A", "B", "D", "E", "G", "Z"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([2, 1, 1, 1, 1, 2], rail.Buckets.Select(b => b.Count).ToArray());
    }

    [Fact]
    public void RootLetters_HashThenAToZThenScripts_EachContiguous()
    {
        var rows = Rows(
            (CatalogNodeKind.Folder, "Zebra"), (CatalogNodeKind.Folder, "apple"), (CatalogNodeKind.Folder, "Avocado"),
            (CatalogNodeKind.Folder, "12 Monkeys"), (CatalogNodeKind.Folder, "[Bracketed]"), (CatalogNodeKind.Folder, "-Bonus"),
            (CatalogNodeKind.Folder, "あいうえお"), (CatalogNodeKind.Folder, "漫画"), (CatalogNodeKind.Folder, "Книга"),
            (CatalogNodeKind.Folder, "Banana"));

        var rail = BuildBoth(rows);

        Assert.Equal(JumpIndexBuilder.Letters, rail.Mode);
        Assert.Equal(["#", "A", "B", "Z", "Cyrillic", "Kana", "CJK"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal(3, rail.Buckets[0].Count); // -Bonus, 12 Monkeys, [Bracketed]: where they sort
        Assert.Null(rail.Buckets[0].FirstCursor);
    }

    [Fact]
    public void ALabelInTwoRuns_KeepsItsBiggestRun_AndTheOtherJoinsTheChipBeforeIt()
    {
        // Greek (U+03xx) and Greek Extended (U+1Fxx) are one label, but Cyrillic (U+04xx) sorts between them.
        var rows = Archives(["Alpha", "Βήτα", "Γάμμα", "Книга", "Ἀρχή"]);

        var rail = BuildBoth(rows);

        Assert.Equal(["A", "Greek", "Cyrillic"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([1, 2, 2], rail.Buckets.Select(b => b.Count).ToArray());
    }

    [Fact]
    public void MixedLevel_MinorityFoldersGetOneLeadingChip()
    {
        var nodes = new List<(CatalogNodeKind, string)> { (CatalogNodeKind.Folder, "Extras"), (CatalogNodeKind.Folder, "Specials") };
        nodes.AddRange(Enumerable.Range(1, 40).Select(n => (CatalogNodeKind.Archive, $"Ch. {n:000}.cbz")));
        var rows = Rows(nodes.ToArray());

        var rail = BuildBoth(rows);

        Assert.Equal(JumpIndexBuilder.Numbers, rail.Mode);
        Assert.Equal([JumpIndexBuilder.FoldersLabel, "0", "5", "10", "15", "20", "25", "30", "35", "40"], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal(2, rail.Buckets[0].Count);
        Assert.Null(rail.Buckets[0].FirstCursor);
        Assert.Equal(rows[1].SortKey, rail.Buckets[1].FirstCursor);
    }

    [Fact]
    public void MixedLevel_MinorityFilesGetOneTrailingChip()
    {
        var rows = Rows(
            (CatalogNodeKind.Folder, "Alpha"), (CatalogNodeKind.Folder, "Beta"), (CatalogNodeKind.Folder, "Gamma"),
            (CatalogNodeKind.Archive, "Alpha Loose.cbz"), (CatalogNodeKind.Archive, "Zulu Loose.cbz"));

        var rail = BuildBoth(rows);

        Assert.Equal(["A", "B", "G", JumpIndexBuilder.FilesLabel], rail.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal(2, rail.Buckets[^1].Count);
    }

    [Fact]
    public void Descending_LastBucketFirst_WithNullCursor_AndCursorsFromTheNextRow()
    {
        var rows = Archives(Enumerable.Range(1, 30).Select(n => $"Ch. {n:000}.cbz"));

        var desc = JumpIndexBuilder.Build(rows, descending: true);

        Assert.Equal(["30", "25", "20", "15", "10", "5", "0"], desc.Buckets.Select(b => b.Label).ToArray());
        Assert.Null(desc.Buckets[0].FirstCursor);
        Assert.Equal(rows[^1].Id, desc.Buckets[0].FirstNodeId);
        // "25-29": first row in descending order is Ch. 029; the row before it (descending) is Ch. 030.
        Assert.Equal(rows.Single(r => r.Name == "Ch. 030.cbz").SortKey, desc.Buckets[1].FirstCursor);
        Assert.Equal(rows.Single(r => r.Name == "Ch. 029.cbz").Id, desc.Buckets[1].FirstNodeId);
    }

    [Fact]
    public void AFolderTitleNumberInTheCommonPrefix_IsIgnored()
    {
        var rows = Archives(Enumerable.Range(1, 30).Select(n => $"Robot No. 9 - {n:000}.cbz"));

        var rail = BuildBoth(rows, container: "Robot No. 9");

        Assert.Equal(["0", "5", "10", "15", "20", "25", "30"], rail.Buckets.Select(b => b.Label).ToArray());
    }

    [Theory]
    [InlineData(new[] { "Ch. 0012", "Ch. 0013", "Ch. 0140" }, "Ch. ")]
    [InlineData(new[] { "Vol. 001", "Vol. 002" }, "Vol. ")]
    [InlineData(new[] { "Some Series - Alpha", "Some Series - Beta" }, "Some Series - ")]
    [InlineData(new[] { "[Group] Some Series v01", "[Group] Some Series v02", "[group] some series v03" }, "[Group] Some Series v")]
    [InlineData(new[] { "Alpha", "Alpine" }, "")]
    [InlineData(new[] { "Vol. 10.5", "Vol. 10.6" }, "Vol. ")]
    [InlineData(new[] { "Ch. 1", "Ch. 2", "Other" }, "Ch. ")]
    [InlineData(new[] { "Ch. 1", "Other", "Else" }, "")]
    [InlineData(new[] { "Single" }, "")]
    public void CommonPrefix_EndsOnATokenBoundary_AndTwoThirdsShareIt(string[] names, string expected)
    {
        Assert.Equal(expected, JumpIndexBuilder.CommonPrefix(names));
    }

    [Theory]
    [InlineData("Apple", "A")]
    [InlineData("zebra", "Z")]
    [InlineData("12 Monkeys", "#")]
    [InlineData("[Archive]", "#")]
    [InlineData("\"Quoted\"", "#")]
    [InlineData("!!!", "#")]
    [InlineData("{Curly}", "Other")]
    [InlineData("Élan", "Other")]
    [InlineData("", "Other")]
    [InlineData("あいうえお", "Kana")]
    [InlineData("カタカナ", "Kana")]
    [InlineData("한국어", "Hangul")]
    [InlineData("漫画", "CJK")]
    [InlineData("Книга", "Cyrillic")]
    [InlineData("Βιβλίο", "Greek")]
    [InlineData("كتاب", "Arabic")]
    [InlineData("ספר", "Hebrew")]
    [InlineData("หนังสือ", "Thai")]
    public void LetterLabel_IsTheFirstCharacterAsItSorts(string name, string expected)
    {
        Assert.Equal(expected, JumpIndexBuilder.LetterLabelFor(name));
    }

    [Fact]
    public void LongestNonDecreasing_KeepsTheMajorityOrder()
    {
        long?[] values = [100, 1, 2, null, 2, 3, 0, 4, 5];

        JumpIndexBuilder.KeepLongestNonDecreasing(values);

        Assert.Equal(new long?[] { null, 1, 2, null, 2, 3, null, 4, 5 }, values);
    }
}
