namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch.Golden;

using System.Threading.RateLimiting;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// One migrated database for the whole golden set: Fetch on for the library, the Automatic matching switch
/// with its consent, and a budget no run can spend (the set measures matching, not the budget).
/// </summary>
public sealed class GoldenEnvironment : IAsyncLifetime
{
    public MetadataTestDb Db { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Db = await MetadataTestDb.CreateAsync();
        using var net = new GatewayHarness(Db);
        await net.EnableAsync();
        var row = await Db.Db.AppSettings.FirstAsync(s => s.Id == AppSettingsEntity.SingletonId);
        row.MetadataAutoMatchEnabled = true;
        row.MetadataAutoConsentVersion = MetadataAutoConsent.CurrentVersion;
        row.MetadataAutoConsentAt = net.Time.GetUtcNow();
        row.MetadataDailyBudget = 1_000_000;
        await Db.Db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await Db.DisposeAsync();
}

/// <summary>
/// The stage-2 golden set: every case runs the real detector and planner, then the PRODUCTION retrieval
/// loop (<see cref="AutoMatchLookup.SearchAndScoreAsync"/>: automatic searches through the real
/// <see cref="MetadataGateway"/> and MangaUpdates provider, GETs, the provider mapping) over RECORDED
/// MangaUpdates answers replayed by a scripted HTTP handler (no network), and the real scorer. Asserts the
/// class, band and chosen id per case; <see cref="Report_AutoRateAndPrecision"/> asserts and prints the
/// aggregate numbers so a threshold or rule change shows its effect in CI. Moved from Core.Tests in 1.27.0:
/// the old harness modelled the retrieval loop and parsed the provider JSON itself, so it passed while the
/// production mapping dropped the chapter total (live run, 2026-09-27).
/// </summary>
public sealed class GoldenSetTests(GoldenEnvironment env, ITestOutputHelper output) : IClassFixture<GoldenEnvironment>
{
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
    public async Task Case(GoldenCase c)
    {
        var run = await ExecuteAsync(c, MatchThresholds.Default);

        Assert.True(run.Missing.Count == 0, "Missing fixtures:\n" + string.Join('\n', run.Missing));
        if (c.Class is { } cls)
            Assert.Equal(cls, run.Classification.Class);
        if (c.Content is { } content)
            Assert.Equal(content, run.Classification.ContentSuggestion);
        if (c.Band is not { } band)
            return;

        var outcome = run.Outcome!;
        var top = outcome.Ranked.Count > 0 ? outcome.Ranked[0] : null;
        var detail = Describe(outcome);
        Assert.True(band == outcome.Band, $"band {outcome.Band}, expected {band}: {detail}");
        if (c.Vetoes is { } vetoes)
            Assert.Equal(vetoes, top!.Reasons & MatchScorer.VetoReasons);
        if (c.ExpectedId is { } id)
            Assert.True(top?.Candidate.ExternalId == id, $"chosen {top?.Candidate.ExternalId}, expected {id}: {detail}");
    }

    /// <summary>
    /// The aggregate bands at the default thresholds (1.27.0). A rule change that moves a band must update these on
    /// purpose, with the per-case reason in <see cref="GoldenCases"/>.
    /// </summary>
    public const int ExpectedAuto = 54, ExpectedReview = 11, ExpectedUnmatched = 2;

    [Fact]
    public async Task Aggregate_BandsAndPrecision_AtTheDefaults()
    {
        var (_, auto, autoCorrect, review, unmatched) = await CountAsync(MatchThresholds.Default, _ => true);

        Assert.Equal(auto, autoCorrect); // precision 100%: every auto link is the expected record
        Assert.Equal((ExpectedAuto, ExpectedReview, ExpectedUnmatched), (auto, review, unmatched));
    }

