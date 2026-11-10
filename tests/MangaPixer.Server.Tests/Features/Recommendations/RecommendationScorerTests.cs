namespace com.lifepixer.mangapixer.Tests.Server.Features.Recommendations;

using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Recommendations;
using Xunit;

/// <summary>
/// 1.53.0 lane C: the recommender's scoring, deterministic and in memory (unit). Synthetic index of six works:
/// <code>
///   creator C1           W1 W2
///   tag Horror (T1)      W1 W3 W4
///   tag School Life (T2) W1 W3
///   genre Action (G1)    W1 W2 W3 W4 W5 W6   (every work: idf 0, dropped)
///   genre Drama (G2)     W5 W6
///   record R1 (W1) lists R6 (W6) as its "sequel"
/// </code>
/// With W1 as the only seed: W2 0.861 (same author), W3 0.509 (Horror + School Life), W6 0.500 (the relation bonus only), W4 0.272
/// (Horror), W5 nothing in common.
/// </summary>
public sealed class RecommendationScorerTests
{
    private const long C1 = 101, T1 = 110, T2 = 111, G1 = 160, G2 = 161;
    private const long R1 = 9001, R6 = 9006;

    private static readonly (long Work, long Value, FacetBucket Bucket)[] Rows =
    [
        (1, C1, FacetBucket.Creators), (2, C1, FacetBucket.Creators),
        (1, T1, FacetBucket.Tags), (3, T1, FacetBucket.Tags), (4, T1, FacetBucket.Tags),
        (1, T2, FacetBucket.Tags), (3, T2, FacetBucket.Tags),
        (1, G1, FacetBucket.Genres), (2, G1, FacetBucket.Genres), (3, G1, FacetBucket.Genres), (4, G1, FacetBucket.Genres),
        (5, G1, FacetBucket.Genres), (6, G1, FacetBucket.Genres),
        (5, G2, FacetBucket.Genres), (6, G2, FacetBucket.Genres),
    ];

    private static RecommendationIndex Index(bool shuffle = false, bool incoming = false)
    {
        var entries = Rows.Select(r => new RecommendationIndex.EntryRow(r.Work, 1000 + r.Work, 1, r.Value, (int)r.Bucket)).ToList();
        if (shuffle)
            entries.Reverse();
        var locations = Enumerable.Range(1, 6).Select(w => new RecommendationIndex.LocationRow(w, 1000 + w, 1, "Lib", "w" + w));
        var records = incoming
            ? new[]
            {
                new RecommendationIndex.RecordRow(1, R1, []),
                new RecommendationIndex.RecordRow(6, R6, [(R1, "prequel")]),
            }
            : new[]
            {
                new RecommendationIndex.RecordRow(1, R1, [(R6, "sequel")]),
                new RecommendationIndex.RecordRow(6, R6, []),
            };
        return RecommendationIndex.Build(entries, locations, records);
    }

    private static RecommendationScorer.Seed SeedOf(RecommendationIndex index, long workId, double weight)
    {
        Assert.True(index.TryWork(workId, out var w));
        return new RecommendationScorer.Seed(index.WorkValues[w], weight, index.RecordIds[w], w);
    }

    private static long WorkId(RecommendationIndex index, RecommendationScorer.Pick pick) => index.WorkIds[pick.Work];

    [Fact]
    public void Index_DropsValuesEveryWorkCarries_AndWeighsByBucketAndRarity()
    {
        var index = Index();
        Assert.Equal(6, index.ScopeWorkCount);
        Assert.False(index.TryValue(G1, out _)); // on all six works: ln(6/6) = 0
        Assert.True(index.TryValue(C1, out var c1));
        Assert.True(index.TryValue(T2, out var t2));
        Assert.Equal(3.0 * Math.Log(6.0 / 2), index.ValueWeights[c1], 9);
        Assert.Equal(1.5 * Math.Log(6.0 / 2), index.ValueWeights[t2], 9);
        Assert.Equal(0.0, RecommendationIndex.BucketWeight(FacetBucket.Year));
        Assert.Equal(0.0, RecommendationIndex.BucketWeight(FacetBucket.Series));
    }

    [Fact]
    public void OneSeed_RanksByCosinePlusRelationBonus_WithOneReasonEach()
    {
        var index = Index();
        var seeds = new[] { SeedOf(index, 1, 1.0) };
        Assert.True(index.TryWork(1, out var seedWork));
        var picks = RecommendationScorer.Score(index, seeds, w => w != seedWork, 12);

        Assert.Equal([2L, 3, 6, 4], picks.Select(p => WorkId(index, p)));
        Assert.Equal(0.861, picks[0].Score, 3);
        Assert.Equal(0.509, picks[1].Score, 3);
        Assert.Equal(0.500, picks[2].Score, 3);
        Assert.Equal(0.272, picks[3].Score, 3);

        Assert.Equal(RecommendationScorer.ReasonKind.Creator, picks[0].Reason.Kind);
        Assert.Equal([C1], picks[0].Reason.ValueIds);
        Assert.Equal(RecommendationScorer.ReasonKind.SharedValues, picks[1].Reason.Kind);
        Assert.Equal(0, picks[1].Reason.SeedIndex);
        Assert.Equal([T2, T1], picks[1].Reason.ValueIds); // School Life is rarer, so it contributes more
        Assert.Equal(RecommendationScorer.ReasonKind.Relation, picks[2].Reason.Kind);
        Assert.Equal("sequel", picks[2].Reason.Relation);
        Assert.True(picks[2].Reason.SeedListsPick);
        Assert.Equal("Sequel of", RecommendationScorer.RelationWords(picks[2].Reason.Relation!, picks[2].Reason.SeedListsPick));
        Assert.Equal([T1], picks[3].Reason.ValueIds);
    }

