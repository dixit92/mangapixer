namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using System.Text.Json;
using System.Threading.RateLimiting;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.51.0 (matcher rules 6): the stored other names of a record's authors join its candidate's authors, so an archive whose creator tag
/// (or whose folder-name author hint) gives an alias of the record's author agrees with the record instead of vetoing it. The production
/// retrieval loop (<see cref="AutoMatchLookup"/>) with the REAL scorer over scripted MangaUpdates answers and the real alias store, then
/// the whole path through <c>MetadataAutoMatchService.ProcessAsync</c> with the real detector and planner. Synthetic names; no network.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class AliasCandidateScoringTests : IAsyncLifetime
{
    private const long MainPenId = 7001;
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
        await _h.EnableAutomaticAsync();
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private static readonly MetadataRateLimitOptions s_unpaced = new()
    {
        AutomaticInterval = TimeSpan.Zero,
        Api = new TokenBucketRateLimiterOptions { TokenLimit = 1000, TokensPerPeriod = 1000, ReplenishmentPeriod = TimeSpan.FromMilliseconds(1), QueueLimit = 0, AutoReplenishment = true },
    };

    /// <summary>The stored answer of one author-record request, built directly (the store is filled only when an admin asks).</summary>
    private async Task StoreAuthorAsync(long id, string name, params string[] otherNames)
    {
        _db.Db.MetadataAuthors.Add(new MetadataAuthorEntity
        {
            Provider = "mangaupdates",
            ExternalId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Name = name,
            OtherNamesJson = otherNames.Length == 0 ? null : MetadataJson.WriteList(otherNames),
            Status = (int)MetadataAuthorStatus.Ok,
            FetchedAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await _db.Db.SaveChangesAsync();
    }

    /// <summary>Counts the batched reads of the alias store (and forwards them to the real one).</summary>
    private sealed class CountingAliases(IAuthorAliasSource inner) : IAuthorAliasSource
    {
        public List<string[]> Calls { get; } = [];

        public Task<IReadOnlyDictionary<string, AuthorAliases>> GetAsync(IReadOnlyCollection<string> authorIds, CancellationToken ct)
        {
            Calls.Add(authorIds.Order(StringComparer.Ordinal).ToArray());
            return inner.GetAsync(authorIds, ct);
        }
    }

    private static MatchQuery ArchiveQuery(string title, string[]? tags = null, string[]? hints = null) => new(
        [new QueryVariant(title, QueryVariantKind.Primary)],
        new MatchContext(WorkClass.CollectionLeaf, 1, 0, 0, null, null, false, tags ?? [], CreatorHints: hints));

    /// <summary>Searches answer from <paramref name="hits"/> (page 1 only), GETs from <paramref name="records"/>.</summary>
    private async Task<(WorkLookupResult Result, CountingAliases Aliases, List<long> Gets)> RunAsync(
        MatchQuery query, MuJson.Hit[] hits, Dictionary<long, string> records)
    {
        using var net = new GatewayHarness(_db, s_unpaced);
        var gets = new List<long>();
        net.Handler.Respond = request =>
        {
            if (request.Method == HttpMethod.Post)
                return ScriptedHandler.Json(MuJson.Search(hits));
            var id = long.Parse(request.RequestUri!.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
            gets.Add(id);
            return records.TryGetValue(id, out var r)
                ? ScriptedHandler.Json(r)
                : ScriptedHandler.Json("{\"reason\":\"not found\"}", System.Net.HttpStatusCode.NotFound);
        };
        var aliases = new CountingAliases(new StoredAuthorAliases(_db.Db));
        var lookup = new AutoMatchLookup(_db.Db, net.Gateway(), new MatchQueryPlanner(), new MatchScorer(), aliases: aliases);
        var classification = new WorkClassification(WorkClass.CollectionLeaf, MatchLevel.Archive, [], []);
        var result = await lookup.SearchAndScoreAsync(query, classification, _db.LibraryId, MatchThresholds.Default, false,
            MetadataCallContext.Automatic(), CancellationToken.None);
        return (result, aliases, gets);
    }

    private static Dictionary<long, string> ShortStory(params string[] authors) =>
        new() { [7] = MuJson.Get(7, "Short Story", authors: authors) };

    private static readonly MuJson.Hit[] s_hit = [new MuJson.Hit(7, "Short Story")];

    [Fact]
    public async Task AnArchiveTaggedWithAnAliasOfTheRecordsAuthor_ScoresLikeOneTaggedWithTheRecordsName()
    {
        await StoreAuthorAsync(MainPenId, "Main Pen", "Hidden Pen", "Third Pen");

        var byName = (await RunAsync(ArchiveQuery("Short Story", ["Main Pen"]), s_hit, ShortStory("Main Pen"))).Result.Outcome;
        var (alias, _, _) = await RunAsync(ArchiveQuery("Short Story", ["Hidden Pen"]), s_hit, ShortStory("Main Pen"));
        var byAlias = alias.Outcome;

        Assert.Equal(MatchBand.Auto, byName.Band);
        Assert.Equal(MatchBand.Auto, byAlias.Band);
        Assert.False(byAlias.Ranked[0].Reasons.HasFlag(MatchReason.AuthorConflict));
        Assert.Equal(byName.Ranked[0].AdjustedScore, byAlias.Ranked[0].AdjustedScore, 9); // the same AuthorAgree bonus (rules 4's bracket alias)
        Assert.Contains("Hidden Pen", byAlias.Ranked[0].Candidate.Authors);
        Assert.Contains("Main Pen", byAlias.Ranked[0].Candidate.Authors);
    }

    [Fact]
    public async Task ATagNamingSomeoneElse_StillConflicts_WhateverAliasesAreStored()
    {
        await StoreAuthorAsync(MainPenId, "Main Pen", "Hidden Pen");

        var (result, _, _) = await RunAsync(ArchiveQuery("Short Story", ["Someone Else"]), s_hit, ShortStory("Main Pen"));

        Assert.Equal(MatchBand.NeedsReview, result.Outcome.Band);
        Assert.True(result.Outcome.Ranked[0].Reasons.HasFlag(MatchReason.AuthorConflict));
    }

    [Fact]
    public async Task WithNoStoredAliases_TheResultIsTheOldOne_AndTheAliasTagStillConflicts()
    {
        var (result, aliases, _) = await RunAsync(ArchiveQuery("Short Story", ["Hidden Pen"]), s_hit, ShortStory("Main Pen"));

        Assert.Equal(MatchBand.NeedsReview, result.Outcome.Band);
        Assert.True(result.Outcome.Ranked[0].Reasons.HasFlag(MatchReason.AuthorConflict));
        Assert.Equal(["Main Pen"], result.Outcome.Ranked[0].Candidate.Authors);
        Assert.Equal([[MainPenId.ToString(System.Globalization.CultureInfo.InvariantCulture)]], aliases.Calls); // asked once, found nothing
    }

    [Fact]
    public async Task AFolderNameAuthorHint_GivingAnAlias_NamesTheRecord_LikeItsOwnName()
    {
        var withoutStore = (await RunAsync(ArchiveQuery("Short Story", hints: ["Hidden Pen"]), s_hit, ShortStory("Main Pen"))).Result.Outcome;
        await StoreAuthorAsync(MainPenId, "Main Pen", "Hidden Pen");
        var withStore = (await RunAsync(ArchiveQuery("Short Story", hints: ["Hidden Pen"]), s_hit, ShortStory("Main Pen"))).Result.Outcome;
        var byName = (await RunAsync(ArchiveQuery("Short Story", hints: ["Main Pen"]), s_hit, ShortStory("Main Pen"))).Result.Outcome;

        Assert.Equal(byName.Ranked[0].AdjustedScore, withStore.Ranked[0].AdjustedScore, 9);
        Assert.Equal(MatchScorer.CreatorHintAgree, withStore.Ranked[0].AdjustedScore - withoutStore.Ranked[0].AdjustedScore, 9);
    }

    [Fact]
    public async Task ManyCandidatesReadInOnePass_AskTheAliasStoreOnce_ForEveryAuthorId()
    {
        await StoreAuthorAsync(8001, "First Pen", "Hidden One");
        await StoreAuthorAsync(8002, "Second Pen", "Hidden Two");
        // Two same-titled records, both read (the best hit and its close runner-up); the second has two authors.
        MuJson.Hit[] hits = [new MuJson.Hit(1, "Sprout (FIRST Pen)"), new MuJson.Hit(2, "Sprout (SECOND Pen)")];
        var records = new Dictionary<long, string>
        {
            [1] = MuJson.Get(1, "Sprout (FIRST Pen)", authors: ["First Pen"], authorIds: [8001]),
            [2] = MuJson.Get(2, "Sprout (SECOND Pen)", authors: ["Second Pen", "Unknown Hand"], authorIds: [8002, 8003]),
        };

        var (result, aliases, gets) = await RunAsync(ArchiveQuery("Sprout", ["Hidden Two"]), hits, records);

        Assert.Equal([1L, 2L], gets.Order());
        Assert.Equal([["8001", "8002", "8003"]], aliases.Calls); // one batched read, not one per candidate or per author
        var second = result.Outcome.Ranked.Single(r => r.Candidate.ExternalId == "2");
        Assert.Contains("Hidden Two", second.Candidate.Authors);
        Assert.DoesNotContain("Hidden One", second.Candidate.Authors);
        Assert.False(second.Reasons.HasFlag(MatchReason.AuthorConflict));
        Assert.True(result.Outcome.Ranked.Single(r => r.Candidate.ExternalId == "1").Reasons.HasFlag(MatchReason.AuthorConflict));
    }

    [Fact]
    public async Task ASearchHitThatIsNeverRead_CostsNoAliasLookup_AndKeepsNoAuthors()
    {
        await StoreAuthorAsync(MainPenId, "Main Pen", "Hidden Pen");

        var (result, aliases, gets) = await RunAsync(ArchiveQuery("Short Story", ["Hidden Pen"]), [new MuJson.Hit(9, "Unrelated Filler Name")], []);

        Assert.Empty(gets);
        Assert.Empty(aliases.Calls);
        Assert.All(result.Outcome.Ranked, r => Assert.Empty(r.Candidate.Authors));
    }

    [Fact]
    public async Task ARecordWithoutAuthorIds_NeedsNoLookup()
    {
        var record = MuJson.Get(7, "Short Story", authors: ["Main Pen"]).Replace("\"author_id\":7001", "\"author_id\":null", StringComparison.Ordinal);

        var (result, aliases, _) = await RunAsync(ArchiveQuery("Short Story", ["Main Pen"]), s_hit, new() { [7] = record });

        Assert.Empty(aliases.Calls);
        Assert.Equal(MatchBand.Auto, result.Outcome.Band);
    }

    // --- the whole path: a folder named by the alias, through ProcessAsync with the real detector, planner and scorer ---

    private async Task<(NodeSeriesLinkEntity Link, MetadataMatchQueueEntity Queue)> AliasFolderAsync(bool aliasStored)
    {
        // A linked series by "Main Pen" (the library's evidence); the folder "Hidden Pen" is named by the author's other name.
        var record = await _db.AddRecordAsync("741", "Synthetic Linked Saga");
        // Without the stored alias the folder is recognised by the linked record listing the name itself (the pre-1.51.0 evidence), so the
        // same archives reach the scorer in both runs; only the candidate's known names differ.
        record.CreatorsJson = JsonSerializer.Serialize(aliasStored
            ? new[] { new { name = "Main Pen", role = "author", providerId = (string?)"7001" } }
            : [new { name = "Main Pen", role = "author", providerId = (string?)"7001" }, new { name = "Hidden Pen", role = "artist", providerId = (string?)null }]);
        await _db.Db.SaveChangesAsync();
        await _db.AddLinkAsync(await _db.AddFolderAsync(null, "Linked Saga"), record);
        if (aliasStored)
            await StoreAuthorAsync(MainPenId, "Main Pen", "Hidden Pen");

        var folder = await _db.AddFolderAsync(null, "Hidden Pen");
        CatalogNodeEntity? alpha = null;
        foreach (var title in new[] { "Alpha Story", "Beta Tale", "Gamma Saga" })
        {
            var archive = await _db.AddArchiveAsync(folder, $"Hidden Pen - {title}");
            alpha ??= archive;
        }
        _h.Search["Alpha Story"] = [new MuJson.Hit(801, "Alpha Story")];
        _h.Records[801] = MuJson.Get(801, "Alpha Story", authors: ["Main Pen"], status: "1 Volume (Complete)");

        var service = _h.ServiceWithRealMatcher();
        var (error, run) = await service.StartBulkAsync(_db.LibraryPublicId, new Core.Api.MetadataMatchLibraryRequest(), "admin");
        Assert.Null(error);
        Assert.Equal(3, run!.Candidates); // three archive works: the alias-named folder is an artist folder
        for (var i = 0; i < 20; i++)
        {
            var row = await _h.ServiceWithRealMatcher().LeaseNextAsync("test");
            if (row is null)
                break;
            await _h.ServiceWithRealMatcher().ProcessAsync(row);
        }
        _db.Db.ChangeTracker.Clear();
        var link = await _db.Db.NodeSeriesLinks.AsNoTracking().Include(l => l.Record).SingleAsync(l => l.NodeId == alpha!.Id);
        var queue = await _db.Db.MetadataMatchQueue.AsNoTracking().SingleAsync(q => q.NodeId == alpha!.Id);
        return (link, queue);
    }

    [Fact]
    public async Task AnUntaggedArchiveInAnAliasNamedFolder_LinksAutomatically_WhenTheAliasIsStored()
    {
        var (link, queue) = await AliasFolderAsync(aliasStored: true);

        Assert.Equal((int)SeriesLinkState.Auto, link.State);
        Assert.Equal("801", link.Record!.ExternalId);
        Assert.False(((MatchReason)queue.OutcomeReasons).HasFlag(MatchReason.AuthorConflict));
        Assert.Equal(MatcherRules.Revision, queue.RulesRevision);
    }

    [Fact]
    public async Task TheSameArchive_WithoutTheStoredAlias_WaitsInReview_WithTheAuthorVeto()
    {
        var (link, queue) = await AliasFolderAsync(aliasStored: false);

        Assert.Equal((int)SeriesLinkState.NeedsReview, link.State);
        Assert.True(((MatchReason)queue.OutcomeReasons).HasFlag(MatchReason.AuthorConflict));
    }
}
