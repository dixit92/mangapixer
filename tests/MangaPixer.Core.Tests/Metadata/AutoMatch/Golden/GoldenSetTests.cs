namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch.Golden;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The stage-2 golden set: every case runs the real detector, planner and scorer over RECORDED
/// MangaUpdates responses (no network) through a model of the server's retrieval loop - search
/// the variants in order (at most <see cref="MaxSearches"/>, the next only while the best title
/// score is below <see cref="NextVariantBelow"/>), GET the top candidate (and the second when it is
/// within <see cref="SecondGetWithin"/>), score with the default thresholds. Asserts the class, band
/// and chosen id per case; <see cref="Report_AutoRateAndPrecision"/> prints the aggregate numbers so a
/// threshold change shows its effect in CI.
/// </summary>
public sealed class GoldenSetTests(ITestOutputHelper output)
{
    public const int MaxSearches = 4;
    public const double NextVariantBelow = 0.85;
    public const double SecondGetWithin = 0.10;

    private static readonly WorkDetector s_detector = new();
    private static readonly MatchQueryPlanner s_planner = new();
    private static readonly MatchScorer s_scorer = new();

    public static TheoryData<GoldenCase> Cases()
    {
        var data = new TheoryData<GoldenCase>();
        foreach (var c in GoldenCases.All)
            data.Add(c);
        return data;
    }

    public sealed record Run(WorkClassification Classification, MatchOutcome? Outcome, IReadOnlyList<string> Missing, int Searches, int Gets);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(GoldenCase c)
    {
        var run = Execute(c, MatchThresholds.Default);

        Assert.True(run.Missing.Count == 0, "Missing fixtures:\n" + string.Join('\n', run.Missing));
        if (c.Class is { } cls)
            Assert.Equal(cls, run.Classification.Class);
        if (c.Content is { } content)
            Assert.Equal(content, run.Classification.ContentSuggestion);
        if (c.Band is not { } band)
            return;

        var outcome = run.Outcome!;
        var top = outcome.Ranked.Count > 0 ? outcome.Ranked[0] : null;
        var detail = top is null
            ? "no candidates"
            : FormattableString.Invariant($"top {top.Candidate.ExternalId} '{top.Candidate.Title}' title {top.TitleScore:0.000} adj {top.AdjustedScore:0.000} [{top.Reasons}]")
                + (outcome.Ranked.Count > 1
                    ? FormattableString.Invariant($"; 2nd {outcome.Ranked[1].Candidate.ExternalId} '{outcome.Ranked[1].Candidate.Title}' adj {outcome.Ranked[1].AdjustedScore:0.000}")
                    : string.Empty);
        Assert.True(band == outcome.Band, $"band {outcome.Band}, expected {band}: {detail}");
        if (c.Vetoes is { } vetoes)
            Assert.Equal(vetoes, top!.Reasons & MatchScorer.VetoReasons);
        if (c.ExpectedId is { } id)
            Assert.True(top?.Candidate.ExternalId == id, $"chosen {top?.Candidate.ExternalId}, expected {id}: {detail}");
    }

    [Fact]
    public void Report_AutoRateAndPrecision()
    {
        // Per case at the default thresholds, then the aggregate at the default and at both ends
        // of the admin-adjustable bounds (decision 13), so a threshold change shows its effect.
        foreach (var c in GoldenCases.All.Where(c => c.Band is not null))
        {
            var o = Execute(c, MatchThresholds.Default).Outcome;
            var top = o?.Ranked.FirstOrDefault();
            output.WriteLine(FormattableString.Invariant(
                $"{c.Id}: {o?.Band} {top?.Candidate.ExternalId} title {top?.TitleScore:0.000} adj {top?.AdjustedScore:0.000} [{top?.Reasons}]"));
        }

        var loosest = new MatchThresholds(MatchThresholds.AutoTitleMin, MatchThresholds.MarginMin, MatchThresholds.ReviewFloorMin);
        var strictest = new MatchThresholds(MatchThresholds.AutoTitleMax, MatchThresholds.MarginMax, MatchThresholds.ReviewFloorMax);
        (string Name, MatchThresholds Thresholds)[] sweep = [("default", MatchThresholds.Default), ("loosest", loosest), ("strictest", strictest)];
        foreach (var (name, thresholds) in sweep)
        {
            var (report, auto, autoCorrect) = Aggregate(thresholds);
            output.WriteLine($"GOLDEN {name}: {report}");
            Console.WriteLine($"GOLDEN {name}: {report}");

            // Wrong auto links are the costly failure: at the defaults every auto link must be right.
            if (name == "default")
                Assert.Equal(auto, autoCorrect);
        }
    }

