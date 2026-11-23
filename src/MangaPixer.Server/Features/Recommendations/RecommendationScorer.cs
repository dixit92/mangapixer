namespace com.lifepixer.mangapixer.Server.Features.Recommendations;

using com.lifepixer.mangapixer.Core.Metadata.Facets;

/// <summary>
/// The scoring of the recommender (1.53.0 lane C) - pure and deterministic, over a <see cref="RecommendationIndex"/>:
/// <list type="number">
/// <item>Each seed is a set of values (a work's own, or one location's) with a weight (negative for a disliked series); its vector is
/// normalised, so a series with many tags does not outweigh one with few.</item>
/// <item>The profile is the weighted sum of the seed vectors.</item>
/// <item>A candidate's score is the cosine of its vector with the profile, plus the relations bonus: +<see cref="RelationBonus"/> x the
/// seed's weight when its linked record and a positive seed's record name each other (either direction; the largest one counts, not
/// the sum).</item>
/// <item>Picks score at least <see cref="Floor"/>; ties go to the lower work id.</item>
/// </list>
/// Each pick carries its <see cref="ReasonSpec"/>: the largest contributor - the relation when its bonus is at least the cosine part,
/// else the top shared value: a creator gives "Same author", anything else the seed sharing the most with the pick and the top two
/// non-creator values they share.
/// 1.54.0 (community scores, owner q5 "Yes"): a candidate's community score (<see cref="RecommendationIndex.CommunityScores"/>) is a weak
/// QUALITY PRIOR - <c>score x (1 + <see cref="QualityPriorWeight"/> x q)</c>, q the score's distance from the candidates' median in standard
/// deviations, clipped to -1..1 (0 when unknown) - so it breaks near-ties and never lifts a poor taste match (it multiplies). And a
/// COLD START (<see cref="ColdStart"/>): without seeds, the best-rated candidates, "Well rated on MangaUpdates: 9.0".
/// </summary>
public static class RecommendationScorer
{
    /// <summary>1.54.0: the most the community score moves a pick's score (+/- 15 %).</summary>
    public const double QualityPriorWeight = 0.15;

    /// <summary>1.54.0: below this many scored candidates (or with no spread) there is no prior.</summary>
    public const int QualityPriorMinCandidates = 3;

    /// <summary>The lowest score a pick may have (below it the row would show noise).</summary>
    public const double Floor = 0.05;

    /// <summary>The relations bonus per unit of seed weight.</summary>
    public const double RelationBonus = 0.5;

    /// <summary>The most picks any answer holds.</summary>
    public const int MaxLimit = 50;

    /// <summary>
    /// One seed: <paramref name="Values"/> (indices of <see cref="RecommendationIndex.ValueIds"/>), its weight, its linked record (for the
    /// relations bonus; null = none) and its work in the index (null = not in this scope).
    /// </summary>
    public sealed record Seed(IReadOnlyList<int> Values, double Weight, long? RecordId, int? Work);

    public enum ReasonKind { Creator, SharedValues, Relation, CommunityScore }

    /// <summary>
    /// Why a pick appears: the seed it names (index into the seed list; null for a creator), the values (value ids) it names, and for a
    /// relation the relation word and its direction.
    /// </summary>
    public sealed record ReasonSpec(ReasonKind Kind, int? SeedIndex, IReadOnlyList<long> ValueIds, string? Relation = null, bool SeedListsPick = false,
        double? CommunityScore = null, string? CommunitySource = null);

    public sealed record Pick(int Work, double Score, ReasonSpec Reason);

