namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using Xunit;

/// <summary>
/// Unit tests for <see cref="FacetExtractor"/> (1.45.0 lane F, D1 2.4): the buckets of one location - creators with merged roles and
/// sources, magazines, publishers (record then ComicInfo publisher / imprint), the year (record, else the earliest ComicInfo year), genres
/// case-folded across sources, the type (declared, else from the record; a conflict shows both, the declared one flagged - owner
/// 2026-09-27 answer 3), declared creators the record contradicts flagged, and the About value. Synthetic names only.
/// </summary>
public sealed class FacetExtractorTests
{
    private static FacetRecord Record(MetadataOrigin? origin = MetadataOrigin.Japan, bool? webtoon = null, MetadataFormat? format = null,
        int? startYear = 2001) => new(
            10, "Series Alpha", origin, format, webtoon, startYear,
            [new FacetCredit("Tanaka Hiroshi", "author", "mangaupdates:7")],
            ["Action", "Drama"],
            [new FacetPublisher("English House", "english"), new FacetPublisher("Original House", "original")],
            ["Weekly Synthetic"]);

    private static FacetComicInfo Ci(long id, int? year = null, string? publisher = null, params string[] genres) =>
        new(id, "Series Alpha", [], genres, publisher, null, year);

    private static string Name(long id) => id switch { 1 => "Tanaka Hiroshi", 2 => "Second Artist", _ => "Somebody" };

    private static FacetEntryDraft One(IEnumerable<FacetEntryDraft> entries, FacetBucket bucket, string key) =>
        Assert.Single(entries, e => e.Bucket == bucket && e.ValueKey == key);

    [Fact]
    public void ALinkedLocation_GetsEveryBucket_WithSourcesAndRolesMerged()
    {
        var input = new FacetLocationInput(
            Record(),
            [new FacetResolvedCredit(1, FacetRoles.Writer, "Tanaka Hiroshi")],
            [Ci(100, 1999, "Original House", "action", "Romance")],
            [new FacetResolvedCredit(1, FacetRoles.Artist, "Hiroshi Tanaka"), new FacetResolvedCredit(2, FacetRoles.Artist, "Second Artist")],
            null, [], false, null);

        var entries = FacetExtractor.Extract(input, Name);

        var tanaka = One(entries, FacetBucket.Creators, "c:1");
        Assert.Equal((FacetSources.Web | FacetSources.ComicInfo, FacetRoles.Writer | FacetRoles.Artist, "Tanaka Hiroshi", 1L),
            (tanaka.Sources, tanaka.Roles, tanaka.Display, tanaka.CreatorId!.Value));
        Assert.Equal(FacetSources.ComicInfo, One(entries, FacetBucket.Creators, "c:2").Sources);
        Assert.Equal("Weekly Synthetic", One(entries, FacetBucket.Magazine, "m:weekly synthetic").Display);
        Assert.Equal(FacetSources.Web | FacetSources.ComicInfo, One(entries, FacetBucket.Publisher, "p:original house").Sources);
        Assert.Equal(new[] { "p:original house", "p:english house" },
            entries.Where(e => e.Bucket == FacetBucket.Publisher).Select(e => e.ValueKey).ToArray());
        // The record's start year wins over the ComicInfo year.
        Assert.Equal("2001", Assert.Single(entries, e => e.Bucket == FacetBucket.Year).Display);
        var action = One(entries, FacetBucket.Genres, "g:action");
        Assert.Equal(("Action", FacetSources.Web | FacetSources.ComicInfo), (action.Display, action.Sources));
        Assert.Equal(FacetSources.ComicInfo, One(entries, FacetBucket.Genres, "g:romance").Sources);
        Assert.Equal("Manga", One(entries, FacetBucket.Type, "t:manga").Display);
        Assert.DoesNotContain(entries, e => e.Conflict);
    }

    [Fact]
    public void AnUnlinkedLocation_TakesTheEarliestComicInfoYear()
    {
        var input = new FacetLocationInput(null, [], [Ci(101, 2005), Ci(102, 2003), Ci(103, 12)], [], null, [], false, null);

        var year = Assert.Single(FacetExtractor.Extract(input, Name), e => e.Bucket == FacetBucket.Year);
        Assert.Equal(("y:2003", FacetSources.ComicInfo), (year.ValueKey, year.Sources));
    }

    [Fact]
    public void ADeclaredTypeTheRecordContradicts_ShowsBoth_TheDeclaredOneFlagged()
    {
        var input = new FacetLocationInput(Record(), [], [], [], DeclaredType.Manhwa, [], false, null);

        var types = FacetExtractor.Extract(input, Name).Where(e => e.Bucket == FacetBucket.Type).ToList();

        Assert.Equal(new[] { ("t:manhwa", true), ("t:manga", false) }, types.Select(t => (t.ValueKey, t.Conflict)).ToArray());
    }

    [Fact]
    public void ADeclaredTypeTheRecordAgreesWith_IsOneValue_FromBothSources()
    {
        var input = new FacetLocationInput(Record(MetadataOrigin.Korea, webtoon: true), [], [], [], DeclaredType.Webtoon, [], false, null);

        var type = Assert.Single(FacetExtractor.Extract(input, Name), e => e.Bucket == FacetBucket.Type);
        Assert.Equal(("t:webtoon", FacetSources.Declared | FacetSources.Web, false), (type.ValueKey, type.Sources, type.Conflict));
    }

    [Fact]
    public void TheRecordsType_ComesFromOriginFormatAndWebtoon()
    {
        Assert.Equal(new[] { DeclaredType.Manhwa, DeclaredType.Webtoon }, FacetExtractor.TypesOf(Record(MetadataOrigin.Korea, webtoon: true)));
        Assert.Equal(new[] { DeclaredType.Manhua }, FacetExtractor.TypesOf(Record(MetadataOrigin.ChinaTaiwan)));
        Assert.Equal(new[] { DeclaredType.Comic }, FacetExtractor.TypesOf(Record(MetadataOrigin.French)));
        Assert.Equal(new[] { DeclaredType.Novel }, FacetExtractor.TypesOf(Record(MetadataOrigin.Japan, format: MetadataFormat.Novel)));
        Assert.Empty(FacetExtractor.TypesOf(Record(MetadataOrigin.Thailand)));
    }

    [Fact]
    public void DeclaredCreatorsTheRecordContradicts_AreFlagged_ButNotWhenTheRecordAlsoCreditsThem()
    {
        var input = new FacetLocationInput(
            Record(),
            [new FacetResolvedCredit(1, FacetRoles.Writer, "Tanaka Hiroshi")],
            [], [], null,
            [new FacetResolvedCredit(3, FacetRoles.None, "Somebody"), new FacetResolvedCredit(1, FacetRoles.Artist, "Tanaka Hiroshi")],
            DeclaredCreatorsConflict: true, null);

        var entries = FacetExtractor.Extract(input, Name);

        Assert.True(One(entries, FacetBucket.Creators, "c:3").Conflict);
        Assert.False(One(entries, FacetBucket.Creators, "c:1").Conflict);
        Assert.Equal(FacetSources.Web | FacetSources.Declared, One(entries, FacetBucket.Creators, "c:1").Sources);
    }

    [Fact]
    public void AWorkInACollectionAboutFolder_GetsTheAboutValue()
    {
        var input = new FacetLocationInput(null, [], [], [], null, [], false, (50, "The Original"));

        var about = Assert.Single(FacetExtractor.Extract(input, Name));
        Assert.Equal((FacetBucket.About, "a:50", "The Original"), (about.Bucket, about.ValueKey, about.Display));
    }
}
