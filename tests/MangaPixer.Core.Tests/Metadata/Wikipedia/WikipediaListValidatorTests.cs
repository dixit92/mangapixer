namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Wikipedia;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Wikipedia;
using Xunit;

/// <summary>Validation, the table choice and the merge precedence of Wikipedia lists (1.32.0): synthetic numbers only.</summary>
public sealed class WikipediaListValidatorTests
{
    private static WikipediaVolumeRow Row(string volume, params string[] chapters) => new(volume, chapters, null, null);

    private static WikipediaTable Table(string? heading, int implicitChapters, params WikipediaVolumeRow[] rows) =>
        new(heading, rows, [], implicitChapters, 0);

    private static WikipediaTable Series(int volumes, int perVolume = 4, string? heading = null)
    {
        var rows = new List<WikipediaVolumeRow>();
        var next = 1;
        for (var v = 1; v <= volumes; v++)
        {
            rows.Add(Row(v.ToString(), Enumerable.Range(next, perVolume).Select(c => c.ToString()).ToArray()));
            next += perVolume;
        }
        return Table(heading, 0, rows.ToArray());
    }

    private static IReadOnlyList<VolumeMapEntry> AsMap(WikipediaTable t) => t.Volumes.Select(v => new VolumeMapEntry(v.Volume, v.Chapters)).ToList();

    [Fact]
    public void AGoodList_IsAccepted_WithNoReference() =>
        Assert.True(WikipediaListValidator.Validate(Series(10), WikipediaReference.None).Accepted);

    [Fact]
    public void AnEmptyList_IsRejected() =>
        Assert.Equal(WikipediaValidation.NoVolumes, WikipediaListValidator.Validate(Table(null, 0, Row("1")), WikipediaReference.None).Code);

    [Fact]
    public void MoreVolumesThanMangaUpdatesPlusOne_IsRejected()
    {
        var reference = new WikipediaReference([], 10);
        Assert.True(WikipediaListValidator.Validate(Series(11), reference).Accepted);
        Assert.Equal(WikipediaValidation.TooManyVolumes, WikipediaListValidator.Validate(Series(12), reference).Code);
    }

    [Fact]
    public void AShuffledList_IsRejected_AStrayRowIsTolerated()
    {
        var shuffled = Table(null, 0, Row("1", "50", "51"), Row("2", "1", "2"), Row("3", "60", "61"), Row("4", "3", "4"), Row("5", "70"));
        Assert.Equal(WikipediaValidation.NotMonotonic, WikipediaListValidator.Validate(shuffled, WikipediaReference.None).Code);

        var rows = Series(10).Volumes.ToList();
        rows[4] = Row("5", "1", "2"); // one stray row among ten
        Assert.True(WikipediaListValidator.Validate(Table(null, 0, rows.ToArray()), WikipediaReference.None).Accepted);
    }

    [Fact]
    public void AgreementWithMangaDex_IsRequired_OnSharedChapters()
    {
        var wiki = Series(10);
        var agreeing = new WikipediaReference(AsMap(Series(8)), null);
        var verdict = WikipediaListValidator.Validate(wiki, agreeing);
        Assert.True(verdict.Accepted);
        Assert.Equal(32, verdict.SharedChapters);

        // MangaDex puts every chapter one volume later: 0% agreement.
        var shifted = new WikipediaReference(Series(8).Volumes.Select(v => new VolumeMapEntry((int.Parse(v.Volume) + 1).ToString(), v.Chapters)).ToList(), null);
        Assert.Equal(WikipediaValidation.DisagreesWithMangaDex, WikipediaListValidator.Validate(wiki, shifted).Code);
    }

    [Fact]
    public void NinetyFivePercentAgreement_IsTheBar()
    {
        var wiki = Series(25); // 100 chapters, 4 per volume

        // MangaDex puts the LAST chapter of the first n volumes in the next volume instead.
        static WikipediaReference MovedLast(int n)
        {
            var volumes = Series(25).Volumes.Select(v => (v.Volume, Chapters: v.Chapters.ToList())).ToList();
            for (var i = 0; i < n; i++)
            {
                var moved = volumes[i].Chapters[^1];
                volumes[i].Chapters.RemoveAt(volumes[i].Chapters.Count - 1);
                volumes[i + 1].Chapters.Insert(0, moved);
            }
            return new WikipediaReference(volumes.Select(v => new VolumeMapEntry(v.Volume, v.Chapters)).ToList(), null);
        }

        var five = WikipediaListValidator.Validate(wiki, MovedLast(5));
        Assert.True(five.Accepted);
        Assert.Equal((100, 95), (five.SharedChapters, five.AgreeingChapters));

        var six = WikipediaListValidator.Validate(wiki, MovedLast(6));
        Assert.Equal(WikipediaValidation.DisagreesWithMangaDex, six.Code);
    }