    [Fact]
    public void Relation_CountsInBothDirections()
    {
        var index = Index(incoming: true); // W6's record lists W1's as its prequel
        Assert.True(index.TryWork(1, out var seedWork));
        var picks = RecommendationScorer.Score(index, [SeedOf(index, 1, 1.0)], w => w != seedWork, 12);
        var relation = Assert.Single(picks, p => WorkId(index, p) == 6);
        Assert.Equal(RecommendationScorer.ReasonKind.Relation, relation.Reason.Kind);
        Assert.False(relation.Reason.SeedListsPick);
        Assert.Equal("Sequel of", RecommendationScorer.RelationWords("prequel", seedListsPick: false));
    }

    [Fact]
    public void NegativeSeed_CountsAgainstWhatItShares()
    {
        var index = Index();
        Assert.True(index.TryWork(1, out var w1));
        Assert.True(index.TryWork(3, out var w3));
        var liked = RecommendationScorer.Score(index, [SeedOf(index, 1, 1.0)], w => w != w1 && w != w3, 12);
        Assert.Contains(liked, p => WorkId(index, p) == 4);

        // W3 (Horror + School Life) rated low: Horror now counts for almost nothing, so W4 falls below the floor; the author still counts.
        var mixed = RecommendationScorer.Score(index, [SeedOf(index, 1, 1.0), SeedOf(index, 3, -0.5)], w => w != w1 && w != w3, 12);
        Assert.DoesNotContain(mixed, p => WorkId(index, p) == 4);
        Assert.Equal(2, WorkId(index, mixed[0]));
    }

    [Fact]
    public void OnlyNegativeSeeds_PickNothing_AndNothingInCommon_IsBelowTheFloor()
    {
        var index = Index();
        Assert.Empty(RecommendationScorer.Score(index, [SeedOf(index, 1, -0.5)], _ => true, 12));
        Assert.True(index.TryWork(5, out var w5));
        var picks = RecommendationScorer.Score(index, [SeedOf(index, 5, 1.0)], w => w != w5, 12);
        Assert.Equal([6L], picks.Select(p => WorkId(index, p))); // Drama only; nothing else shares a weighted value
    }

    [Fact]
    public void Scores_AreDeterministic_WhateverTheRowOrder_AndHonourCandidatesAndLimit()
    {
        var a = Index();
        var b = Index(shuffle: true);
        Assert.True(a.TryWork(1, out var wa));
        Assert.True(b.TryWork(1, out var wb));
        var pa = RecommendationScorer.Score(a, [SeedOf(a, 1, 1.0)], w => w != wa, 12);
        var pb = RecommendationScorer.Score(b, [SeedOf(b, 1, 1.0)], w => w != wb, 12);
        Assert.Equal(pa.Select(p => (WorkId(a, p), Math.Round(p.Score, 12))), pb.Select(p => (WorkId(b, p), Math.Round(p.Score, 12))));

        Assert.True(a.TryWork(2, out var w2));
        var without = RecommendationScorer.Score(a, [SeedOf(a, 1, 1.0)], w => w != wa && w != w2, 2);
        Assert.Equal([3L, 6], without.Select(p => WorkId(a, p)));
    }

    [Fact]
    public void Ties_GoToTheLowerWorkId()
    {
        var entries = new[] { (1L, C1), (7L, C1), (8L, C1), (9L, T1) }
            .Select(r => new RecommendationIndex.EntryRow(r.Item1, 2000 + r.Item1, 1, r.Item2, r.Item2 == C1 ? (int)FacetBucket.Creators : (int)FacetBucket.Tags));
        var index = RecommendationIndex.Build(entries, [], []);
        Assert.True(index.TryWork(1, out var w1));
        var picks = RecommendationScorer.Score(index, [SeedOf(index, 1, 1.0)], w => w != w1, 12);
        Assert.Equal([7L, 8], picks.Select(p => WorkId(index, p)));
        Assert.Equal(picks[0].Score, picks[1].Score, 12);
    }

    [Theory]
    [InlineData(10, 1.0)]
    [InlineData(9, 1.0)]
    [InlineData(8, 0.6)]
    [InlineData(7, 0.6)]
    [InlineData(6, null)]
    [InlineData(5, null)]
    [InlineData(4, -0.5)]
    [InlineData(1, -0.5)]
    public void RatingWeights_FollowTheDesign(int score, double? weight) =>
        Assert.Equal(weight, RecommendationService.RatingWeight(score));

    [Theory]
    [InlineData("sequel", true, "Sequel of")]
    [InlineData("side story", true, "Side story of")]
    [InlineData("spin-off", true, "Spin-off of")]
    [InlineData("main story", false, "Side story of")]
    [InlineData("sequel", false, "Prequel of")]
    [InlineData("adapted from", true, "Related to")]
    [InlineData("doujinshi", false, "Related to")]
    public void RelationWords_ReadFromThePicksSide(string relation, bool seedListsPick, string words) =>
        Assert.Equal(words, RecommendationScorer.RelationWords(relation, seedListsPick));
}