    /// <summary>
    /// Scores every work of the index that <paramref name="isCandidate"/> admits and answers the best <paramref name="limit"/> at or
    /// above <see cref="Floor"/>.
    /// </summary>
    public static IReadOnlyList<Pick> Score(RecommendationIndex index, IReadOnlyList<Seed> seeds, Func<int, bool> isCandidate, int limit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        if (index.WorkCount == 0 || seeds.Count == 0)
            return [];

        // Normalised seed vectors and the profile.
        var seedVectors = new Dictionary<int, double>[seeds.Count];
        var profile = new Dictionary<int, double>();
        for (var s = 0; s < seeds.Count; s++)
        {
            var values = seeds[s].Values.Distinct().Order().ToArray();
            var norm = Math.Sqrt(values.Sum(v => index.ValueWeights[v] * index.ValueWeights[v]));
            var vector = new Dictionary<int, double>();
            if (norm > 0)
            {
                foreach (var v in values)
                {
                    var x = index.ValueWeights[v] / norm;
                    vector[v] = x;
                    profile[v] = profile.GetValueOrDefault(v) + seeds[s].Weight * x;
                }
            }
            seedVectors[s] = vector;
        }
        var profileNorm = Math.Sqrt(profile.Values.Sum(x => x * x));

        // Dot products over the profile's values only (the postings), in value order (deterministic sums); an array per work.
        var dot = new double[index.WorkCount];
        var reached = new bool[index.WorkCount];
        var reachedList = new List<int>();
        if (profileNorm > 1e-12)
        {
            foreach (var (v, p) in profile.OrderBy(kv => kv.Key))
            {
                if (Math.Abs(p) < 1e-12)
                    continue;
                var contribution = p * index.ValueWeights[v];
                foreach (var w in index.ValueWorks[v])
                {
                    dot[w] += contribution;
                    if (!reached[w])
                    {
                        reached[w] = true;
                        reachedList.Add(w);
                    }
                }
            }
        }

        // Relations bonus from the positive seeds with a record.
        var bonus = new Dictionary<int, (double Value, int Seed, RecommendationIndex.Edge Edge)>();
        for (var s = 0; s < seeds.Count; s++)
        {
            if (seeds[s].Weight <= 0 || seeds[s].RecordId is not { } record)
                continue;
            var value = RelationBonus * seeds[s].Weight;
            foreach (var edge in index.EdgesOf(record))
            {
                if (!bonus.TryGetValue(edge.OtherWork, out var existing) || existing.Value < value - 1e-12)
                    bonus[edge.OtherWork] = (value, s, edge);
            }
        }

        var prior = QualityPrior(index, isCandidate);
        var scored = new List<(int Work, double Score, double Cosine)>();
        foreach (var w in reachedList.Union(bonus.Keys))
        {
            if (!isCandidate(w))
                continue;
            var cosine = reached[w] && index.WorkNorms[w] > 0 ? dot[w] / (profileNorm * index.WorkNorms[w]) : 0;
            var total = (cosine + (bonus.TryGetValue(w, out var b) ? b.Value : 0)) * (1 + QualityPriorWeight * prior(w));
            if (total >= Floor)
                scored.Add((w, total, cosine));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => index.WorkIds[x.Work])
            .Take(limit)
            .Select(x => new Pick(x.Work, x.Score, ReasonOf(index, seeds, seedVectors, profile, x.Work, x.Cosine,
                bonus.TryGetValue(x.Work, out var b) ? b : null)))
            .ToList();
    }

    private static ReasonSpec ReasonOf(
        RecommendationIndex index, IReadOnlyList<Seed> seeds, Dictionary<int, double>[] seedVectors, Dictionary<int, double> profile,
        int work, double cosine, (double Value, int Seed, RecommendationIndex.Edge Edge)? bonus)
    {
        if (bonus is { } b && b.Value >= cosine)
            return new ReasonSpec(ReasonKind.Relation, b.Seed, [], b.Edge.Relation, SeedListsPick: b.Edge.RecordListsOther);

        // The shared values with a positive profile weight, by contribution (then value id).
        var shared = index.WorkValues[work]
            .Where(v => profile.TryGetValue(v, out var p) && p > 0)
            .Select(v => (Value: v, Contribution: profile[v] * index.ValueWeights[v]))
            .OrderByDescending(x => x.Contribution)
            .ThenBy(x => index.ValueIds[x.Value])
            .ToList();
        if (shared.Count == 0)
            return new ReasonSpec(ReasonKind.SharedValues, null, []); // unreachable above the floor; kept total

        var top = shared[0].Value;
        if (index.ValueBuckets[top] == FacetBucket.Creators)
            return new ReasonSpec(ReasonKind.Creator, null, [index.ValueIds[top]]);

        // The positive seed that shares the most with the pick, then its top two non-creator values.
        var candidateValues = index.WorkValues[work];
        var bestSeed = -1;
        var bestShare = 0.0;
        for (var s = 0; s < seeds.Count; s++)
        {
            if (seeds[s].Weight <= 0)
                continue;
            var share = 0.0;
            foreach (var v in candidateValues)
            {
                if (seedVectors[s].TryGetValue(v, out var x))
                    share += seeds[s].Weight * x * index.ValueWeights[v];
            }
            if (share > bestShare + 1e-12)
            {
                bestShare = share;
                bestSeed = s;
            }
        }
        if (bestSeed < 0)
            return new ReasonSpec(ReasonKind.SharedValues, null, shared.Take(2).Select(x => index.ValueIds[x.Value]).ToList());

        var named = candidateValues
            .Where(v => seedVectors[bestSeed].ContainsKey(v) && index.ValueBuckets[v] != FacetBucket.Creators)
            .Select(v => (Value: v, Contribution: seedVectors[bestSeed][v] * index.ValueWeights[v]))
            .OrderByDescending(x => x.Contribution)
            .ThenBy(x => index.ValueIds[x.Value])
            .Take(2)
            .Select(x => index.ValueIds[x.Value])
            .ToList();
        if (named.Count == 0)
        {
            // Only creators shared with that seed: say so.
            var creator = shared.FirstOrDefault(x => index.ValueBuckets[x.Value] == FacetBucket.Creators, shared[0]).Value;
            return index.ValueBuckets[creator] == FacetBucket.Creators
                ? new ReasonSpec(ReasonKind.Creator, null, [index.ValueIds[creator]])
                : new ReasonSpec(ReasonKind.SharedValues, null, shared.Take(2).Select(x => index.ValueIds[x.Value]).ToList());
        }
        return new ReasonSpec(ReasonKind.SharedValues, bestSeed, named);
    }

