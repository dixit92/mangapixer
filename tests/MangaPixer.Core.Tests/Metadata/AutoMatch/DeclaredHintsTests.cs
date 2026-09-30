namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// Unit tests for <see cref="DeclaredHints"/> (1.28.0): declared facts folded into a query; the declared type a strong hint both ways
/// since 1.30.0 (never a veto), declared creators positive-only. Synthetic titles.
/// </summary>
public sealed class DeclaredHintsTests
{
    private static MatchQuery Query(string? category = null, IReadOnlyList<string>? hints = null) =>
        new([new QueryVariant("Sprout", QueryVariantKind.Primary)],
            new MatchContext(WorkClass.Series, 3, 3, 0, null, category, false, ["Tag Person"], CreatorHints: hints));

    [Theory]
    [InlineData(DeclaredType.Manga)]
    [InlineData(DeclaredType.Webtoon)]
    [InlineData(DeclaredType.Comic)]
    [InlineData(DeclaredType.Novel)]
    public void DeclaredType_JoinsTheContext_AndLeavesTheFolderWordAlone(DeclaredType type)
    {
        var q = DeclaredHints.Apply(Query(category: "manhwa"), new DeclaredFacts(DeclaredFactKeys.TypeSlug(type), []));

        Assert.Equal(type, q.Context.DeclaredType);
        Assert.Equal("manhwa", q.Context.CategoryHint); // kept, but the scorer reads the declaration instead (1.30.0)
    }

    private static MatchCandidate Record(string id, string title, string type, bool? webtoon = null) =>
        new("mangaupdates", id, title, [], MetadataFormat.Comic, type, null, null, null, [], [], webtoon);

    private static MatchQuery Declared(DeclaredType type, string? category = null) =>
        DeclaredHints.Apply(Query(category), new DeclaredFacts(DeclaredFactKeys.TypeSlug(type), []));

    [Theory]
    [InlineData(DeclaredType.Manga, "2")]
    [InlineData(DeclaredType.Manhwa, "1")]
    [InlineData(DeclaredType.Manhua, "3")]
    public void ADeclaredType_SettlesATitleTie_BetweenOrigins(DeclaredType type, string expected)
    {
        // Same title, three origins (1.30.0, owner: a strong hint). The fitting record gains, the others lose - by exactly the margin.
        MatchCandidate[] records = [Record("1", "Sprout", "Manhwa"), Record("2", "Sprout", "Manga"), Record("3", "Sprout", "Manhua")];

        var outcome = new MatchScorer().Score(Declared(type), records, MatchThresholds.Default);

        Assert.Equal(expected, outcome.Ranked[0].Candidate.ExternalId);
        Assert.Equal(MatchBand.Auto, outcome.Band);
        Assert.True(outcome.Ranked[0].Reasons.HasFlag(MatchReason.DeclaredTypeAgree));
        Assert.True(outcome.Ranked[1].Reasons.HasFlag(MatchReason.DeclaredTypeMismatch));
        Assert.Equal(MatchScorer.DeclaredTypeAgree - MatchScorer.DeclaredTypeMismatch, outcome.Ranked[0].AdjustedScore - outcome.Ranked[1].AdjustedScore, 6);
    }

    [Fact]
    public void WithoutADeclaration_AnOriginTieStaysInReview()
    {
        MatchCandidate[] records = [Record("1", "Sprout", "Manhwa"), Record("2", "Sprout", "Manga")];

        Assert.Equal(MatchBand.NeedsReview, new MatchScorer().Score(Query(), records, MatchThresholds.Default).Band);
    }

    [Fact]
    public void AWrongDeclaration_IsNeverAVeto_AndNeverBeatsABetterTitle()
    {
        // The folder is a manhwa declared manga by mistake: the right record keeps its lead on the title.
        var only = new MatchScorer().Score(Declared(DeclaredType.Manga), [Record("1", "Sprout", "Manhwa")], MatchThresholds.Default);
        Assert.Equal(MatchBand.Auto, only.Band);
        Assert.Equal(0, (int)(only.Ranked[0].Reasons & MatchScorer.VetoReasons));

        var lookalike = new MatchScorer().Score(Declared(DeclaredType.Manga),
            [Record("1", "Sprout", "Manhwa"), Record("2", "Sprouts", "Manga")], MatchThresholds.Default);
        Assert.NotEqual((MatchBand.Auto, "2"), (lookalike.Band, lookalike.Ranked[0].Candidate.ExternalId));
    }

    [Theory]
    [InlineData(DeclaredType.Comic, "OEL", MatchReason.DeclaredTypeAgree)]
    [InlineData(DeclaredType.GraphicNovel, "French", MatchReason.DeclaredTypeAgree)]
    [InlineData(DeclaredType.Comic, "Manga", MatchReason.DeclaredTypeMismatch)]
    [InlineData(DeclaredType.Webtoon, "Manhwa", MatchReason.DeclaredTypeAgree)]
    [InlineData(DeclaredType.Webtoon, "Manga", MatchReason.None)]
    [InlineData(DeclaredType.Novel, "Manga", MatchReason.None)]
    [InlineData(DeclaredType.Manga, "Doujinshi", MatchReason.None)]
    public void TheEvidence_PerDeclaredType(DeclaredType type, string recordType, MatchReason expected)
    {
        var outcome = new MatchScorer().Score(Declared(type), [Record("1", "Sprout", recordType)], MatchThresholds.Default);

        Assert.Equal(expected, outcome.Ranked[0].Reasons & (MatchReason.DeclaredTypeAgree | MatchReason.DeclaredTypeMismatch));
    }

    [Fact]
    public void ADeclaration_TakesThePlaceOfTheCategoryFolder()
    {
        // A "Manhwa" category folder alone prefers the Korean record by +0.02; a declared manga wins over it.
        MatchCandidate[] records = [Record("1", "Sprout", "Manhwa"), Record("2", "Sprout", "Manga")];

        var folderOnly = new MatchScorer().Score(Query("manhwa"), records, MatchThresholds.Default);
        var declared = new MatchScorer().Score(Declared(DeclaredType.Manga, "manhwa"), records, MatchThresholds.Default);

        Assert.Equal("1", folderOnly.Ranked[0].Candidate.ExternalId);
        Assert.Equal(MatchBand.NeedsReview, folderOnly.Band); // agreement only, +0.02
        Assert.Equal("2", declared.Ranked[0].Candidate.ExternalId);
        Assert.Equal(MatchBand.Auto, declared.Band);
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
    public void ADeclaredAuthor_NamesTheDisambiguatedRecord_AndAWrongTypeIsNoVeto()
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
        Assert.True(declared.Ranked[0].Reasons.HasFlag(MatchReason.DeclaredTypeMismatch)); // both records lose the same 0.05
    }
}
