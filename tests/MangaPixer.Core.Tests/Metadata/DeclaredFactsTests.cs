namespace com.lifepixer.mangapixer.Tests.Core.Metadata;

using com.lifepixer.mangapixer.Core.Metadata;
using Xunit;

/// <summary>
/// Unit tests for the declared-facts vocabulary (1.28.0): stable type slugs, creator name cleaning, and the
/// conflict rules between a declaration and a linked record. Synthetic and public names only.
/// </summary>
public sealed class DeclaredFactsTests
{
    [Fact]
    public void TypeSlugs_RoundTrip_AndAreStable()
    {
        foreach (var type in Enum.GetValues<DeclaredType>())
            Assert.Equal(type, DeclaredFactKeys.ParseType(DeclaredFactKeys.TypeSlug(type)));

        // Stored values: never renamed (existing rows keep their meaning).
        Assert.Equal("manga", DeclaredFactKeys.TypeSlug(DeclaredType.Manga));
        Assert.Equal("graphic-novel", DeclaredFactKeys.TypeSlug(DeclaredType.GraphicNovel));
        Assert.Null(DeclaredFactKeys.ParseType("Manga"));
        Assert.Null(DeclaredFactKeys.ParseType("light-novel"));
        Assert.Null(DeclaredFactKeys.ParseType(null));
    }

    [Theory]
    [InlineData("  Some   Author ", "Some Author")]
    [InlineData("Author\tName", "Author Name")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void CleanName_TrimsAndCollapses(string? input, string? expected) =>
        Assert.Equal(expected, DeclaredFactKeys.CleanName(input));

    [Fact]
    public void CleanName_RejectsTooLong_AndControlCharacters()
    {
        Assert.Null(DeclaredFactKeys.CleanName(new string('a', DeclaredFactKeys.MaxValueLength + 1)));
        Assert.NotNull(DeclaredFactKeys.CleanName(new string('a', DeclaredFactKeys.MaxValueLength)));
        Assert.Null(DeclaredFactKeys.CleanName("Name\u0007Bell"));
    }

    [Theory]
    // Origin mismatches for the three origin-bound types.
    [InlineData(DeclaredType.Manga, MetadataOrigin.Japan, MetadataFormat.Comic, false)]
    [InlineData(DeclaredType.Manga, MetadataOrigin.Korea, MetadataFormat.Comic, true)]
    [InlineData(DeclaredType.Manhwa, MetadataOrigin.Korea, null, false)]
    [InlineData(DeclaredType.Manhwa, MetadataOrigin.Japan, null, true)]
    [InlineData(DeclaredType.Manhua, MetadataOrigin.ChinaTaiwan, null, false)]
    [InlineData(DeclaredType.Manhua, MetadataOrigin.Korea, null, true)]
    // Comic / graphic novel contradict the three Asian comic origins only.
    [InlineData(DeclaredType.Comic, MetadataOrigin.EnglishOriginal, MetadataFormat.Comic, false)]
    [InlineData(DeclaredType.Comic, MetadataOrigin.French, null, false)]
    [InlineData(DeclaredType.Comic, MetadataOrigin.Japan, null, true)]
    [InlineData(DeclaredType.GraphicNovel, MetadataOrigin.Korea, null, true)]
    // Webtoons come from everywhere; only a novel contradicts them.
    [InlineData(DeclaredType.Webtoon, MetadataOrigin.Japan, MetadataFormat.Comic, false)]
    [InlineData(DeclaredType.Webtoon, MetadataOrigin.Korea, MetadataFormat.Novel, true)]
    // Novel vs a comic-like format, and every other type vs a novel.
    [InlineData(DeclaredType.Novel, MetadataOrigin.Japan, MetadataFormat.Novel, false)]
    [InlineData(DeclaredType.Novel, MetadataOrigin.Japan, MetadataFormat.Comic, true)]
    [InlineData(DeclaredType.Novel, null, MetadataFormat.Doujinshi, true)]
    [InlineData(DeclaredType.Manga, MetadataOrigin.Japan, MetadataFormat.Novel, true)]
    // Doujinshi are manga: no conflict.
    [InlineData(DeclaredType.Manga, MetadataOrigin.Japan, MetadataFormat.Doujinshi, false)]
    // Unknown record values never conflict.
    [InlineData(DeclaredType.Manga, null, null, false)]
    [InlineData(DeclaredType.Novel, null, null, false)]
    public void TypeConflicts(DeclaredType declared, MetadataOrigin? origin, MetadataFormat? format, bool expected) =>
        Assert.Equal(expected, DeclaredFactsComparer.TypeConflicts(declared, origin, format));

    [Fact]
    public void CreatorsConflict_OnlyWhenBothSidesNameCreators_AndNoneMatch()
    {
        Assert.False(DeclaredFactsComparer.CreatorsConflict(["ODA Eiichiro"], ["Eiichiro Oda"]));
        Assert.False(DeclaredFactsComparer.CreatorsConflict(["Tsugumi Ohba", "Takeshi Obata"], ["OBATA Takeshi"]));
        Assert.False(DeclaredFactsComparer.CreatorsConflict(["Émile Author"], ["emile author"]));
        Assert.True(DeclaredFactsComparer.CreatorsConflict(["Someone Else"], ["Eiichiro Oda"]));
        // A spelling variant is a different name (no fuzzy matching).
        Assert.True(DeclaredFactsComparer.CreatorsConflict(["Oda Eiichirou"], ["Oda Eiichiro"]));
        Assert.False(DeclaredFactsComparer.CreatorsConflict([], ["Eiichiro Oda"]));
        Assert.False(DeclaredFactsComparer.CreatorsConflict(["Eiichiro Oda"], []));
    }

    [Theory]
    [InlineData("ODA, Eiichiro", "eiichiro oda")]
    [InlineData("Oda  Eiichiro", "eiichiro oda")]
    [InlineData("Émile", "emile")]
    [InlineData("---", "")]
    public void NameKey_IsFoldedAndOrderFree(string name, string expected) =>
        Assert.Equal(expected, DeclaredFactsComparer.NameKey(name));

    [Fact]
    public void DeclaredFacts_TypeValue_And_IsEmpty()
    {
        Assert.True(DeclaredFacts.Empty.IsEmpty);
        var facts = new DeclaredFacts("manhwa", [new DeclaredCreator("A Name", "artist")]);
        Assert.False(facts.IsEmpty);
        Assert.Equal(DeclaredType.Manhwa, facts.TypeValue);
        Assert.Null(new DeclaredFacts("future-kind", []).TypeValue);
    }
}
