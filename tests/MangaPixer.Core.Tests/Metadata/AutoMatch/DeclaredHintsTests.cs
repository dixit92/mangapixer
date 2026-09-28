namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>Unit tests for <see cref="DeclaredHints"/> (1.28.0): declared facts folded into a query, positive-only.</summary>
public sealed class DeclaredHintsTests
{
    private static MatchQuery Query(string? category = null, IReadOnlyList<string>? hints = null) =>
        new([new QueryVariant("Sprout", QueryVariantKind.Primary)],
            new MatchContext(WorkClass.Series, 3, 3, 0, null, category, false, ["Tag Person"], CreatorHints: hints));

    [Theory]
    [InlineData(DeclaredType.Manga, "manga")]
    [InlineData(DeclaredType.Manhwa, "manhwa")]
    [InlineData(DeclaredType.Manhua, "manhua")]
    [InlineData(DeclaredType.Webtoon, "webtoon")]
    [InlineData(DeclaredType.Comic, "manga")]        // no origin to agree with: the folder's own hint stays
    [InlineData(DeclaredType.GraphicNovel, "manga")]
    [InlineData(DeclaredType.Novel, "manga")]
    public void DeclaredType_IsTheCategoryHint_WinningOverAFolderWord(DeclaredType type, string expected)
    {
        var q = DeclaredHints.Apply(Query(category: "manga"), new DeclaredFacts(DeclaredFactKeys.TypeSlug(type), []));

        Assert.Equal(expected, q.Context.CategoryHint);
    }

    [Fact]
    public void DeclaredCreators_JoinTheCreatorHints_Deduplicated_NeverTheAuthorTags()
    {
        var q = DeclaredHints.Apply(Query(hints: ["Given Family"]),
            new DeclaredFacts(null, [new DeclaredCreator("Family Given", "author"), new DeclaredCreator("Other Person", null)]));

        Assert.Equal(["Given Family", "Other Person"], q.Context.CreatorHints);
        Assert.Equal(["Tag Person"], q.Context.AuthorTags); // tags can veto; declarations never do
    }

    [Fact]
    public void NothingDeclared_ReturnsTheSameQuery()
    {
        var q = Query();

        Assert.Same(q, DeclaredHints.Apply(q, null));
        Assert.Same(q, DeclaredHints.Apply(q, DeclaredFacts.Empty));
    }

    [Fact]
    public void ADeclaredAuthor_NamesTheDisambiguatedRecord_AndAWrongTypeCostsNothing()
    {
        var scorer = new MatchScorer();
        MatchCandidate[] records =
        [
            new("mangaupdates", "1", "Sprout (ALPHA Writer)", [], MetadataFormat.Comic, "Manga", null, null, null, [], []),
            new("mangaupdates", "2", "Sprout (BETA Painter)", [], MetadataFormat.Comic, "Manga", null, null, null, [], []),
        ];
        var plain = scorer.Score(Query(), records, MatchThresholds.Default);
        var declared = scorer.Score(DeclaredHints.Apply(Query(), new DeclaredFacts("manhwa", [new DeclaredCreator("Beta Painter", null)])),
            records, MatchThresholds.Default);

        Assert.Equal(MatchBand.NeedsReview, plain.Band);
        Assert.Equal("2", declared.Ranked[0].Candidate.ExternalId);
        Assert.Equal(MatchBand.Auto, declared.Band);                       // +0.10 on a raw 1.00 tie
        Assert.Equal(0, (int)(declared.Ranked[0].Reasons & MatchScorer.VetoReasons)); // the manhwa declaration vetoes nothing
    }
}
