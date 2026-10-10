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
}