    [Fact]
    public async Task RecordedEnglishPublisherNotes_ReachTheCandidate_ThroughTheProductionMapping()
    {
        var missing = new List<string>();
        using var net = new GatewayHarness(env.Db, s_unpaced);
        net.Handler.Respond = request => GoldenFixtures.Respond(request, missing);
        var record = await net.Gateway().GetSeriesAsync("mangaupdates", env.Db.LibraryId, "15180124327", CancellationToken.None, MetadataCallContext.Automatic());
        var candidate = AutoMatchLookup.ToCandidate(record!);

        Assert.Empty(missing);
        Assert.Equal((15, 201), (candidate.EnglishVolumes, candidate.EnglishChapters)); // "13+2 Volumes", "201 Chapters"
        Assert.Equal(200, candidate.TotalChapters); // "200 Chapters + Prologue (Complete)"
        Assert.True(candidate.Webtoon);
    }

    [Fact]
    public async Task Report_AutoRateAndPrecision()
    {
        // Per case at the default thresholds, then the aggregate at the default and at both ends
        // of the admin-adjustable bounds (decision 13), so a threshold change shows its effect.
        foreach (var c in GoldenCases.All.Where(c => c.Band is not null))
        {
            var o = (await ExecuteAsync(c, MatchThresholds.Default)).Outcome;
            output.WriteLine($"{c.Id}: {o?.Band} {(o is null ? "no outcome" : Describe(o))}");
        }

        var loosest = new MatchThresholds(MatchThresholds.AutoTitleMin, MatchThresholds.MarginMin, MatchThresholds.ReviewFloorMin);
        var strictest = new MatchThresholds(MatchThresholds.AutoTitleMax, MatchThresholds.MarginMax, MatchThresholds.ReviewFloorMax);
        (string Name, MatchThresholds Thresholds)[] sweep = [("default", MatchThresholds.Default), ("loosest", loosest), ("strictest", strictest)];
        foreach (var (name, thresholds) in sweep)
        {
            var (report, auto, autoCorrect) = await AggregateAsync(thresholds);
            output.WriteLine($"GOLDEN {name}: {report}");
            Console.WriteLine($"GOLDEN {name}: {report}");

            // Wrong auto links are the costly failure: at the defaults every auto link must be right.
            if (name == "default")
                Assert.Equal(auto, autoCorrect);
        }

        // Archive-level works (collection / artist folders, loose archives) reported separately (1.27.0).
        var (_, archiveAuto, archiveCorrect, archiveReview, archiveUnmatched) = await CountAsync(MatchThresholds.Default, c => c.GroupTitle is not null);
        var archiveTotal = archiveAuto + archiveReview + archiveUnmatched;
        var archiveLine = FormattableString.Invariant(
            $"GOLDEN archive-level: {archiveTotal} works, auto {archiveAuto} ({100.0 * archiveAuto / Math.Max(1, archiveTotal):0.0}%), precision {archiveCorrect}/{archiveAuto}");
        output.WriteLine(archiveLine);
        Console.WriteLine(archiveLine);

        // Measure only (1.27.0 lane brief): would a different auto threshold per work class help? Auto count and
        // precision per class across the admin-adjustable auto range, margin and floor at their defaults.
        foreach (var cls in GoldenCases.All.Where(c => c.Band is not null).Select(c => s_detector.Classify(c.Folder).Class).Distinct().Order())
        {
            var parts = new List<string>();
            foreach (var autoTitle in new[] { 0.85, 0.88, 0.90, 0.92, 0.95, 0.99 })
            {
                var t = MatchThresholds.Default with { AutoTitle = autoTitle };
                var (_, a, ok, _, _) = await CountAsync(t, c => s_detector.Classify(c.Folder).Class == cls);
                parts.Add(FormattableString.Invariant($"{autoTitle:0.00}: {ok}/{a}"));
            }
            var line = $"GOLDEN per-class {cls}: " + string.Join(", ", parts);
            output.WriteLine(line);
            Console.WriteLine(line);
        }
    }

