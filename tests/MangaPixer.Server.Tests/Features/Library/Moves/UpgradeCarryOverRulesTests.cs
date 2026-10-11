namespace com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Server.Features.Library.Moves;
using Xunit;

/// <summary>Unit tests of the pure chapter-to-volume upgrade rules (1.40.0, <see cref="UpgradeCarryOver"/>). Synthetic names.</summary>
public sealed class UpgradeCarryOverRulesTests
{
    private static UpgradeChapterFile File(long id, string name, string? folder = "Synthetic Saga") =>
        new(id, UpgradeCarryOver.UnitsOfArchive(name, folder, null, null));

    private static decimal[] Units(params string[] values) =>
        values.Select(v => decimal.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    [Theory]
    [InlineData("Synthetic Saga v01 (2026) (Digital).cbz", 1)]
    [InlineData("Synthetic Saga Vol. 3.cbz", 3)]
    [InlineData("Synthetic Saga v01 c003.cbz", null)]
    [InlineData("Synthetic Saga c012.cbz", null)]
    [InlineData("Synthetic Saga v01-03.cbz", null)]
    [InlineData("Synthetic Saga v02.5.cbz", null)]
    public void VolumeOf_OnlyOneWholeVolumeWithoutAChapter(string name, int? expected)
    {
        var volume = UpgradeCarryOver.VolumeOf(UpgradeCarryOver.UnitsOfArchive(name, "Synthetic Saga", null, null));
        Assert.Equal(expected is { } v ? (decimal?)v : null, volume);
    }

    [Fact]
    public void UnitsOf_ExtrasNextToTheirWholeAreOptional_PartsOfASplitChapterAreRequired()
    {
        var units = UpgradeCarryOver.UnitsOf(1, Units("1", "2", "3", "4.1", "4.2", "9", "9.5"));
        Assert.Equal(Units("1", "2", "3", "4.1", "4.2", "9"), units.Required);
        Assert.Equal(Units("9.5"), units.Extras);
    }

    [Fact]
    public void Decide_AllRequiredRead_IsRead_EvenWithoutTheExtra()
    {
        var units = UpgradeCarryOver.UnitsOf(1, Units("1", "2", "2.5"));
        var files = new[] { File(1, "Synthetic Saga c001.cbz"), File(2, "Synthetic Saga c002.cbz"), File(3, "Synthetic Saga c002.5.cbz") };
        var coverage = UpgradeCarryOver.Cover(units, files);
        Assert.Equal(UpgradeOutcome.Read, UpgradeCarryOver.Decide(units, coverage, new HashSet<long> { 1, 2 }));
        Assert.Equal(UpgradeOutcome.InProgress, UpgradeCarryOver.Decide(units, coverage, new HashSet<long> { 1 }));
        // An extra alone counts as "some read".
        Assert.Equal(UpgradeOutcome.InProgress, UpgradeCarryOver.Decide(units, coverage, new HashSet<long> { 3 }));
        Assert.Equal(UpgradeOutcome.Nothing, UpgradeCarryOver.Decide(units, coverage, new HashSet<long>()));
    }

    [Fact]
    public void Cover_ARangeFileCoversItsListedChapters()
    {
        var units = UpgradeCarryOver.UnitsOf(1, Units("1", "2", "3", "4"));
        var files = new[] { File(1, "Synthetic Saga c001-003.cbz"), File(2, "Synthetic Saga c004.cbz") };
        var coverage = UpgradeCarryOver.Cover(units, files);
        Assert.Equal(UpgradeOutcome.Read, UpgradeCarryOver.Decide(units, coverage, new HashSet<long> { 1, 2 }));
        Assert.Equal(UpgradeOutcome.InProgress, UpgradeCarryOver.Decide(units, coverage, new HashSet<long> { 1 }));
    }

    [Fact]
    public void Cover_PartsOnDiskOfAListedWholeChapter_CountWhenEveryPartIsRead()
    {
        var units = UpgradeCarryOver.UnitsOf(1, Units("1", "2"));
        var files = new[] { File(1, "Synthetic Saga c001.cbz"), File(2, "Synthetic Saga c002.1.cbz"), File(3, "Synthetic Saga c002.2.cbz") };
        var coverage = UpgradeCarryOver.Cover(units, files);
        Assert.Equal(3, coverage.Evidence.Count);
        Assert.Equal(UpgradeOutcome.Read, UpgradeCarryOver.Decide(units, coverage, new HashSet<long> { 1, 2, 3 }));
        Assert.Equal(UpgradeOutcome.InProgress, UpgradeCarryOver.Decide(units, coverage, new HashSet<long> { 1, 2 }));
    }

    [Fact]
    public void Cover_AWholeChapterFileHoldsTheListedPartsOfItsChapter()
    {
        var units = UpgradeCarryOver.UnitsOf(1, Units("3", "4.1", "4.2"));
        var files = new[] { File(1, "Synthetic Saga c003.cbz"), File(2, "Synthetic Saga c004.cbz") };
        var coverage = UpgradeCarryOver.Cover(units, files);
        Assert.Equal(UpgradeOutcome.Read, UpgradeCarryOver.Decide(units, coverage, new HashSet<long> { 1, 2 }));
    }

    [Fact]
    public void Cover_AFileStatingAnotherVolume_OrAChapterOutsideTheList_IsNotEvidence()
    {
        var units = UpgradeCarryOver.UnitsOf(1, Units("1", "2"));
        var files = new[]
        {
            File(1, "Synthetic Saga v01 c001.cbz"),
            File(2, "Synthetic Saga v02 c002.cbz"),
            File(3, "Synthetic Saga c007.cbz"),
            File(4, "Synthetic Saga c002.cbz"),
        };
        var coverage = UpgradeCarryOver.Cover(units, files);
        Assert.Equal(new HashSet<long> { 1, 4 }, coverage.Evidence.ToHashSet());
        Assert.Equal(UpgradeOutcome.Nothing, UpgradeCarryOver.Decide(units, coverage, new HashSet<long> { 2, 3 }));
    }

    [Fact]
    public void Decide_DuplicateFiles_EitherCopyCounts()
    {
        var units = UpgradeCarryOver.UnitsOf(1, Units("1", "2"));
        var files = new[]
        {
            File(1, "Synthetic Saga c001 [GroupA].cbz"), File(2, "Synthetic Saga c001 [GroupB].cbz"),
            File(3, "Synthetic Saga c002 [GroupA].cbz"), File(4, "Synthetic Saga c002 [GroupB].cbz"),
        };
        var coverage = UpgradeCarryOver.Cover(units, files);
        Assert.Equal(UpgradeOutcome.Read, UpgradeCarryOver.Decide(units, coverage, new HashSet<long> { 2, 3 }));
    }

    // 1.41.0 follow-ups: range volume files, a volume replacing a volume.

    private static IReadOnlyList<decimal> VolumesOf(string name) =>
        UpgradeCarryOver.VolumesOf(UpgradeCarryOver.UnitsOfArchive(name, "Synthetic Saga", null, null));

    [Theory]
    [InlineData("Synthetic Saga v01-03.cbz", "1,2,3")]
    [InlineData("Synthetic Saga Vol. 1-3 (2026).cbz", "1,2,3")]
    [InlineData("Synthetic Saga Volume 01-05 [Group].cbz", "1,2,3,4,5")]
    [InlineData("Synthetic Saga v02 (Digital).cbz", "2")]
    [InlineData("Synthetic Saga v01-03 c012.cbz", "")]
    [InlineData("Synthetic Saga v02.5.cbz", "")]
    [InlineData("Synthetic Saga c001-010.cbz", "")]
    [InlineData("Synthetic Saga v01-200.cbz", "")]
    public void VolumesOf_OneWholeVolumeOrARangeOfWholeVolumes_WithoutAChapter(string name, string expected)
    {
        decimal[] want = expected.Length == 0 ? [] : Units(expected.Split(','));
        Assert.Equal(want, VolumesOf(name));
    }

    [Theory]
    [InlineData("Synthetic Saga v03.cbz", "Synthetic Saga v03 (Re-encode) [HQ] (2026).cbz", true)]
    [InlineData("Synthetic Saga v03 [GroupA].cbz", "Synthetic_Saga_v03.cbz", true)]
    [InlineData("Synthetic Saga v03.cbz", "Synthetic Saga v03 Digital 1080p.cbz", true)]
    [InlineData("Synthetic Saga v03.cbz", "Synthetic Saga 2019 v03.cbz", true)]
    [InlineData("Vol. 03.cbz", "Synthetic Saga v03.cbz", true)]
    [InlineData("Synthetic Saga v03.cbz", "Other Tale v03.cbz", false)]
    [InlineData("Synthetic Saga v03.cbz", "Synthetic Saga 2 v03.cbz", false)]
    [InlineData("Synthetic Saga v03.cbz", "Synthetic Saga Kanzenban v03.cbz", false)]
    [InlineData("Synthetic Saga v03.cbz", "Synthetic Saga Omnibus v03.cbz", false)]
    public void SameWork_TheNamesAgreeWithoutUnitsTagsYearsAndExtension_AndOnTheEdition(string a, string b, bool expected)
    {
        Assert.Equal(expected, UpgradeCarryOver.SameWork(a, b, "Synthetic Saga"));
        Assert.Equal(expected, UpgradeCarryOver.SameWork(b, a, "Synthetic Saga"));
    }

    private static readonly IReadOnlyDictionary<decimal, IReadOnlyList<decimal>> ThreeVolumes = new Dictionary<decimal, IReadOnlyList<decimal>>
    {
        [1] = Units("1", "2", "3"),
        [2] = Units("4", "5", "6"),
        [3] = Units("7", "8", "9"),
    };

    [Fact]
    public void Plan_AVolumeWithItsOwnReplacedFile_UsesIt_TheOthersUseTheirListedChapters()
    {
        var files = new[] { File(1, "Synthetic Saga v01.cbz"), File(2, "Synthetic Saga c004.cbz"), File(3, "Synthetic Saga c007-009.cbz") };
        var plans = UpgradeCarryOver.Plan([1, 2, 3], ThreeVolumes, files, _ => true)!;
        Assert.Equal([1L], plans[0].VolumeFiles);
        Assert.Null(plans[0].Coverage);
        Assert.Equal([2L], plans[1].Evidence);
        Assert.Equal([3L], plans[2].Evidence);
    }

    [Fact]
    public void Plan_AVolumeWithoutItsOwnFileAndWithoutAnExactListEntry_GivesNothingForTheWholeRange()
    {
        var files = new[] { File(1, "Synthetic Saga v01.cbz"), File(2, "Synthetic Saga v02.cbz") };
        Assert.Null(UpgradeCarryOver.Plan([1, 2, 3], null, files, _ => true));
        var twoListed = new Dictionary<decimal, IReadOnlyList<decimal>> { [1] = Units("1", "2", "3"), [2] = Units("4", "5", "6") };
        Assert.Null(UpgradeCarryOver.Plan([1, 2, 3], twoListed, files, _ => true));
        // Every volume with its own file: no list needed.
        Assert.NotNull(UpgradeCarryOver.Plan([1, 2], null, files, _ => true));
        // A file the caller rejects (another work, identical bytes) is no evidence.
        Assert.Null(UpgradeCarryOver.Plan([1, 2], null, files, id => id != 2));
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DecideVolume_TheMostRecentlyReadFileDecides_AFileOpenedAtPage1SaysNothing()
    {
        Assert.Equal(UpgradeOutcome.Nothing, UpgradeCarryOver.DecideVolume([new(1, false, 0, T0)]).Outcome);
        var rereading = UpgradeCarryOver.DecideVolume([new(1, true, 0, T0), new(2, false, 12, T0.AddDays(1)), new(3, false, 0, T0.AddDays(2))]);
        Assert.Equal((UpgradeOutcome.InProgress, 12, (long?)2), (rereading.Outcome, rereading.Ordinal, rereading.FromNode));
        var finishedLater = UpgradeCarryOver.DecideVolume([new(1, true, 0, T0.AddDays(2)), new(2, false, 12, T0.AddDays(1))]);
        Assert.Equal((UpgradeOutcome.Read, 0, (long?)1), (finishedLater.Outcome, finishedLater.Ordinal, finishedLater.FromNode));
    }

    [Fact]
    public void DecideTarget_ARangeIsReadWhenEveryVolumeIsRead_InProgressAtPage1WhenAnyIs()
    {
        var files = new[] { File(1, "Synthetic Saga v01.cbz"), File(2, "Synthetic Saga c004-006.cbz"), File(3, "Synthetic Saga c007-009.cbz") };
        var plans = UpgradeCarryOver.Plan([1, 2, 3], ThreeVolumes, files, _ => true)!;
        UpgradeCarry Decide(long[] read, params UpgradeVolumeFileState[] states) =>
            UpgradeCarryOver.DecideTarget(plans, read.ToHashSet(), states);

        Assert.Equal(UpgradeOutcome.Read, Decide([1, 2, 3], new UpgradeVolumeFileState(1, true, 0, T0)).Outcome);
        var partly = Decide([2, 3], new UpgradeVolumeFileState(1, false, 30, T0));
        Assert.Equal((UpgradeOutcome.InProgress, 0), (partly.Outcome, partly.Ordinal));
        Assert.Equal(UpgradeOutcome.InProgress, Decide([], new UpgradeVolumeFileState(1, false, 30, T0)).Outcome);
        Assert.Equal(UpgradeOutcome.Nothing, Decide([]).Outcome);
    }

    [Theory]
    [InlineData(30, 180, 180, 30)]
    [InlineData(30, 180, 175, 0)]
    [InlineData(30, null, 180, 0)]
    [InlineData(0, 180, 180, 0)]
    public void PageFor_TheOldPageOnlyWithEqualPageCounts(int ordinal, int? oldPages, int? newPages, int expected) =>
        Assert.Equal(expected, UpgradeCarryOver.PageFor(ordinal, oldPages, newPages));
}
