namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of automatic matching (stage 2) with the fake matcher
/// core: post-scan queueing (new folders + parents, idempotent, one boolean when
/// off), the lease (expiry resume), outcome writes (auto link live at once, review
/// candidates, unmatched retry ladder), re-match rules, review-first, review-only
/// classes, archive groups, doujinshi Content, refusal release, runs + counters,
/// and a log sentinel (no folder name ever reaches a log line).
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class AutoMatchServiceTests : IAsyncLifetime
{
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;
    private DateTimeOffset _since;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
        _since = DateTimeOffset.UtcNow.AddMinutes(-1);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private async Task<CatalogNodeEntity> SeriesAsync(string name, CatalogNodeEntity? parent = null, int archives = 2)
    {
        var folder = await _db.AddFolderAsync(parent, name);
        for (var i = 1; i <= archives; i++)
            await _db.AddArchiveAsync(folder, $"{name} v{i:D2}");
        return folder;
    }

    private Task<int> EnqueueAsync() => _h.Service().EnqueueNewFoldersAsync(_db.LibraryId, _since);

    private async Task<NodeSeriesLinkEntity?> LinkOfAsync(CatalogNodeEntity node) =>
        await _db.Db.NodeSeriesLinks.AsNoTracking().FirstOrDefaultAsync(l => l.NodeId == node.Id);

    private async Task<MetadataMatchQueueEntity> QueueOfAsync(CatalogNodeEntity node) =>
        await _db.Db.MetadataMatchQueue.AsNoTracking().SingleAsync(q => q.NodeId == node.Id);

    // --- Queueing ---

    [Fact]
    public async Task PostScan_QueuesNewSeriesLikeFolders_Once_WithAScanRun()
    {
        var category = await _db.AddFolderAsync(null, "Manga");
        var alpha = await SeriesAsync("Alpha Saga", category);
        var beta = await SeriesAsync("Beta Tale", category);

        Assert.Equal(2, await EnqueueAsync());
        Assert.Equal(0, await EnqueueAsync()); // idempotent: one row per node
        var rows = await _db.Db.MetadataMatchQueue.AsNoTracking().ToListAsync();
        Assert.Equal([alpha.Id, beta.Id], rows.Select(r => r.NodeId).Order().ToArray());
        Assert.All(rows, r => Assert.Equal(QueueState.Pending, r.State));
        var run = await _db.Db.MetadataMatchRuns.AsNoTracking().SingleAsync();
        Assert.Equal((int)MetadataMatchRunTrigger.Scan, run.Trigger);
        Assert.Equal(2, run.Candidates);
        Assert.Equal(2, run.Queued);
        Assert.Equal(0, _h.Handler.CallCount); // queueing never contacts the provider
    }

    [Fact]
    public async Task PostScan_FoldersFromEarlierScans_AreNotQueued()
    {
        await SeriesAsync("Old Saga");
        var since = DateTimeOffset.UtcNow.AddSeconds(1);
        await Task.Delay(1100);
        var fresh = await SeriesAsync("Fresh Saga");
        Assert.Equal(1, await _h.Service().EnqueueNewFoldersAsync(_db.LibraryId, since));
        Assert.Equal(fresh.Id, (await _db.Db.MetadataMatchQueue.AsNoTracking().SingleAsync()).NodeId);
    }

    [Fact]
    public async Task PostScan_NewArchive_InACollectionFolder_IsQueued_ANewChapterOfAQueuedSeriesIsNot()
    {
        var shelf = await _db.AddFolderAsync(null, "Collection Shelf");
        foreach (var title in new[] { "Alpha Story.cbz", "Beta Tale.cbz", "Gamma Saga.cbz", "Delta Night.cbz", "Epsilon Dawn.cbz" })
            await _db.AddArchiveAsync(shelf, title);
        var series = await SeriesAsync("Zeta Saga");
        Assert.Equal(6, await EnqueueAsync()); // five archive works + the series

        var since = DateTimeOffset.UtcNow.AddSeconds(1);
        await Task.Delay(1100);
        var fresh = await _db.AddArchiveAsync(shelf, "Eta Voyage.cbz");
        await _db.AddArchiveAsync(series, "Zeta Saga v03");

        Assert.Equal(1, await _h.Service().EnqueueNewFoldersAsync(_db.LibraryId, since));
        Assert.Equal((int)MatchLevel.Archive, (await QueueOfAsync(fresh)).Level);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task PostScanHook_AutomaticOff_QueuesNothing_ButCarryOverStillRuns()
    {
        await SeriesAsync("Alpha Saga");
        await _h.EnableAutomaticAsync(automatic: false);
        var hook = new MetadataPostScanHook(_h.CarryOver(), _h.Service(), NullLogger<MetadataPostScanHook>.Instance);
        await hook.OnScanCompletedAsync(_db.LibraryId, _since, new ScanResult { Success = true });
        Assert.False(await _db.Db.MetadataMatchQueue.AnyAsync());
        Assert.Equal(0, _h.Handler.CallCount);

        await _h.EnableAutomaticAsync();
        await hook.OnScanCompletedAsync(_db.LibraryId, _since, new ScanResult { Success = true });
        Assert.True(await _db.Db.MetadataMatchQueue.AnyAsync());
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task MatcherNotRegistered_WaitsWithMatcherUnavailable_AndQueuesNothing()
    {
        await SeriesAsync("Alpha Saga");
        await _h.EnableAutomaticAsync();
        var service = _h.ServiceWithoutMatcher();
        Assert.Equal("matcher_unavailable", (await service.CheckGlobalGateAsync())!.Code);
        Assert.Equal(0, await service.EnqueueNewFoldersAsync(_db.LibraryId, _since));
    }

    // --- Lease ---

    [Fact]
    public async Task Lease_IsExclusive_AndAnExpiredLeaseIsPickedUpAgain()
    {
        await SeriesAsync("Alpha Saga");
        await EnqueueAsync();
        await _h.EnableAutomaticAsync();

        var first = await _h.Service().LeaseNextAsync("worker-a");
        Assert.NotNull(first);
        Assert.Null(await _h.Service().LeaseNextAsync("worker-b"));

        _h.Time.Advance(AutoMatchPolicy.LeaseDuration + TimeSpan.FromSeconds(1)); // worker-a "crashed"
        var again = await _h.Service().LeaseNextAsync("worker-b");
        Assert.Equal(first!.Id, again!.Id);
        Assert.Equal("worker-b", (await _db.Db.MetadataMatchQueue.AsNoTracking().SingleAsync()).LeaseOwner);
    }

    [Fact]
    public async Task Lease_SkipsLibrariesWhoseFetchSwitchIsOff()
    {
        await SeriesAsync("Alpha Saga");
        await EnqueueAsync();
        await _h.EnableAutomaticAsync();
        var lib = await _db.Db.Libraries.FirstAsync(l => l.Id == _db.LibraryId);
        lib.MetadataEnabled = false;
        await _db.Db.SaveChangesAsync();
        Assert.Null(await _h.Service().LeaseNextAsync("w"));
    }

    // --- Outcomes ---

    [Fact]
    public async Task ConfidentMatch_AutoLinksAtOnce_StoresTheRecord_AndCountsTheRun()
    {
        var alpha = await SeriesAsync("Alpha Saga");
        _h.Search["Alpha Saga"] = [new MuJson.Hit(101, "Alpha Saga"), new MuJson.Hit(102, "Unrelated Omega")];
        _h.Records[101] = MuJson.Get(101, "Alpha Saga", relatedId: 555);
        await _h.EnableAutomaticAsync();
        await EnqueueAsync();

        Assert.Equal(1, await _h.DrainAsync());

        var link = await LinkOfAsync(alpha);
        Assert.Equal((int)SeriesLinkState.Auto, link!.State);
        Assert.Equal((int)MetadataMatchMethod.Auto, link.MatchMethod);
        var record = await _db.Db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Id == link.RecordId);
        Assert.Equal("101", record.ExternalId);
        Assert.Contains("Synthetic Weekly", record.PublicationsJson);
        Assert.Contains("\"externalId\":\"555\"", record.RelationsJson);
        Assert.Contains("\"providerId\":\"7001\"", record.CreatorsJson);

        // Auto links go live immediately (decision 1): the resolver shows web data.
        var info = await _db.ResolveAsync(alpha);
        Assert.Equal(SeriesInfoState.Web, info.State);
        Assert.Equal(SeriesLinkState.Auto, info.Link!.State);

        var queue = await QueueOfAsync(alpha);
        Assert.Equal(QueueState.Done, queue.State);
        Assert.Equal((int)MatchBand.Auto, queue.Outcome);
        var run = await _db.Db.MetadataMatchRuns.AsNoTracking().SingleAsync();
        Assert.Equal((1, 1, 2), (run.Processed, run.AutoLinked, run.RequestsUsed)); // 1 search + 1 GET
        Assert.Equal((int)MetadataMatchRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task CloseSecond_GoesToReview_WithStoredCandidates_AndNoLiveLink()
    {
        var beta = await SeriesAsync("Beta Tale");
        _h.Search["Beta Tale"] = [new MuJson.Hit(201, "Beta Tale"), new MuJson.Hit(202, "Beta Tale!", Year: 2015)];
        _h.Records[201] = MuJson.Get(201, "Beta Tale");
        _h.Records[202] = MuJson.Get(202, "Beta Tale!");
        await _h.EnableAutomaticAsync();
        await EnqueueAsync();
        await _h.DrainAsync();

        var link = await LinkOfAsync(beta);
        Assert.Equal((int)SeriesLinkState.NeedsReview, link!.State);
        Assert.Null(link.RecordId);
        var candidates = await _db.Db.MetadataMatchCandidates.AsNoTracking().Where(c => c.NodeId == beta.Id).OrderBy(c => c.Rank).ToListAsync();
        Assert.Equal([1, 2], candidates.Select(c => c.Rank).ToArray());
        Assert.Equal(["201", "202"], candidates.Select(c => c.ExternalId).ToArray());
        Assert.Equal(MatchReason.CloseSecond, (MatchReason)(await QueueOfAsync(beta)).OutcomeReasons);
        Assert.Equal(SeriesInfoState.None, (await _db.ResolveAsync(beta)).State); // review rows are not shown to users
    }

    [Fact]
    public async Task NoMatch_IsUnmatched_RetriedAfter30Then90Days()
    {
        var gamma = await SeriesAsync("Gamma Nothing");
        await _h.EnableAutomaticAsync();
        await EnqueueAsync();
        await _h.DrainAsync();

        var row = await QueueOfAsync(gamma);
        Assert.Equal((int)MatchBand.Unmatched, row.Outcome);
        Assert.Equal(_h.Time.GetUtcNow() + TimeSpan.FromDays(30), row.NotBefore);
        Assert.Null(await LinkOfAsync(gamma));

        _h.Time.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, await _h.Service().PromoteDueRetriesAsync());
        _h.Time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await _h.Service().PromoteDueRetriesAsync());
        Assert.Contains(await _db.Db.MetadataMatchRuns.AsNoTracking().ToListAsync(), r => r.Trigger == (int)MetadataMatchRunTrigger.Retry);
        await _h.DrainAsync();
        Assert.Equal(_h.Time.GetUtcNow() + TimeSpan.FromDays(90), (await QueueOfAsync(gamma)).NotBefore);
    }

    [Fact]
    public async Task ReviewFirst_BulkRun_SendsWouldBeAutoLinksToReview()
    {
        var alpha = await SeriesAsync("Alpha Saga");
        _h.Search["Alpha Saga"] = [new MuJson.Hit(101, "Alpha Saga")];
        _h.Records[101] = MuJson.Get(101, "Alpha Saga");
        await _h.EnableAutomaticAsync();

        var estimate = await _h.Service().EstimateAsync(_db.LibraryPublicId, retryUnmatched: false);
        Assert.True(estimate!.FirstRun);
        Assert.Equal(1, estimate.Candidates);
        Assert.Equal(3, estimate.EstimatedRequests);
        Assert.True(estimate.AutomaticAvailable);

        var (error, run) = await _h.Service().StartBulkAsync(_db.LibraryPublicId, new MetadataMatchLibraryRequest { ReviewFirst = true }, "admin");
        Assert.Null(error);
        Assert.True(run!.ReviewFirst);
        await _h.DrainAsync();
        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(alpha))!.State);
        Assert.False((await _h.Service().EstimateAsync(_db.LibraryPublicId, false))!.FirstRun);
    }

    [Fact]
    public async Task ReviewOnlyClass_NeverAutoLinks()
    {
        var mixed = await SeriesAsync("Mixed Bag");
        _h.Search["Mixed Bag"] = [new MuJson.Hit(401, "Mixed Bag")];
        _h.Records[401] = MuJson.Get(401, "Mixed Bag");
        await _h.EnableAutomaticAsync();
        await EnqueueAsync();
        await _h.DrainAsync();
        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(mixed))!.State);
        Assert.True(((MatchReason)(await QueueOfAsync(mixed)).OutcomeReasons).HasFlag(MatchReason.ReviewOnlyClass));
    }

    [Fact]
    public async Task ArchiveGroup_AutoLinksEveryArchiveOfTheGroup_OnlyTheAnchorHasAQueueRow()
    {
        var pairs = await _db.AddFolderAsync(null, "Pairs Shelf");
        var d1 = await _db.AddArchiveAsync(pairs, "Delta Saga");
        var d2 = await _db.AddArchiveAsync(pairs, "Delta Saga Extra");
        _h.Search["Delta Saga"] = [new MuJson.Hit(301, "Delta Saga")];
        _h.Records[301] = MuJson.Get(301, "Delta Saga");
        await _h.EnableAutomaticAsync();
        await EnqueueAsync();

        var row = await QueueOfAsync(d1);
        Assert.Equal((int)MatchLevel.Archive, row.Level);
        Assert.Equal([d2.Id], JsonSerializer.Deserialize<long[]>(row.MemberNodeIdsJson!)!);
        await _h.DrainAsync();

        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(d1))!.State);
        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(d2))!.State);
        Assert.Null(await LinkOfAsync(pairs)); // the collection folder itself is never linked
        Assert.False(await _db.Db.MetadataMatchQueue.AnyAsync(q => q.NodeId == d2.Id));
    }

    [Fact]
    public async Task DoujinshiContentFolder_LiftsTheDoujinshiExclusion_ForSearchesBelowIt()
    {
        var shelf = await _db.AddFolderAsync(null, "Shelf");
        var collection = await _db.AddFolderAsync(shelf, "Collection Circle");
        await _db.AddArchiveAsync(collection, "Epsilon Story");
        var other = await _db.AddFolderAsync(null, "Collection Plain");
        await _db.AddArchiveAsync(other, "Zeta Story");
        _db.Db.FolderMetadataContents.Add(new FolderMetadataContentEntity { NodeId = shelf.Id, Content = (int)MetadataFolderContent.DoujinshiAndAdultOneShots });
        await _db.Db.SaveChangesAsync();
        await _h.EnableAutomaticAsync();
        await EnqueueAsync();
        await _h.DrainAsync();

        var filters = _h.Handler.Seen.Where(s => s.Method == HttpMethod.Post)
            .ToDictionary(
                s => JsonDocument.Parse(s.Body!).RootElement.GetProperty("search").GetString()!,
                s => JsonDocument.Parse(s.Body!).RootElement.GetProperty("filter_types").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.DoesNotContain("Doujinshi", filters["Epsilon Story"]); // inherited from the ancestor's Content
        Assert.Contains("Doujinshi", filters["Zeta Story"]);
    }

    // --- Re-match rules ---

    [Fact]
    public async Task ConfirmedDontMatchAndAutoLinks_AreNeverMatchedAgainAutomatically()
    {
        var confirmed = await SeriesAsync("Confirmed Saga");
        var dont = await SeriesAsync("Dont Saga");
        var auto = await SeriesAsync("Auto Saga");
        var record = await _db.AddRecordAsync("9001", "Confirmed Saga");
        await _db.AddLinkAsync(confirmed, record);
        await _db.AddLinkAsync(dont, null, SeriesLinkState.DontMatch);
        await _db.AddLinkAsync(auto, record, SeriesLinkState.Auto);
        await _h.EnableAutomaticAsync();

        Assert.Equal(0, await EnqueueAsync());
        var (works, linked, _) = await _h.Service().DetectLibraryAsync(_db.LibraryId, retryUnmatched: true, default);
        Assert.Empty(works);
        Assert.Equal(3, linked);
        var (error, _) = await _h.Service().StartBulkAsync(_db.LibraryPublicId, new MetadataMatchLibraryRequest(), "admin");
        Assert.Equal("nothing_to_match", error);
    }

    [Fact]
    public async Task ARowWhoseNodeWasLinkedMeanwhile_IsSkipped_WithZeroRequests()
    {
        var alpha = await SeriesAsync("Alpha Saga");
        _h.Search["Alpha Saga"] = [new MuJson.Hit(101, "Alpha Saga")];
        await _h.EnableAutomaticAsync();
        await EnqueueAsync();
        await _db.AddLinkAsync(alpha, await _db.AddRecordAsync("9002", "Alpha Saga")); // an admin linked it first

        await _h.DrainAsync();
        Assert.Equal(QueueState.Skipped, (await QueueOfAsync(alpha)).State);
        Assert.Equal(0, _h.Handler.CallCount);
        Assert.Equal((int)SeriesLinkState.Confirmed, (await LinkOfAsync(alpha))!.State);
    }

    [Fact]
    public async Task Rerun_ReEvaluatesANeedsReviewRow_OnlyWhenAnAdminAsks()
    {
        var beta = await SeriesAsync("Beta Tale");
        _h.Search["Beta Tale"] = [new MuJson.Hit(201, "Beta Tale"), new MuJson.Hit(202, "Beta Tale!")];
        _h.Records[201] = MuJson.Get(201, "Beta Tale");
        _h.Records[202] = MuJson.Get(202, "Beta Tale!");
        await _h.EnableAutomaticAsync();
        await EnqueueAsync();
        await _h.DrainAsync();
        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(beta))!.State);
        Assert.Equal(0, await EnqueueAsync()); // nothing re-queues it on its own

        _h.Records.Remove(202); // the look-alike is gone at the provider now (the search itself is cached)
        var codes = await _h.Service().RerunAsync([beta.PublicId], "admin");
        Assert.Equal("ok", codes[beta.PublicId]);
        await _h.DrainAsync();
        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(beta))!.State);
        Assert.False(await _db.Db.MetadataMatchCandidates.AnyAsync(c => c.NodeId == beta.Id));
    }

    // --- Refusals and pausing ---

    [Fact]
    public async Task BudgetSpentMidWork_ReleasesTheRow_AndWaitsForTheNextUtcDay()
    {
        var alpha = await SeriesAsync("Alpha Saga");
        _h.Search["Alpha Saga"] = [new MuJson.Hit(101, "Alpha Saga")];
        _h.Records[101] = MuJson.Get(101, "Alpha Saga");
        await _h.EnableAutomaticAsync();
        var row = await _db.Db.AppSettings.FirstAsync();
        row.MetadataDailyBudget = 1; // the search fits, the GET does not
        await _db.Db.SaveChangesAsync();
        await EnqueueAsync();

        await _h.DrainAsync();
        var queue = await QueueOfAsync(alpha);
        Assert.Equal(QueueState.Pending, queue.State);
        Assert.Equal(0, queue.Attempts);
        Assert.Null(await LinkOfAsync(alpha));
        var wait = await _h.Service().CheckGlobalGateAsync();
        Assert.Equal("budget_exhausted", wait!.Code);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero), wait.Until);

        _h.Time.Advance(TimeSpan.FromDays(1));
        await _h.DrainAsync();
        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(alpha))!.State);
    }

    [Fact]
    public async Task ProviderFailure_CountsAnAttempt_AndGivesUpAfterThree()
    {
        var alpha = await SeriesAsync("Alpha Saga");
        await _h.EnableAutomaticAsync();
        await EnqueueAsync();
        _h.Handler.Respond = _ => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest); // a 4xx: no backoff

        for (var i = 0; i < AutoMatchPolicy.MaxAttempts; i++)
        {
            await _h.DrainAsync();
            _h.Time.Advance(AutoMatchPolicy.FailureRetry + TimeSpan.FromSeconds(1));
        }
        var queue = await QueueOfAsync(alpha);
        Assert.Equal(QueueState.Failed, queue.State);
        Assert.Equal("provider_error", queue.LastErrorCode);
        Assert.Equal(1, (await _db.Db.MetadataMatchRuns.AsNoTracking().SingleAsync()).Failed);
    }

    [Fact]
    public async Task CancelRun_CancelsItsPendingRows()
    {
        await SeriesAsync("Alpha Saga");
        await SeriesAsync("Beta Tale");
        await _h.EnableAutomaticAsync();
        var (_, run) = await _h.Service().StartBulkAsync(_db.LibraryPublicId, new MetadataMatchLibraryRequest(), "admin");
        var (error, cancelled) = await _h.Service().CancelRunAsync(run!.RunId, "admin");
        Assert.Null(error);
        Assert.Equal(MetadataMatchRunStatus.Cancelled, cancelled!.Status);
        Assert.Equal(2, cancelled.Skipped);
        Assert.Equal(0, await _h.DrainAsync());
    }

    // --- Privacy ---

    [Fact]
    public async Task LogSentinel_AutomaticSearchesNeverLogFolderNamesOrTitles()
    {
        const string sentinel = "Zqxvwk";
        var folder = await SeriesAsync($"{sentinel} Saga");
        _h.Search[$"{sentinel} Saga"] = [new MuJson.Hit(701, $"{sentinel} Saga")];
        _h.Records[701] = MuJson.Get(701, $"{sentinel} Saga", alt: [$"{sentinel} Alt Title"]);
        await _h.EnableAutomaticAsync();
        await EnqueueAsync();
        await _h.DrainAsync();

        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(folder))!.State); // the path really ran
        Assert.NotEmpty(_h.Net.Logs.Lines);
        Assert.DoesNotContain(_h.Net.Logs.Lines, l => l.Contains(sentinel, StringComparison.OrdinalIgnoreCase));
    }
}