    private static (string Report, int Auto, int AutoCorrect) Aggregate(MatchThresholds thresholds)
    {
        int matched = 0, auto = 0, autoCorrect = 0, review = 0, reviewTopCorrect = 0, reviewWithExpected = 0, unmatched = 0, searches = 0, gets = 0;
        foreach (var c in GoldenCases.All.Where(c => c.Band is not null))
        {
            var run = Execute(c, thresholds);
            if (run.Outcome is not { } o) continue;
            matched++;
            searches += run.Searches;
            gets += run.Gets;
            var topId = o.Ranked.Count > 0 ? o.Ranked[0].Candidate.ExternalId : null;
            switch (o.Band)
            {
                case MatchBand.Auto:
                    auto++;
                    if (c.ExpectedId is not null && topId == c.ExpectedId) autoCorrect++;
                    break;
                case MatchBand.NeedsReview:
                    review++;
                    if (c.ExpectedId is not null)
                    {
                        reviewWithExpected++;
                        if (topId == c.ExpectedId) reviewTopCorrect++;
                    }
                    break;
                default:
                    unmatched++;
                    break;
            }
        }

        var report = FormattableString.Invariant(
            $"matched cases {matched}: auto {auto} ({100.0 * auto / matched:0.0}%), review {review}, unmatched {unmatched}; auto precision {autoCorrect}/{auto} ({(auto == 0 ? 0 : 100.0 * autoCorrect / auto):0.0}%); review top correct {reviewTopCorrect}/{reviewWithExpected}; requests {searches} searches + {gets} GETs ({(double)(searches + gets) / matched:0.00} per work); fixtures {GoldenFixtures.SearchCount} searches, {GoldenFixtures.SeriesCount} series");
        return (report, auto, autoCorrect);
    }

    public static Run Execute(GoldenCase c) => Execute(c, MatchThresholds.Default);

    public static Run Execute(GoldenCase c, MatchThresholds thresholds)
    {
        var classification = s_detector.Classify(c.Folder);
        if (c.Band is null)
            return new Run(classification, null, [], 0, 0);

        var query = c.GroupTitle is null
            ? s_planner.PlanFolder(c.Folder, classification, c.ComicInfo)
            : s_planner.PlanArchiveGroup(c.Folder, classification,
                classification.ArchiveGroups.Single(g => g.QueryTitle == c.GroupTitle));

        var missing = new List<string>();
        var candidates = new List<MatchCandidate>();
        var searches = 0;
        foreach (var variant in query.Variants)
        {
            if (searches == MaxSearches) break;
            searches++;
            var page = GoldenFixtures.Search(variant.Text, c.DoujinAllowed);
            if (page is null)
            {
                missing.Add($"{{\"kind\":\"search\",\"query\":{System.Text.Json.JsonSerializer.Serialize(variant.Text)},\"doujin\":{(c.DoujinAllowed ? "true" : "false")}}}");
                break;
            }
            foreach (var hit in page.Hits.Where(h => !candidates.Any(x => x.ExternalId == h.ExternalId)))
                candidates.Add(hit);
            var probe = s_scorer.Score(query, candidates, MatchThresholds.Default);
            if (probe.Ranked.Count > 0 && probe.Ranked[0].TitleScore >= NextVariantBelow)
                break;
        }

        var gets = 0;
        var ranked = s_scorer.Score(query, candidates, MatchThresholds.Default).Ranked;
        var toGet = ranked.Take(1).ToList();
        if (ranked.Count > 1 && ranked[0].TitleScore >= MatchThresholds.Default.ReviewFloor
            && ranked[0].TitleScore - ranked[1].TitleScore <= SecondGetWithin)
            toGet.Add(ranked[1]);
        foreach (var s in toGet.Where(s => s.TitleScore >= MatchThresholds.Default.ReviewFloor))
        {
            gets++;
            var full = GoldenFixtures.Series(s.Candidate.ExternalId);
            if (full is null)
            {
                missing.Add($"{{\"kind\":\"get\",\"id\":\"{s.Candidate.ExternalId}\"}}");
                continue;
            }
            // The full record replaces the hit; the hit title stays an alt title.
            var alt = full.AltTitles.Concat(s.Candidate.AltTitles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            candidates[candidates.FindIndex(x => x.ExternalId == s.Candidate.ExternalId)] = full with { AltTitles = alt };
        }

        // Retrieval above always runs at the defaults (a fixed fixture set); only the final bands use the given thresholds.
        var outcome = missing.Count == 0 ? s_scorer.Score(query, candidates, thresholds) : null;
        return new Run(classification, outcome, missing, searches, gets);
    }
}
