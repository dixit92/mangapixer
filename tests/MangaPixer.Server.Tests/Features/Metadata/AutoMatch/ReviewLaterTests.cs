namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.Review;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of "Later" in Needs review (1.33.0): setting a row aside and bringing it back (audited once per change),
/// the order "not Later first (newest first), then Later (set aside first)" with a compound cursor that never repeats or skips a
/// row across the two buckets, the Later filter and count, and every path that decides or re-checks the work clearing it.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class ReviewLaterTests : IAsyncLifetime
{
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private MetadataReviewService Review()
    {
        _db.Db.ChangeTracker.Clear();
        return new MetadataReviewService(_db.Db, _h.Net.Links(), _h.Net.Identify(), _h.Service(), _h.CarryOver(), new AuditService(_db.Db),
            NullLogger<MetadataReviewService>.Instance, _h.Time);
    }

    /// <summary>A folder waiting in Needs review with one stored candidate whose record is stored (accepting it makes no request).</summary>
    private async Task<CatalogNodeEntity> WaitingAsync(string name, string externalId = "301")
    {
        var folder = await _db.AddFolderAsync(null, name);
        if (!await _db.Db.MetadataRecords.AnyAsync(r => r.ExternalId == externalId))
            await _db.AddRecordAsync(externalId, "Synthetic Record " + externalId);
        await _db.AddLinkAsync(folder, null, SeriesLinkState.NeedsReview);
        var now = _h.Time.GetUtcNow();
        _db.Db.MetadataMatchCandidates.Add(new MetadataMatchCandidateEntity
        {
            NodeId = folder.Id,
            Rank = 1,
            Provider = "mangaupdates",
            ExternalId = externalId,
            Title = "Synthetic Record " + externalId,
            TitleScore = 0.8,
            AdjustedScore = 0.8,
            CreatedAt = now,
        });
        _db.Db.MetadataMatchQueue.Add(new MetadataMatchQueueEntity
        {
            NodeId = folder.Id,
            LibraryId = folder.LibraryId,
            State = QueueState.Done,
            Outcome = (int)MatchBand.NeedsReview,
            EnqueuedAt = now,
            CompletedAt = now,
        });
        await _db.Db.SaveChangesAsync();
        return folder;
    }

    private async Task<NodeSeriesLinkEntity?> LinkOfAsync(CatalogNodeEntity node)
    {
        _db.Db.ChangeTracker.Clear();
        return await _db.Db.NodeSeriesLinks.AsNoTracking().FirstOrDefaultAsync(l => l.NodeId == node.Id);
    }

    private async Task<List<string>> LaterAuditsAsync() =>
        await _db.Db.AuditEvents.AsNoTracking().Where(a => a.Action == AuditActions.MetadataReviewLater).OrderBy(a => a.Id).Select(a => a.Result).ToListAsync();

    /// <summary>Every row of Needs review, page by page with the given page size.</summary>
    private async Task<List<string>> WalkAsync(int limit, bool? later = null)
    {
        var names = new List<string>();
        string? cursor = null;
        for (var guard = 0; guard < 50; guard++)
        {
            var (error, page) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, cursor, limit, later: later);
            Assert.Null(error);
            names.AddRange(page!.Items.Select(i => i.DisplayName));
            if (!page.HasMore)
                return names;
            cursor = page.NextCursor;
        }
        Assert.Fail("The pages did not end.");
        return names;
    }

    // --- Set / clear ---

    [Fact]
    public async Task SetAndClear_AreRemembered_AuditedOncePerChange_AndOnlyForRowsInReview()
    {
        var waiting = await WaitingAsync("Alpha Tale");
        var confirmed = await _db.AddFolderAsync(null, "Kept Tale");
        await _db.AddLinkAsync(confirmed, await _db.AddRecordAsync("302", "Kept Tale"));
        var gone = await WaitingAsync("Gone Tale");
        await _db.Db.CatalogNodes.Where(n => n.Id == gone.Id).ExecuteUpdateAsync(s => s.SetProperty(n => n.Availability, (int)CatalogNodeAvailability.Tombstoned));
        var at = _h.Time.GetUtcNow();

        Assert.Equal("ok", await Review().SetLaterAsync(waiting.PublicId, later: true, "admin"));
        Assert.Equal(at, (await LinkOfAsync(waiting))!.LaterAt);
        _h.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("ok", await Review().SetLaterAsync(waiting.PublicId, later: true, "admin")); // already set aside: keeps its first time
        Assert.Equal(at, (await LinkOfAsync(waiting))!.LaterAt);
        Assert.Equal(["set"], await LaterAuditsAsync());

        var (_, page) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, null, 10);
        Assert.Equal(at, page!.Items.Single(i => i.NodeId == waiting.PublicId).LaterAt);
        Assert.Equal(1, (await Review().SummaryAsync(null))!.Later);

        Assert.Equal("ok", await Review().SetLaterAsync(waiting.PublicId, later: false, "admin"));
        Assert.Null((await LinkOfAsync(waiting))!.LaterAt);
        Assert.Equal("ok", await Review().SetLaterAsync(waiting.PublicId, later: false, "admin"));
        Assert.Equal(["set", "clear"], await LaterAuditsAsync());
        Assert.Equal(0, (await Review().SummaryAsync(null))!.Later);

        Assert.Equal("not_in_review", await Review().SetLaterAsync(confirmed.PublicId, later: true, "admin"));
        Assert.Null((await LinkOfAsync(confirmed))!.LaterAt);
        Assert.Equal("not_in_review", await Review().SetLaterAsync((await _db.AddFolderAsync(null, "Plain Tale")).PublicId, later: true, "admin"));
        Assert.Equal("not_found", await Review().SetLaterAsync(gone.PublicId, later: true, "admin"));
        Assert.Equal("not_found", await Review().SetLaterAsync("nope", later: true, "admin"));
        Assert.Equal(["set", "clear"], await LaterAuditsAsync());
    }

    [Fact]
    public async Task Bulk_SetsAndClearsLater_PerRow()
    {
        var a = await WaitingAsync("Alpha Tale");
        var b = await WaitingAsync("Beta Tale");

        var (error, result) = await Review().BulkAsync(new MetadataReviewBulkRequest
        {
            Action = MetadataReviewBulkAction.Later,
            NodeIds = [a.PublicId, b.PublicId, "nope"],
        }, "admin");

        Assert.Null(error);
        Assert.Equal((2, 1), (result!.Succeeded, result.Failed));
        Assert.Equal("not_found", result.Results.Single(r => r.NodeId == "nope").Code);
        Assert.NotNull((await LinkOfAsync(a))!.LaterAt);
        Assert.NotNull((await LinkOfAsync(b))!.LaterAt);

        (_, result) = await Review().BulkAsync(new MetadataReviewBulkRequest { Action = MetadataReviewBulkAction.ClearLater, NodeIds = [b.PublicId] }, "admin");
        Assert.Equal(1, result!.Succeeded);
        Assert.Null((await LinkOfAsync(b))!.LaterAt);
        Assert.Equal(["set", "set", "clear"], await LaterAuditsAsync());
    }

    // --- Order, cursor, filter ---

    [Fact]
    public async Task Order_NotLaterNewestFirst_ThenLaterOldestFirst_AndEveryPageSizeWalksEachRowOnce()
    {
        var rows = new List<CatalogNodeEntity>();
        foreach (var name in new[] { "R1", "R2", "R3", "R4", "R5", "R6" })
            rows.Add(await WaitingAsync(name));
        await Review().SetLaterAsync(rows[1].PublicId, later: true, "admin"); // R2 at t1
        await Review().SetLaterAsync(rows[4].PublicId, later: true, "admin"); // R5 at t1 too (same time: row id breaks the tie)
        _h.Time.Advance(TimeSpan.FromSeconds(30));
        await Review().SetLaterAsync(rows[0].PublicId, later: true, "admin"); // R1 at t2

        string[] expected = ["R6", "R4", "R3", "R2", "R5", "R1"];
        for (var limit = 1; limit <= 7; limit++)
            Assert.Equal(expected, await WalkAsync(limit));

        Assert.Equal(["R2", "R5", "R1"], await WalkAsync(2, later: true));
        Assert.Equal(["R6", "R4", "R3"], await WalkAsync(2, later: false));

        var (_, all) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, null, 3);
        Assert.Equal((6, true), (all!.Total, all.HasMore));
        Assert.Equal("R3", all.Items[^1].DisplayName);
        var (_, onlyLater) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, null, 3, later: true);
        Assert.Equal((3, false), (onlyLater!.Total, onlyLater.HasMore));
        var summary = (await Review().SummaryAsync(_db.LibraryPublicId))!;
        Assert.Equal((6, 3), (summary.NeedsReview, summary.Later));
    }

    [Fact]
    public async Task Cursor_AcrossTheBoundary_AndFromAnEarlierRelease()
    {
        var r1 = await WaitingAsync("R1");
        var r2 = await WaitingAsync("R2");
        var r3 = await WaitingAsync("R3");
        await Review().SetLaterAsync(r1.PublicId, later: true, "admin");

        // The last row that is not set aside ends a page: the next page starts with the first row set aside.
        var (_, first) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, null, 2);
        Assert.Equal(["R3", "R2"], first!.Items.Select(i => i.DisplayName));
        Assert.True(first.HasMore);
        var (_, second) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, first.NextCursor, 2);
        Assert.Equal(["R1"], second!.Items.Select(i => i.DisplayName));
        Assert.False(second.HasMore);

        // A cursor of an earlier release (a plain row id) still pages the rows that are not set aside, then the Later ones.
        var (_, old) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, r3.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), 5);
        Assert.Equal(["R2", "R1"], old!.Items.Select(i => i.DisplayName));

        // A row set aside while an admin pages moves behind the cursor's bucket; nothing is repeated.
        var (_, top) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, null, 1);
        Assert.Equal("R3", top!.Items.Single().DisplayName);
        await Review().SetLaterAsync(r2.PublicId, later: true, "admin");
        var (_, rest) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, top.NextCursor, 5);
        Assert.Equal(["R1", "R2"], rest!.Items.Select(i => i.DisplayName));

        // Garbage is the first page, as before.
        var (_, garbage) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, "Lx_1", 5);
        Assert.Equal(["R3", "R1", "R2"], garbage!.Items.Select(i => i.DisplayName));
    }

    // --- Every decision clears it ---

    private async Task<CatalogNodeEntity> SetAsideAsync(string name, string externalId = "301")
    {
        var folder = await WaitingAsync(name, externalId);
        Assert.Equal("ok", await Review().SetLaterAsync(folder.PublicId, later: true, "admin"));
        return folder;
    }

    [Fact]
    public async Task Accept_AndBulkAcceptTop_Clear()
    {
        var one = await SetAsideAsync("Alpha Tale");
        var two = await SetAsideAsync("Beta Tale");

        Assert.Null((await Review().AcceptAsync(one.PublicId, 1, "admin")).Error);
        var (_, bulk) = await Review().BulkAsync(new MetadataReviewBulkRequest { Action = MetadataReviewBulkAction.AcceptTop, NodeIds = [two.PublicId] }, "admin");
        Assert.Equal(1, bulk!.Succeeded);

        foreach (var node in new[] { one, two })
        {
            var link = (await LinkOfAsync(node))!;
            Assert.Equal(((int)SeriesLinkState.Confirmed, (DateTimeOffset?)null), (link.State, link.LaterAt));
        }
        Assert.Equal(0, _h.Handler.CallCount); // stored records: no request
    }

    [Fact]
    public async Task DontMatch_Bulk_AndSingle_Clear()
    {
        var one = await SetAsideAsync("Alpha Tale");
        var two = await SetAsideAsync("Beta Tale");

        await Review().BulkAsync(new MetadataReviewBulkRequest { Action = MetadataReviewBulkAction.DontMatch, NodeIds = [one.PublicId] }, "admin");
        await _h.Net.Links().SetDontMatchAsync(two.PublicId, "admin");

        foreach (var node in new[] { one, two })
        {
            var link = (await LinkOfAsync(node))!;
            Assert.Equal(((int)SeriesLinkState.DontMatch, (DateTimeOffset?)null), (link.State, link.LaterAt));
        }
    }

    [Fact]
    public async Task IdentifyLink_Relink_Unlink_AndConfirm_Clear()
    {
        var identified = await SetAsideAsync("Alpha Tale");
        var unlinked = await SetAsideAsync("Beta Tale");
        await _db.AddRecordAsync("303", "Another Record");

        // Identify (any record, stored here, so no request).
        var (code, _) = await _h.Net.Identify().LinkAsync(identified.PublicId, new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = "303" }, "admin");
        Assert.Equal(MetadataLinkResultCode.Ok, code);
        var link = (await LinkOfAsync(identified))!;
        Assert.Equal(((int)SeriesLinkState.Confirmed, (DateTimeOffset?)null), (link.State, link.LaterAt));

        // Unlink removes the row: if the work comes back to review it is a new row, not set aside.
        var (_, unlink) = await Review().BulkAsync(new MetadataReviewBulkRequest { Action = MetadataReviewBulkAction.Unlink, NodeIds = [unlinked.PublicId] }, "admin");
        Assert.Equal("ok", unlink!.Results.Single().Code);
        Assert.Null(await LinkOfAsync(unlinked));

        // Confirm (Auto -> Confirmed in one statement) drops a stale stamp too.
        var auto = await _db.AddFolderAsync(null, "Auto Tale");
        await _db.AddLinkAsync(auto, await _db.AddRecordAsync("304", "Auto Tale"), SeriesLinkState.Auto);
        await _db.Db.NodeSeriesLinks.Where(l => l.NodeId == auto.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.LaterAt, DateTimeOffset.UnixEpoch));
        Assert.Equal(1, (await Review().BulkAsync(new MetadataReviewBulkRequest { Action = MetadataReviewBulkAction.Confirm, NodeIds = [auto.PublicId] }, "admin")).Result!.Succeeded);
        link = (await LinkOfAsync(auto))!;
        Assert.Equal(((int)SeriesLinkState.Confirmed, (DateTimeOffset?)null), (link.State, link.LaterAt));
    }

    // --- The matcher's re-check clears it (the row is updated in place) ---

    /// <summary>"Beta Tale" and a look-alike: the matcher sends it to Needs review (close second).</summary>
    private async Task<CatalogNodeEntity> MatchedIntoReviewAsync()
    {
        var beta = await _db.AddFolderAsync(null, "Beta Tale");
        await _db.AddArchiveAsync(beta, "Beta Tale v01");
        await _db.AddArchiveAsync(beta, "Beta Tale v02");
        _h.Search["Beta Tale"] = [new MuJson.Hit(201, "Beta Tale"), new MuJson.Hit(202, "Beta Tale!")];
        _h.Records[201] = MuJson.Get(201, "Beta Tale");
        _h.Records[202] = MuJson.Get(202, "Beta Tale!");
        await _h.EnableAutomaticAsync();
        await _h.Service().EnqueueNewFoldersAsync(_db.LibraryId, DateTimeOffset.UtcNow.AddMinutes(-1));
        await _h.DrainAsync();
        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(beta))!.State);
        Assert.Equal("ok", await Review().SetLaterAsync(beta.PublicId, later: true, "admin"));
        return beta;
    }

    [Fact]
    public async Task Recheck_ThatStillNeedsReview_UpdatesTheRowInPlace_AndClearsLater()
    {
        var beta = await MatchedIntoReviewAsync();
        var rowId = (await LinkOfAsync(beta))!.Id;
        await _db.Db.MetadataMatchQueue.Where(q => q.NodeId == beta.Id).ExecuteUpdateAsync(s => s.SetProperty(q => q.RulesRevision, (int?)null));

        Assert.Equal(1, await _h.Service().QueueOutdatedReviewsAsync());
        Assert.NotNull((await LinkOfAsync(beta))!.LaterAt); // still set aside while it waits for the re-check
        Assert.Equal(1, await _h.DrainAsync());

        var link = (await LinkOfAsync(beta))!;
        Assert.Equal((rowId, (int)SeriesLinkState.NeedsReview, (DateTimeOffset?)null), (link.Id, link.State, link.LaterAt));
        Assert.Equal(0, (await Review().SummaryAsync(null))!.Later);
    }

    [Fact]
    public async Task RerunMatching_ThatLinks_ClearsLater()
    {
        var beta = await MatchedIntoReviewAsync();
        _h.Records.Remove(202); // the look-alike no longer ties

        var (_, rerun) = await Review().BulkAsync(new MetadataReviewBulkRequest { Action = MetadataReviewBulkAction.RerunMatching, NodeIds = [beta.PublicId] }, "admin");
        Assert.Equal(1, rerun!.Succeeded);
        Assert.Equal(1, await _h.DrainAsync());

        var link = (await LinkOfAsync(beta))!;
        Assert.Equal(((int)SeriesLinkState.Auto, (DateTimeOffset?)null), (link.State, link.LaterAt));
    }
}
