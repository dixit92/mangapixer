namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// 1.47.0 lane S: <see cref="AutoMatchText.UnitsOf(string?)"/> keeps its answers by name. The memo must give exactly the parse's answer -
/// the first time, on a repeat, from many threads at once, and with the series-head overload and the Volumes grouping built on it.
/// Synthetic lookalike names covering every grammar branch the parser has (volumes, chapters, ranges, extras, comics, running index,
/// bracket-only units, watermarks, full-width digits, years).
/// </summary>
public sealed class UnitsMemoTests
{
    public static readonly string[] Names =
    [
        "Series Title v03 c012.cbz", "Series Title c045.5.cbz", "Series Title Vol. 01-05.cbz", "001 [Chapter Title].cbz",
        "0003 [0001 - Chapter Title].cbz", "0002 [0000.5].cbz", "001 [2019].cbz", "Title (Vol. 3).cbz", "Title [v2].cbz",
        "Saga Annual 2.cbr", "Saga Annual #2.cbr", "Saga Special #1.cbz", "FCBD 2019.cbz", "Tome 3.cbz", "T03.cbz", "Band 3.cbz",
        "Deel 3.cbz", "Issue 12.cbz", "No. 12.cbz", "#12.cbz", "Title 001 Read Online.cbz", "Title 014.005.cbz", "Title 021.010.cbz",
        "Title 014.000.cbz", "Title Ch. 100-102.cbz", "Title Episode 3 Vol 2.cbz", "Title Ep 3.cbz", "Ｔｉｔｌｅ ｖ０２ ｃ００７.cbz",
        "Title Volume 10 Chapter 88.3.cbz", "Title 120 (2023) (Digital).cbz", "Title 07.cbz", "Title.cbz", "Oneshot.cbz",
        "Title c001-003.cbz", "Title v01-v03.cbz", "(C99) [Circle (Artist)] Title (Series).cbz", "Title 2019 Annual.cbz",
        "Title One-Shot 1.cbz", "Title chapters 1-10.zip", "Title vol 2 extra.cbz", "  ", "Title c1.5 (part 2).cbz",
    ];

    [Fact]
    public void UnitsOf_GivesTheParse_FirstTime_OnRepeat_AndForNullOrBlank()
    {
        foreach (var name in Names)
        {
            var parsed = AutoMatchText.ParseUnits(name);
            Assert.Equal(parsed, AutoMatchText.UnitsOf(name));
            Assert.Equal(parsed, AutoMatchText.UnitsOf(name)); // memo hit
            Assert.Equal(parsed, AutoMatchText.UnitsOf(new string(name.AsSpan()))); // an equal string that is another instance
        }
        Assert.Equal(default, AutoMatchText.UnitsOf(null));
        Assert.Equal(default, AutoMatchText.ParseUnits(null));
        Assert.Equal(default, AutoMatchText.UnitsOf(" "));
    }

    [Fact]
    public void UnitsOf_IsTheParse_FromManyThreadsAtOnce()
    {
        var expected = Names.ToDictionary(n => n, AutoMatchText.ParseUnits);
        Parallel.For(0, 2_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var name = Names[i % Names.Length] + (i % 3 == 0 ? string.Empty : " "); // trailing space: a different key, the same parse
            Assert.Equal(expected[Names[i % Names.Length]], AutoMatchText.UnitsOf(name));
        });
    }

    [Fact]
    public void UnitsOf_StaysExact_PastTheCapacity()
    {
        // More distinct names than the memo holds: it starts over and every answer is still the parse.
        for (var i = 0; i < AutoMatchText.UnitsMemoCapacity + 500; i++)
        {
            var name = $"Series {i % 97} v{i % 40:00} c{i:00000}.cbz";
            if (i % 997 == 0)
                Assert.Equal(AutoMatchText.ParseUnits(name), AutoMatchText.UnitsOf(name));
            else
                AutoMatchText.UnitsOf(name);
        }
        foreach (var name in Names)
            Assert.Equal(AutoMatchText.ParseUnits(name), AutoMatchText.UnitsOf(name));
    }

    [Fact]
    public void SeriesHeadOverload_AndVolumesGrouping_AreStable_OnMemoHits()
    {
        const string head = "Series Title";
        var rows = Names.Where(n => !string.IsNullOrWhiteSpace(n))
            .Select((n, i) => new GroupingRow($"p{i}", GroupingRowKind.Archive, n, $"k{i:000}", null, null, null))
            .ToList();
        var first = rows.Select(VolumeGrouping.UnitsOf).ToList();
        var headed = Names.Select(n => AutoMatchText.UnitsOf(n, head)).ToList();
        // Warm: every name is in the memo now.
        Assert.Equal(first, rows.Select(VolumeGrouping.UnitsOf).ToList());
        Assert.Equal(headed, Names.Select(n => AutoMatchText.UnitsOf(n, head)).ToList());
        Assert.Equal(VolumeGrouping.WithSeriesHeads(rows), VolumeGrouping.WithSeriesHeads(rows));
    }
}
