namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata.Facets;
using Xunit;

/// <summary>
/// Unit tests for the 1.45.0 tag-index vocabulary (seam I0): the stored numbers never move, and the work key round-trips in its three
/// forms - a ComicInfo key carries the RESOLVED creator ids sorted and distinct (owner Q5), so the same creators in any order or
/// repeated give one key, and only canonical keys parse (string equality = same work).
/// </summary>
public sealed class FacetVocabularyTests
{
    [Fact]
    public void TheStoredNumbers_AreTheDesignsAndNeverMove()
    {
        Assert.Equal(
            new[] { (1, "Creators"), (2, "Series"), (3, "Magazine"), (4, "Publisher"), (5, "Year"), (6, "Genres"), (7, "Type"), (8, "About"), (9, "Custom"), (10, "Tags") },
            Enum.GetValues<FacetBucket>().Select(b => ((int)b, b.ToString())).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 4, 8 }, Enum.GetValues<FacetSources>().Select(s => (int)s).ToArray());
        Assert.Equal((1, 2, 4, 8), ((int)FacetSources.Web, (int)FacetSources.ComicInfo, (int)FacetSources.Declared, (int)FacetSources.Admin));
        Assert.Equal((1, 2), ((int)FacetRoles.Writer, (int)FacetRoles.Artist));
        Assert.Equal((0, 1, 2), ((int)FacetWorkKind.Record, (int)FacetWorkKind.ComicInfo, (int)FacetWorkKind.Node));
        Assert.Equal((0, 1, 2, 3, 4), ((int)CreatorNameSource.Record, (int)CreatorNameSource.ProviderAuthor, (int)CreatorNameSource.ComicInfo,
            (int)CreatorNameSource.Declared, (int)CreatorNameSource.Admin));
        Assert.Equal((0, 1, 2), ((int)CreatorDisplayNameSource.Provider, (int)CreatorDisplayNameSource.MostUsed, (int)CreatorDisplayNameSource.Admin));
        Assert.Equal((0, 1, 2), ((int)CreatorOverrideKind.Merge, (int)CreatorOverrideKind.SplitPin, (int)CreatorOverrideKind.DisplayName));
        Assert.Equal((0, 1, 2, 3), ((int)CreatorNameScript.Latin, (int)CreatorNameScript.Cjk, (int)CreatorNameScript.Hangul, (int)CreatorNameScript.Other));
    }

    [Fact]
    public void RecordAndNodeKeys_FormatAndParse()
    {
        Assert.Equal("rec:42", FacetWorkKey.Record(42));
        Assert.Equal("node:9007199254740993", FacetWorkKey.Node(9007199254740993));

        Assert.True(FacetWorkKey.TryParse("rec:42", out var record));
        Assert.Equal((FacetWorkKind.Record, 42L, (string?)null), (record.Kind, record.Id, record.SeriesForm));
        Assert.Empty(record.CreatorIds);

        Assert.True(FacetWorkKey.TryParse(FacetWorkKey.Node(long.MaxValue), out var node));
        Assert.Equal((FacetWorkKind.Node, long.MaxValue, (string?)null), (node.Kind, node.Id, node.SeriesForm));

        Assert.Throws<ArgumentOutOfRangeException>(() => FacetWorkKey.Record(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FacetWorkKey.Node(-5));
    }

    [Fact]
    public void AComicInfoKey_UsesTheResolvedCreatorIds_SortedAndDistinct_SoAnyOrderGivesOneKey()
    {
        var a = FacetWorkKey.ComicInfo("one piece", [31, 7, 1200]);
        var b = FacetWorkKey.ComicInfo("one piece", [1200, 7, 31, 7]);
        Assert.Equal("ci:one piece#7,31,1200", a);
        Assert.Equal(a, b);
        Assert.NotEqual(a, FacetWorkKey.ComicInfo("one piece", [7, 31]));

        Assert.True(FacetWorkKey.TryParse(a, out var parts));
        Assert.Equal(FacetWorkKind.ComicInfo, parts.Kind);
        Assert.Equal(0, parts.Id);
        Assert.Equal("one piece", parts.SeriesForm);
        Assert.Equal(new long[] { 7, 31, 1200 }, parts.CreatorIds);
    }

    [Fact]
    public void AComicInfoKey_SplitsAtTheLastHash_SoASeriesFormMayHoldOne()
    {
        var key = FacetWorkKey.ComicInfo("x-men #1 special", [5]);
        Assert.Equal("ci:x-men #1 special#5", key);
        Assert.True(FacetWorkKey.TryParse(key, out var parts));
        Assert.Equal("x-men #1 special", parts.SeriesForm);
        Assert.Equal(new long[] { 5 }, parts.CreatorIds);
    }

    [Fact]
    public void AComicInfoKey_NeedsACreatorAndAUsableSeriesForm()
    {
        Assert.Throws<ArgumentException>(() => FacetWorkKey.ComicInfo("series", []));
        Assert.Throws<ArgumentException>(() => FacetWorkKey.ComicInfo("", [1]));
        Assert.Throws<ArgumentException>(() => FacetWorkKey.ComicInfo(" series", [1]));
        Assert.Throws<ArgumentException>(() => FacetWorkKey.ComicInfo("series\n", [1]));
        Assert.Throws<ArgumentException>(() => FacetWorkKey.ComicInfo("se\u0001ries", [1]));
        Assert.Throws<ArgumentException>(() => FacetWorkKey.ComicInfo(new string('s', FacetWorkKey.MaxSeriesFormLength + 1), [1]));
        Assert.Throws<ArgumentException>(() => FacetWorkKey.ComicInfo("series", Enumerable.Range(1, FacetWorkKey.MaxCreatorIds + 1).Select(i => (long)i)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FacetWorkKey.ComicInfo("series", [3, 0]));

        // The longest key fits the column.
        var longest = FacetWorkKey.ComicInfo(new string('s', FacetWorkKey.MaxSeriesFormLength),
            Enumerable.Range(0, FacetWorkKey.MaxCreatorIds).Select(i => long.MaxValue - i));
        Assert.Equal(FacetWorkKey.MaxLength, longest.Length);
        Assert.True(FacetWorkKey.TryParse(longest, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("42")]
    [InlineData("rec:")]
    [InlineData("rec:0")]
    [InlineData("rec:042")]
    [InlineData("rec:+42")]
    [InlineData("rec:-42")]
    [InlineData("rec:4 2")]
    [InlineData("rec:9223372036854775808")]
    [InlineData("REC:42")]
    [InlineData("node:abc")]
    [InlineData("ci:")]
    [InlineData("ci:series")]
    [InlineData("ci:#5")]
    [InlineData("ci:series#")]
    [InlineData("ci:series#5,")]
    [InlineData("ci:series#31,7")]
    [InlineData("ci:series#7,7")]
    [InlineData("ci:series#07")]
    [InlineData("ci: series#7")]
    [InlineData("tag:7")]
    public void NonCanonicalKeys_DoNotParse(string? key)
    {
        Assert.False(FacetWorkKey.TryParse(key, out var parts));
        Assert.Null(parts);
    }

    [Fact]
    public void PublicIds_CarryTheirPrefix_AndAreRandomBase36()
    {
        var creators = Enumerable.Range(0, 200).Select(_ => FacetPublicIds.NewCreatorId()).ToList();
        var values = Enumerable.Range(0, 200).Select(_ => FacetPublicIds.NewValueId()).ToList();
        Assert.All(creators, id => Assert.True(FacetPublicIds.IsCreatorId(id), id));
        Assert.All(values, id => Assert.True(FacetPublicIds.IsValueId(id), id));
        Assert.All(creators.Concat(values), id => Assert.InRange(id.Length, 4, FacetPublicIds.MaxLength));
        Assert.True(creators.Distinct().Count() > 190);

        Assert.False(FacetPublicIds.IsCreatorId(values[0]));
        Assert.False(FacetPublicIds.IsValueId(creators[0]));
        foreach (var bad in new[] { null, "", "cr_", "cr_0abc", "cr_ABC", "cr_ab-c", "cr_" + new string('z', 14), "xx_abc" })
            Assert.False(FacetPublicIds.IsCreatorId(bad), bad);
    }
}
