namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets;
using Xunit;

/// <summary>1.45.0 lane Q: the typed field syntax of the search box (owner Q12) and the <c>f=</c> chip filter form. Pure.</summary>
[Trait("Category", "Unit")]
public sealed class FacetSearchSyntaxTests
{
    private static string Shape(ParsedFacetText parsed) =>
        string.Join(" | ", parsed.Fields.Select(f => $"{FacetBucketTokens.Token(f.Bucket)}={f.Value}")) + " || " + parsed.FreeText;

    [Theory]
    [InlineData("creator:\"Naoki Urasawa\"", "creator=Naoki Urasawa || ")]
    [InlineData("creator:Urasawa", "creator=Urasawa || ")]
    [InlineData("Creator:\"Naoki Urasawa\" monster", "creator=Naoki Urasawa || monster")]
    [InlineData("pluto genre:mystery year:2003", "genre=mystery | year=2003 || pluto")]
    [InlineData("publisher:\"Shogakukan\" magazine:\"Big Comic Original\" type:manga",
        "publisher=Shogakukan | magazine=Big Comic Original | type=manga || ")]
    [InlineData("creator: Urasawa", "creator=Urasawa || ")]
    [InlineData("creator: \"Naoki Urasawa\" x", "creator=Naoki Urasawa || x")]
    [InlineData("genres:mystery", "genre=mystery || ")]
    [InlineData("creator:\"unterminated quote", "creator=unterminated quote || ")]
    public void Parse_TypedFields(string input, string expected) =>
        Assert.Equal(expected, Shape(FacetSearchSyntax.Parse(input)));

    [Theory]
    [InlineData("Re:Zero", " || Re:Zero")]
    [InlineData("about:Pluto", " || about:Pluto")] // about / custom are chip-only, not typed (owner Q12's list; 1.49.0 adds tag + series)
    [InlineData("is:favorite", " || is:favorite")] // 1.49.0: an unknown is: word stays text
    [InlineData("- genre:x", "genre=x || -")]
    [InlineData("creator:", " || creator:")]
    [InlineData("creator:\"\"", " || creator:\"\"")]
    [InlineData("\"quoted text\" plain", " || \"quoted text\" plain")]
    [InlineData("   ", " || ")]
    [InlineData("Pluto  Saga", " || Pluto Saga")]
    public void Parse_LeavesEverythingElseAsFreeText(string input, string expected) =>
        Assert.Equal(expected, Shape(FacetSearchSyntax.Parse(input)));

    [Fact]
    public void Parse_KeepsAtMostTenFields_TheRestStaysText()
    {
        var input = string.Join(' ', Enumerable.Range(1, 12).Select(i => $"genre:g{i}"));
        var parsed = FacetSearchSyntax.Parse(input);
        Assert.Equal(FacetSearchSyntax.MaxFields, parsed.Fields.Count);
        Assert.Equal("genre:g11 genre:g12", parsed.FreeText);
    }

    [Fact]
    public void Parse_RefusesOverlongValues_AsText()
    {
        var value = new string('a', FacetSearchSyntax.MaxValueLength + 1);
        var parsed = FacetSearchSyntax.Parse("creator:" + value);
        Assert.Empty(parsed.Fields);
        Assert.Equal("creator:" + value, parsed.FreeText);
    }

    [Theory]
    [InlineData("creator", FacetBucket.Creators)]
    [InlineData("Creators", FacetBucket.Creators)]
    [InlineData("genre", FacetBucket.Genres)]
    [InlineData("GENRES", FacetBucket.Genres)]
    [InlineData("series", FacetBucket.Series)]
    [InlineData("about", FacetBucket.About)]
    public void BucketTokens_Parse(string name, FacetBucket expected)
    {
        Assert.True(FacetBucketTokens.TryParse(name, out var bucket));
        Assert.Equal(expected, bucket);
    }

    [Fact]
    public void BucketTokens_RoundTrip_EveryBucket()
    {
        foreach (var bucket in Enum.GetValues<FacetBucket>())
        {
            Assert.True(FacetBucketTokens.TryParse(FacetBucketTokens.Token(bucket), out var parsed));
            Assert.Equal(bucket, parsed);
        }
        Assert.False(FacetBucketTokens.TryParse("bogus", out _));
        Assert.False(FacetBucketTokens.TryParse(null, out _));
    }

    [Fact]
    public void ChipFilter_IsBucketColonValueId()
    {
        var chip = FacetSearchService.ParseChip("creator:fv_abc123");
        Assert.Equal((FacetBucket.Creators, false, "fv_abc123"), (chip.Bucket, chip.Negated, chip.Raw));
        Assert.Equal(["fv_abc123"], chip.ValueIds);
        foreach (var bad in new[] { "creator:abc", "nope:fv_abc", "fv_abc", ":fv_abc", "creator:fv_", "creator:fv_ABC", "--genre:fv_a",
                     "genre:fv_a|", "genre:fv_a||fv_b", "-:fv_a", "genre:fv_a|abc" })
            Assert.Throws<FacetFilterFormatException>(() => FacetSearchService.ParseChip(bad));
    }