    /// <summary>
    /// The words of a relation reason, from the PICK's side: "Sequel of" when the seed lists the pick as its sequel, or the pick lists the
    /// seed as its prequel; "Related to" for any relation without a clear reading.
    /// </summary>
    /// <summary>
    /// 1.54.0: q of a candidate - its community score's distance from the median of the scored candidates, in standard deviations, clipped
    /// to -1..1; 0 without a score, or when fewer than <see cref="QualityPriorMinCandidates"/> candidates have one or they barely differ.
    /// </summary>
    public static Func<int, double> QualityPrior(RecommendationIndex index, Func<int, bool> isCandidate)
    {
        var scores = new List<double>();
        for (var w = 0; w < index.CommunityScores.Length; w++)
        {
            if (index.CommunityScores[w] is { } s && isCandidate(w))
                scores.Add(s);
        }
        if (scores.Count < QualityPriorMinCandidates)
            return _ => 0;
        scores.Sort();
        var median = scores.Count % 2 == 1 ? scores[scores.Count / 2] : (scores[(scores.Count / 2) - 1] + scores[scores.Count / 2]) / 2;
        var mean = scores.Average();
        var spread = Math.Sqrt(scores.Sum(s => (s - mean) * (s - mean)) / scores.Count);
        if (spread < 1e-6)
            return _ => 0;
        return w => w < index.CommunityScores.Length && index.CommunityScores[w] is { } s ? Math.Clamp((s - median) / spread, -1, 1) : 0;
    }

    /// <summary>
    /// 1.54.0 (the cold start): the candidates with a community score, best first (ties to the lower work id), at most
    /// <paramref name="limit"/>, each with the <see cref="ReasonKind.CommunityScore"/> reason. Empty when no candidate has a score.
    /// </summary>
    public static IReadOnlyList<Pick> ColdStart(RecommendationIndex index, Func<int, bool> isCandidate, int limit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var picks = new List<(int Work, double Score)>();
        for (var w = 0; w < index.CommunityScores.Length; w++)
        {
            if (index.CommunityScores[w] is { } s && isCandidate(w))
                picks.Add((w, s));
        }
        return picks
            .OrderByDescending(p => p.Score)
            .ThenBy(p => index.WorkIds[p.Work])
            .Take(limit)
            .Select(p => new Pick(p.Work, p.Score, new ReasonSpec(ReasonKind.CommunityScore, null, [],
                CommunityScore: p.Score, CommunitySource: index.CommunityScoreSources[p.Work] ?? "the web")))
            .ToList();
    }

    public static string RelationWords(string relation, bool seedListsPick)
    {
        var r = relation.Trim().ToLowerInvariant();
        if (seedListsPick)
        {
            return r switch
            {
                "sequel" => "Sequel of",
                "prequel" => "Prequel of",
                "side story" => "Side story of",
                "main story" => "Main story of",
                "spin-off" or "spin off" => "Spin-off of",
                _ => "Related to",
            };
        }
        return r switch
        {
            "prequel" => "Sequel of",
            "sequel" => "Prequel of",
            "main story" => "Side story of",
            "side story" => "Main story of",
            _ => "Related to",
        };
    }
}