    private async Task<(int Matched, int Auto, int AutoCorrect, int Review, int Unmatched)> CountAsync(MatchThresholds thresholds, Func<GoldenCase, bool> filter)
    {
        int matched = 0, auto = 0, autoCorrect = 0, review = 0, unmatched = 0;
        foreach (var c in GoldenCases.All.Where(c => c.Band is not null && filter(c)))
        {
            if ((await ExecuteAsync(c, thresholds)).Outcome is not { } o) continue;
            matched++;
            var topId = o.Ranked.Count > 0 ? o.Ranked[0].Candidate.ExternalId : null;
            switch (o.Band)
            {
                case MatchBand.Auto:
                    auto++;
                    if (c.ExpectedId is not null && topId == c.ExpectedId) autoCorrect++;
                    break;
                case MatchBand.NeedsReview:
                    review++;
                    break;
                default:
                    unmatched++;
                    break;
            }
        }
        return (matched, auto, autoCorrect, review, unmatched);
    }

    private async Task<(string Report, int Auto, int AutoCorrect)> AggregateAsync(MatchThresholds thresholds)
    {
        int matched = 0, auto = 0, autoCorrect = 0, review = 0, reviewTopCorrect = 0, reviewWithExpected = 0, unmatched = 0, searches = 0, gets = 0;
        foreach (var c in GoldenCases.All.Where(c => c.Band is not null))
        {
            var run = await ExecuteAsync(c, thresholds);
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

    private static string Describe(MatchOutcome outcome)
    {
        var top = outcome.Ranked.Count > 0 ? outcome.Ranked[0] : null;
        return top is null
            ? "no candidates"
            : FormattableString.Invariant($"top {top.Candidate.ExternalId} '{top.Candidate.Title}' title {top.TitleScore:0.000} adj {top.AdjustedScore:0.000} [{top.Reasons}]")
                + (outcome.Ranked.Count > 1
                    ? FormattableString.Invariant($"; 2nd {outcome.Ranked[1].Candidate.ExternalId} '{outcome.Ranked[1].Candidate.Title}' adj {outcome.Ranked[1].AdjustedScore:0.000}")
                    : string.Empty);
    }

    /// <summary>
    /// Runs one case. Retrieval always runs at the default thresholds (a fixed fixture set); the final bands
    /// re-score the retrieved candidates at <paramref name="thresholds"/>.
    /// </summary>
    public Task<Run> ExecuteAsync(GoldenCase c, MatchThresholds thresholds) => ExecuteAsync(env.Db, c, thresholds);

    public static async Task<Run> ExecuteAsync(MetadataTestDb db, GoldenCase c, MatchThresholds thresholds)
    {
        var classification = s_detector.Classify(c.Folder);
        if (c.Band is null)
            return new Run(classification, null, [], 0, 0);

        var query = c.GroupTitle is null
            ? s_planner.PlanFolder(c.Folder, classification, c.ComicInfo)
            : s_planner.PlanArchiveGroup(c.Folder, classification,
                classification.ArchiveGroups.Single(g => g.QueryTitle == c.GroupTitle));

        var missing = new List<string>();
        using var net = new GatewayHarness(db, s_unpaced);
        net.Handler.Respond = request => GoldenFixtures.Respond(request, missing);
        var lookup = new AutoMatchLookup(db.Db, net.Gateway(), s_planner, s_scorer);
        var result = await lookup.SearchAndScoreAsync(
            query, classification, db.LibraryId, MatchThresholds.Default, c.DoujinAllowed, MetadataCallContext.Automatic(), CancellationToken.None);

        var seen = net.Handler.Seen;
        var searches = seen.Count(r => r.Method == HttpMethod.Post);
        var gets = seen.Count(r => r.Method == HttpMethod.Get);
        var outcome = missing.Count > 0
            ? null
            : thresholds == MatchThresholds.Default
                ? result.Outcome
                : s_scorer.Score(query, result.Outcome.Ranked.Select(r => r.Candidate).ToList(), thresholds);
        return new Run(classification, outcome, missing, searches, gets);
    }

    /// <summary>No automatic spacing and a bucket no case can empty: replayed answers never wait on real time.</summary>
    private static readonly MetadataRateLimitOptions s_unpaced = new()
    {
        AutomaticInterval = TimeSpan.Zero,
        Api = new TokenBucketRateLimiterOptions
        {
            TokenLimit = 10_000,
            TokensPerPeriod = 10_000,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        },
    };
}