    // 1.49.0 (lane T, T2): exclusion and any-of in the f grammar; the 1.45.0 form above stays valid.
    [Fact]
    public void ChipFilter_ExcludeAndAnyOf()
    {
        var excluded = FacetSearchService.ParseChip("-genre:fv_a");
        Assert.Equal((FacetBucket.Genres, true, "fv_a"), (excluded.Bucket, excluded.Negated, excluded.Raw));
        var anyOf = FacetSearchService.ParseChip("tag:fv_a|fv_b|fv_a");
        Assert.Equal((FacetBucket.Tags, false), (anyOf.Bucket, anyOf.Negated));
        Assert.Equal(["fv_a", "fv_b"], anyOf.ValueIds);
        var both = FacetSearchService.ParseChip("-tags:fv_a|fv_b");
        Assert.True(both.Negated);
        Assert.Equal(2, both.ValueIds.Count);
        var tooMany = "genre:" + string.Join('|', Enumerable.Range(1, FacetSearchService.MaxAlternatives + 1).Select(i => "fv_a" + i));
        Assert.Throws<FacetFilterFormatException>(() => FacetSearchService.ParseChip(tooMany));
    }

    // 1.49.0 (lane T, S4): tag:, series:, a leading -, quotes for an exact value, | for alternatives, year ranges, is: and library:.
    [Theory]
    [InlineData("tag:isekai", "tag=isekai || ")]
    [InlineData("tags:\"time travel\" pluto", "tag=time travel || pluto")]
    [InlineData("series:Pluto", "series=Pluto || ")]
    [InlineData("-genre:horror monster", "-genre=horror || monster")]
    [InlineData("-creator:\"Naoki Urasawa\"", "-creator=Naoki Urasawa || ")]
    [InlineData("genre:horror|mystery", "genre=horror|mystery || ")]
    [InlineData("year:2010..2015", "year=2010..2015 || ")]
    [InlineData("-word plain", " || -word plain")]
    public void Parse_Lane149Fields(string input, string expected)
    {
        var parsed = FacetSearchSyntax.Parse(input);
        var shape = string.Join(" | ", parsed.Fields.Select(f => $"{(f.Negated ? "-" : "")}{FacetBucketTokens.Token(f.Bucket)}={f.Value}"))
            + " || " + parsed.FreeText;
        Assert.Equal(expected, shape);
    }

    [Fact]
    public void Parse_QuotedValueIsExact_AndHasOneAlternative()
    {
        var quoted = Assert.Single(FacetSearchSyntax.Parse("genre:\"horror|mystery\"").Fields);
        Assert.True(quoted.Exact);
        Assert.Equal(["horror|mystery"], quoted.Alternatives);
        var bare = Assert.Single(FacetSearchSyntax.Parse("genre:horror|mystery").Fields);
        Assert.False(bare.Exact);
        Assert.Equal(["horror", "mystery"], bare.Alternatives);
        Assert.Equal("-genre:\"horror|mystery\"", FacetSearchSyntax.Render(quoted with { Negated = true }));
    }

    [Theory]
    [InlineData("is:read monster", "read", null, "monster")]
    [InlineData("IS:Unread", "unread", null, "")]
    [InlineData("is:reading library:\"Manga Shelf\" x", "reading", "Manga Shelf", "x")]
    [InlineData("library:Comics genre:drama", null, "Comics", "")]
    [InlineData("-is:read", null, null, "-is:read")]
    public void Parse_ScopeWords(string input, string? read, string? library, string free)
    {
        var parsed = FacetSearchSyntax.Parse(input);
        Assert.Equal((read, library, free), (parsed.ReadState, parsed.Library, parsed.FreeText));
        Assert.Equal(read is not null || library is not null, parsed.HasScope);
    }

    [Theory]
    [InlineData("2004", 2004, 2004)]
    [InlineData("2010..2015", 2010, 2015)]
    [InlineData("2015..2010", 2010, 2015)]
    [InlineData("..1999", 1000, 1999)]
    [InlineData("2020..", 2020, 9999)]
    public void YearRange_Parses(string value, int from, int to) =>
        Assert.Equal((from, to), FacetSearchSyntax.YearRange(value));

    [Theory]
    [InlineData("..")]
    [InlineData("20")]
    [InlineData("abcd..2000")]
    [InlineData("nineteen")]
    public void YearRange_RefusesOtherText(string value) => Assert.Null(FacetSearchSyntax.YearRange(value));

    [Fact]
    public void TagViewText_SplitsWords_QuotesAndNegation()
    {
        var words = TagViewText.Words("Pluto \"big comic\" -saga - x");
        Assert.Equal([("Pluto", false), ("big comic", false), ("saga", true), ("x", false)],
            words.Select(w => (w.Text, w.Negated)).ToArray());
        Assert.Empty(TagViewText.Words("  \"\" "));
    }

    [Fact]
    public void WorksCursor_RoundTrips_AndAMalformedOneStartsOver()
    {
        var cursor = FacetSearchService.EncodeCursor(42, "monster:with colon");
        Assert.Equal(("monster:with colon", 42L), FacetSearchService.ParseCursor(cursor));
        Assert.Null(FacetSearchService.ParseCursor("k:1:x"));
        Assert.Null(FacetSearchService.ParseCursor("w:x:y"));
        Assert.Null(FacetSearchService.ParseCursor(null));
    }

    [Fact]
    public void ViewerScope_HashNamesTheSortedSet_NotTheOrder()
    {
        Assert.Equal(FacetViewerScope.Of([3, 1, 2]).Hash, FacetViewerScope.Of([1, 2, 3, 3]).Hash);
        Assert.NotEqual(FacetViewerScope.Of([1, 2]).Hash, FacetViewerScope.Of([1, 2, 3]).Hash);
        Assert.NotEqual(FacetViewerScope.Of([12]).Hash, FacetViewerScope.Of([1, 2]).Hash);
        Assert.True(FacetViewerScope.Empty.IsEmpty);
        Assert.True(FacetViewerScope.Of([1, 2]).Narrow(3).IsEmpty);
        Assert.Equal([2L], FacetViewerScope.Of([1, 2]).Narrow(2).Shown);
    }
}