    [Fact]
    public void CountedOnChapters_NeedAMangaDexListToBeTrusted()
    {
        var guessed = Table(null, 6, Row("1", "1", "2", "3"), Row("2", "4", "5", "6"));
        Assert.Equal(WikipediaValidation.UnverifiedImplicit, WikipediaListValidator.Validate(guessed, WikipediaReference.None).Code);

        var withReference = new WikipediaReference([new VolumeMapEntry("1", ["1", "2", "3"])], null);
        Assert.True(WikipediaListValidator.Validate(guessed, withReference).Accepted);
    }

    [Fact]
    public void Pick_ChoosesTheTableThatAgreesWithMangaDex_NotTheLargest()
    {
        var spinOff = Table("Tokyo Ghoul:re", 0, Enumerable.Range(1, 16).Select(v => Row(v.ToString(), (1000 + v).ToString())).ToArray());
        var main = Series(14, 3, "Tokyo Ghoul");
        var reference = new WikipediaReference(AsMap(Series(12, 3)), 14);
        var picked = WikipediaListValidator.Pick([spinOff, main], reference, out var verdict);
        Assert.Same(main, picked);
        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void Pick_PrefersAnUnrelatedHeading_OverASpinOff_WhenNothingElseDecides()
    {
        var spin = Series(5, 3, "Spin-off novels");
        var main = Series(5, 3, "Volumes");
        Assert.Same(main, WikipediaListValidator.Pick([spin, main], WikipediaReference.None, out _));
    }

    [Fact]
    public void Pick_ReturnsNull_WhenNoTableIsAcceptable()
    {
        var picked = WikipediaListValidator.Pick([Series(30)], new WikipediaReference([], 10), out var verdict);
        Assert.Null(picked);
        Assert.Equal(WikipediaValidation.TooManyVolumes, verdict.Code);
    }

    // --- Merge precedence -------------------------------------------------------------------------------------------------

    private static string Flat(IReadOnlyList<VolumeMapEntry> v) => string.Join(";", v.Select(e => e.Volume + ":" + string.Join(",", e.Chapters)));

    [Fact]
    public void Merge_MangaDexWinsWhereItPlaces_WikipediaFillsTheRest()
    {
        var mangaDex = new[] { new VolumeMapEntry("1", ["1", "2"]), new VolumeMapEntry("2", ["3", "4"]) };
        var wikipedia = new[]
        {
            new VolumeMapEntry("1", ["1", "2"]),
            new VolumeMapEntry("2", ["3"]),
            new VolumeMapEntry("3", ["4", "5", "6"]), // 4 is MangaDex's (volume 2): MangaDex wins; 5 and 6 are new
        };
        var merged = VolumeListMerge.Merge(mangaDex, ["5", "6", "9"], wikipedia);
        Assert.Equal("1:1,2;2:3,4;3:5,6", Flat(merged.Volumes));
        Assert.Equal(["9"], merged.Unassigned);
        Assert.Equal(2, merged.ChaptersFromWikipedia);
        Assert.Equal(1, merged.VolumesFromWikipedia);
        Assert.True(merged.WikipediaFilled);
    }

    [Fact]
    public void Merge_WithNoMangaDexList_UsesTheWikipediaList()
    {
        var merged = VolumeListMerge.Merge([], [], [new VolumeMapEntry("1", ["1", "2"]), new VolumeMapEntry("2", ["3"])]);
        Assert.Equal("1:1,2;2:3", Flat(merged.Volumes));
        Assert.Equal(3, merged.ChaptersFromWikipedia);
        Assert.Equal(2, merged.VolumesFromWikipedia);
    }

    [Fact]
    public void Merge_WithNothingToAdd_ChangesNothing()
    {
        var mangaDex = new[] { new VolumeMapEntry("1", ["1", "2"]) };
        var merged = VolumeListMerge.Merge(mangaDex, ["7"], [new VolumeMapEntry("1", ["1", "2"])]);
        Assert.False(merged.WikipediaFilled);
        Assert.Equal("1:1,2", Flat(merged.Volumes));
        Assert.Equal(["7"], merged.Unassigned);
    }

    [Fact]
    public void Merge_KeepsFractionalChaptersAndVolumeOrder()
    {
        var merged = VolumeListMerge.Merge([new VolumeMapEntry("10", ["90", "91"])], [], [new VolumeMapEntry("2", ["10", "10.5"]), new VolumeMapEntry("9", ["80"])]);
        Assert.Equal("2:10,10.5;9:80;10:90,91", Flat(merged.Volumes));
    }
}
