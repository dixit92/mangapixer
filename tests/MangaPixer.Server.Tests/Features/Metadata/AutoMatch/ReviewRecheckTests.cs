namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the background re-check of works waiting in Needs review (1.31.0): the rules revision
/// stamped on every scored queue row, the once-per-revision queueing, which rows it must never touch (admin decisions,
/// reach demotions, other states), the gate it waits behind, and that a queued row keeps what the review row showed until
/// it is scored again.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class ReviewRecheckTests : IAsyncLifetime
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

    private async Task<CatalogNodeEntity> SeriesAsync(string name, int archives = 2)
    {
        var folder = await _db.AddFolderAsync(null, name);
        for (var i = 1; i <= archives; i++)
            await _db.AddArchiveAsync(folder, $"{name} v{i:D2}");
        return folder;
    }

    private async Task<NodeSeriesLinkEntity?> LinkOfAsync(CatalogNodeEntity node) =>
        await _db.Db.NodeSeriesLinks.AsNoTracking().FirstOrDefaultAsync(l => l.NodeId == node.Id);

    private async Task<MetadataMatchQueueEntity> QueueOfAsync(CatalogNodeEntity node) =>
        await _db.Db.MetadataMatchQueue.AsNoTracking().SingleAsync(q => q.NodeId == node.Id);

    /// <summary>"Beta Tale" and a look-alike: the matcher sends it to Needs review (close second).</summary>
    private async Task<CatalogNodeEntity> ReviewedSeriesAsync()
    {
        var beta = await SeriesAsync("Beta Tale");
        _h.Search["Beta Tale"] = [new MuJson.Hit(201, "Beta Tale"), new MuJson.Hit(202, "Beta Tale!")];
        _h.Records[201] = MuJson.Get(201, "Beta Tale");
        _h.Records[202] = MuJson.Get(202, "Beta Tale!");
        await _h.EnableAutomaticAsync();
        await _h.Service().EnqueueNewFoldersAsync(_db.LibraryId, _since);
        await _h.DrainAsync();
        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(beta))!.State);
        return beta;
    }

    /// <summary>Makes a scored row look like one scored by an earlier release (no stamp).</summary>
    private async Task MakeOlderAsync(params CatalogNodeEntity[] nodes)
    {
        var ids = nodes.Select(n => n.Id).ToList();
        await _db.Db.MetadataMatchQueue.Where(q => ids.Contains(q.NodeId)).ExecuteUpdateAsync(s => s.SetProperty(q => q.RulesRevision, (int?)null));
        _db.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task EveryScoredRow_IsStampedWithTheCurrentRevision_SoNothingJustScoredIsCheckedAgain()
    {
        var beta = await ReviewedSeriesAsync();

        Assert.Equal(MatcherRules.Revision, (await QueueOfAsync(beta)).RulesRevision);
        Assert.Equal(0, await _h.Service().QueueOutdatedReviewsAsync());
        Assert.Equal(1, await _db.Db.MetadataMatchQueue.CountAsync(q => q.State == QueueState.Done));
    }

    [Fact]
    public async Task StaleReviewRow_IsQueuedOnce_KeepsItsResultMeanwhile_AndIsScoredUnderTheNewRules()
    {
        var beta = await ReviewedSeriesAsync();
        await MakeOlderAsync(beta);
        var before = await QueueOfAsync(beta);
        var calls = _h.Handler.CallCount;

        Assert.Equal(1, await _h.Service().QueueOutdatedReviewsAsync());
        Assert.Equal(0, await _h.Service().QueueOutdatedReviewsAsync()); // already waiting: once per revision, not once per pass
        Assert.Equal(calls, _h.Handler.CallCount); // queueing contacts nobody

        var queued = await QueueOfAsync(beta);
        Assert.Equal((QueueState.Pending, QueueReason.Recheck), (queued.State, queued.Reason));
        Assert.Equal(before.Outcome, queued.Outcome); // the review row still shows what it showed
        Assert.Equal(before.OutcomeReasons, queued.OutcomeReasons);
        Assert.Equal(before.CompletedAt, queued.CompletedAt);
        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(beta))!.State);
        Assert.Equal(2, await _db.Db.MetadataMatchCandidates.CountAsync(c => c.NodeId == beta.Id));
        var run = await _db.Db.MetadataMatchRuns.AsNoTracking().SingleAsync(r => r.Id == queued.RunId);
        Assert.Equal((int)MetadataMatchRunTrigger.Recheck, run.Trigger);
        Assert.Equal(1, run.Queued);
        Assert.Equal(1, await _h.Service().RecheckPendingAsync(null));
        Assert.Equal(1, await _h.Service().RecheckPendingAsync(_db.LibraryId));
        Assert.Equal(0, await _h.Service().RecheckPendingAsync(_db.LibraryId + 1));

        _h.Records.Remove(202); // under today's rules the look-alike no longer ties (the search itself is cached)
        Assert.Equal(1, await _h.DrainAsync());

        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(beta))!.State);
        var done = await QueueOfAsync(beta);
        Assert.Equal(MatcherRules.Revision, done.RulesRevision);
        Assert.Equal(QueueState.Done, done.State);
        Assert.Equal(0, await _h.Service().RecheckPendingAsync(null));
        Assert.Equal(0, await _h.Service().QueueOutdatedReviewsAsync()); // scored under the current revision now
    }

    [Fact]
    public async Task ARecheckThatStillNeedsReview_IsNotQueuedAgainUnderTheSameRevision()
    {
        var beta = await ReviewedSeriesAsync();
        await MakeOlderAsync(beta);

        Assert.Equal(1, await _h.Service().QueueOutdatedReviewsAsync());
        Assert.Equal(1, await _h.DrainAsync());

        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(beta))!.State);
        Assert.Equal(MatcherRules.Revision, (await QueueOfAsync(beta)).RulesRevision);
        Assert.Equal(0, await _h.Service().QueueOutdatedReviewsAsync());
        Assert.Equal(0, await _h.Service().QueueOutdatedReviewsAsync());
    }

    [Fact]
    public async Task NextRevision_ChecksTheWorkAgain()
    {
        var beta = await ReviewedSeriesAsync();
        // A row scored under an EARLIER numbered revision is older too (the constant only goes up).
        await _db.Db.MetadataMatchQueue.Where(q => q.NodeId == beta.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.RulesRevision, MatcherRules.Revision - 1));
        _db.Db.ChangeTracker.Clear();

        Assert.Equal(1, await _h.Service().QueueOutdatedReviewsAsync());
    }

    [Fact]
    public async Task ReviewFirstRow_StaysInReview_EvenWhenTheNewRulesWouldLinkIt()
    {
        var alpha = await SeriesAsync("Alpha Saga");
        _h.Search["Alpha Saga"] = [new MuJson.Hit(101, "Alpha Saga")];
        _h.Records[101] = MuJson.Get(101, "Alpha Saga");
        await _h.EnableAutomaticAsync();
        var (error, _) = await _h.Service().StartBulkAsync(_db.LibraryPublicId, new MetadataMatchLibraryRequest { ReviewFirst = true }, "admin");
        Assert.Null(error);
        await _h.DrainAsync();
        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(alpha))!.State); // the admin asked to review everything once
        await MakeOlderAsync(alpha);

        Assert.Equal(1, await _h.Service().QueueOutdatedReviewsAsync());
        await _h.DrainAsync();

        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(alpha))!.State);
        Assert.Equal(MatcherRules.Revision, (await QueueOfAsync(alpha)).RulesRevision);
        // 1.40.0: the decision line names the band WRITTEN (it said "Auto" while the row stayed in review), and the score.
        var decided = _h.Net.Logs.Lines.Where(l => l.Contains($" {LogEvents.Metadata.AutoMatchDecided} ") && l.Contains($"node {alpha.Id}:")).ToList();
        Assert.Equal(2, decided.Count);
        Assert.All(decided, l => Assert.Contains($"node {alpha.Id}: NeedsReview, scored Auto (", l));
    }

    [Fact]
    public async Task OnlyMatcherScoredReviewRows_AreQueued_NeverAnAdminsDecisionOrAnythingElse()
    {
        var eligible = await SeriesAsync("Eligible Saga");
        var confirmed = await SeriesAsync("Confirmed Saga");
        var dontMatch = await SeriesAsync("Dont Saga");
        var reach = await SeriesAsync("Reach Saga");
        var handLinked = await SeriesAsync("Hand Saga");
        var gone = await SeriesAsync("Gone Saga");
        var current = await SeriesAsync("Current Saga");
        var unmatched = await SeriesAsync("Unmatched Saga");
        var waiting = await SeriesAsync("Waiting Saga");
        var failed = await SeriesAsync("Failed Saga");
        var otherLibrary = await _db.AddLibraryAsync("fetchoff", "Fetch Off");
        var elsewhere = await _db.AddFolderAsync(null, "Elsewhere Saga", otherLibrary.Id);

        var record = await _db.AddRecordAsync("500", "Some Record");
        await Seed(eligible, SeriesLinkState.NeedsReview, MatchBand.NeedsReview);
        await Seed(confirmed, SeriesLinkState.Confirmed, MatchBand.NeedsReview, record); // an admin confirmed it
        await Seed(dontMatch, SeriesLinkState.DontMatch, MatchBand.NeedsReview);
        await Seed(reach, SeriesLinkState.NeedsReview, MatchBand.NeedsReview, reasons: MatchReason.ReachConflict); // demoted by the reach check
        await Seed(handLinked, SeriesLinkState.NeedsReview, MatchBand.NeedsReview, method: MetadataMatchMethod.Search); // not written by the matcher
        await Seed(gone, SeriesLinkState.NeedsReview, MatchBand.NeedsReview);
        await Seed(current, SeriesLinkState.NeedsReview, MatchBand.NeedsReview, revision: MatcherRules.Revision);
        await Seed(unmatched, null, MatchBand.Unmatched); // its own retry ladder
        await Seed(waiting, SeriesLinkState.NeedsReview, MatchBand.NeedsReview, state: QueueState.Pending);
        await Seed(failed, SeriesLinkState.NeedsReview, MatchBand.NeedsReview, state: QueueState.Failed);
        await Seed(elsewhere, SeriesLinkState.NeedsReview, MatchBand.NeedsReview); // a library whose Fetch switch is off
        await _db.Db.CatalogNodes.Where(n => n.Id == gone.Id).ExecuteUpdateAsync(s => s.SetProperty(n => n.Availability, (int)CatalogNodeAvailability.Tombstoned));
        _db.Db.ChangeTracker.Clear();
        await _h.EnableAutomaticAsync();

        Assert.Equal(1, await _h.Service().QueueOutdatedReviewsAsync());

        var recheck = await _db.Db.MetadataMatchQueue.AsNoTracking().Where(q => q.Reason == QueueReason.Recheck).Select(q => q.NodeId).ToListAsync();
        Assert.Equal([eligible.Id], recheck);
        Assert.Equal((int)SeriesLinkState.Confirmed, (await LinkOfAsync(confirmed))!.State);
        Assert.Equal(QueueState.Failed, (await QueueOfAsync(failed)).State);

        async Task Seed(CatalogNodeEntity node, SeriesLinkState? link, MatchBand band, MetadataRecordEntity? linked = null,
            MatchReason reasons = MatchReason.None, MetadataMatchMethod method = MetadataMatchMethod.Auto, int? revision = null, int state = QueueState.Done)
        {
            if (link is { } l)
                _db.Db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
                {
                    NodeId = node.Id,
                    LibraryId = node.LibraryId,
                    State = (int)l,
                    RecordId = linked?.Id,
                    MatchMethod = (int)method,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            _db.Db.MetadataMatchQueue.Add(new MetadataMatchQueueEntity
            {
                NodeId = node.Id,
                LibraryId = node.LibraryId,
                State = state,
                Level = (int)MatchLevel.Folder,
                WorkClass = (int)WorkClass.Series,
                Outcome = (int)band,
                OutcomeReasons = (int)reasons,
                RulesRevision = revision,
                EnqueuedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
            });
            await _db.Db.SaveChangesAsync();
        }
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("consent")]
    public async Task NothingIsQueued_WithoutAutomaticMatching_OrWithItsCurrentConsent(string what)
    {
        var beta = await ReviewedSeriesAsync();
        var calls = _h.Handler.CallCount;
        await MakeOlderAsync(beta);
        if (what == "switch")
            await _h.EnableAutomaticAsync(automatic: false);
        else
            await _h.EnableAutomaticAsync(autoConsentVersion: MetadataAutoConsent.CurrentVersion - 1);

        Assert.Equal(0, await _h.Service().QueueOutdatedReviewsAsync());
        Assert.Equal(QueueState.Done, (await QueueOfAsync(beta)).State);
        Assert.Equal(calls, _h.Handler.CallCount); // only the first scoring ever reached the provider
    }

    [Fact]
    public async Task Recheck_WaitsBehindEverythingElse_AndBehindTheGate()
    {
        var beta = await ReviewedSeriesAsync();
        await MakeOlderAsync(beta);
        Assert.Equal(1, await _h.Service().QueueOutdatedReviewsAsync());
        var fresh = await SeriesAsync("Fresh Saga");
        Assert.Equal(1, await _h.Service().EnqueueNewFoldersAsync(_db.LibraryId, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var first = await _h.Service().LeaseNextAsync("w");
        Assert.Equal(fresh.Id, first!.NodeId); // a new folder goes before a re-check

        Assert.Null(await _h.Service().CheckGlobalGateAsync());
        await _h.EnableAutomaticAsync(automatic: false);
        var closed = await _h.Service().CheckGlobalGateAsync();
        Assert.Equal("automatic_off", closed!.Code); // the worker's gate stops the re-check like any automatic work
    }
}
