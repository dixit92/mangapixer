namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Collections;
using com.lifepixer.mangapixer.Server.Features.Metadata.Review;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of "Collection about" (1.34.0): set / clear, the folder's own series information and the inheritance stop,
/// the Content side effect, the works inside queued and searched with the series as their parody, the nearest-wins matching rule with
/// its Don't match / Needs review exceptions (selector, enqueue and re-run), retirement below a series that stops at a collection, and
/// the review suggestion with "Accept as a collection". Synthetic names and provider answers only.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class CollectionAboutServiceTests : IAsyncLifetime
{
    private const string SeriesId = "9001";
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;
    private MetadataRecordEntity _series = null!;

    private static readonly string[] Doujins =
    [
        "(Event 1) [Circle One (Artist A)] Moonlit Promise [English].cbz",
        "[Circle Two (Artist B)] Summer Lesson [English].cbz",
        "Circle Three] Rainy Day Story.cbz",
        "[Artist C] After School.cbz",
    ];

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
        _series = await _db.AddRecordAsync(SeriesId, "Starlight Academy");
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private MetadataAutoMatchService Matcher() => _h.ServiceWithRealMatcher();

    private CollectionAboutService Collections()
    {
        _db.Db.ChangeTracker.Clear();
        var matcher = Matcher();
        return new CollectionAboutService(_db.Db, _h.Net.Identify(), _h.Net.Links(),
            new MetadataFolderContentService(_db.Db, new AuditService(_db.Db), NullLogger<MetadataFolderContentService>.Instance, [new WorkDetector()], matcher),
            matcher);
    }

    private MetadataReviewService Review()
    {
        _db.Db.ChangeTracker.Clear();
        return new MetadataReviewService(_db.Db, _h.Net.Links(), _h.Net.Identify(), Matcher(), _h.CarryOver(), new AuditService(_db.Db),
            NullLogger<MetadataReviewService>.Instance, _h.Time, Collections());
    }

    private async Task<CatalogNodeEntity> DoujinFolderAsync(CatalogNodeEntity? parent = null, string name = "Starlight Academy")
    {
        var folder = await _db.AddFolderAsync(parent, name);
        foreach (var n in Doujins)
            await _db.AddArchiveAsync(folder, n);
        return folder;
    }

    private Task<CollectionAboutResultDto?> SetAsync(CatalogNodeEntity folder, bool content = true) =>
        Collections().SetAsync(folder.PublicId, new SetCollectionAboutRequest { Provider = "mangaupdates", ExternalId = SeriesId, SetDoujinContent = content }, "admin")
            .ContinueWith(t => t.Result.Result);

    private async Task<NodeSeriesLinkEntity?> LinkOfAsync(CatalogNodeEntity node) =>
        await _db.Db.NodeSeriesLinks.AsNoTracking().FirstOrDefaultAsync(l => l.NodeId == node.Id);

    private async Task<List<long>> QueuedAsync() =>
        await _db.Db.MetadataMatchQueue.AsNoTracking().OrderBy(q => q.NodeId).Select(q => q.NodeId).ToListAsync();

    private async Task<List<CatalogNodeEntity>> ArchivesOfAsync(CatalogNodeEntity folder) =>
        await _db.Db.CatalogNodes.AsNoTracking().Where(n => n.ParentId == folder.Id).OrderBy(n => n.SortKey).ToListAsync();

    // --- Set / clear ---

    [Fact]
    public async Task Set_MarksTheFolder_SetsTheContent_QueuesItsWorks_AndIsAudited()
    {
        await _h.EnableAutomaticAsync();
        var folder = await DoujinFolderAsync();

        var result = await SetAsync(folder);

        Assert.NotNull(result);
        var link = await LinkOfAsync(folder);
        Assert.Equal((int)SeriesLinkState.CollectionAbout, link!.State);
        Assert.Equal(_series.Id, link.RecordId);
        Assert.True(result!.ContentSet);
        Assert.Equal((int)MetadataFolderContent.DoujinshiAndAdultOneShots,
            (await _db.Db.FolderMetadataContents.AsNoTracking().SingleAsync(c => c.NodeId == folder.Id)).Content);
        // Every archive is a work of its own; the folder is not one.
        Assert.Equal(Doujins.Length, result.Queued);
        Assert.Equal((await ArchivesOfAsync(folder)).Select(a => a.Id).Order().ToList(), await QueuedAsync());
        var run = await _db.Db.MetadataMatchRuns.AsNoTracking().SingleAsync();
        Assert.Equal((int)MetadataMatchRunTrigger.Rerun, run.Trigger);
        Assert.Contains(await _db.Db.AuditEvents.Select(a => a.Action).ToListAsync(), a => a == AuditActions.MetadataCollection);
        Assert.Equal(0, _h.Handler.CallCount); // the record was stored: no request
    }

    [Fact]
    public async Task Set_WithAutomaticMatchingOff_QueuesNothing_MatchLibraryNowFindsTheWorks()
    {
        var folder = await DoujinFolderAsync();
        var result = await SetAsync(folder, content: false);

        Assert.Equal(0, result!.Queued);
        Assert.False(result.ContentSet);
        Assert.Empty(await QueuedAsync());
        var (works, _, _) = await Matcher().DetectLibraryAsync(_db.LibraryId, retryUnmatched: false, default);
        Assert.Equal(Doujins.Length, works.Count);
        Assert.All(works, w => Assert.Equal(MatchLevel.Archive, w.Level));
    }

    [Fact]
    public async Task Set_OnAnArchive_IsRefused()
    {
        var folder = await DoujinFolderAsync();
        var archive = (await ArchivesOfAsync(folder))[0];
        var (code, _) = await Collections().SetAsync(archive.PublicId,
            new SetCollectionAboutRequest { Provider = "mangaupdates", ExternalId = SeriesId }, "admin");
        Assert.Equal(MetadataLinkResultCode.NotAFolder, code);
        Assert.Null(await LinkOfAsync(archive));
    }

    [Fact]
    public async Task Set_KeepsTheRowsBelow_AndReplacesTheFoldersOwnReviewRow()
    {
        await _h.EnableAutomaticAsync();
        var folder = await DoujinFolderAsync();
        await _db.AddLinkAsync(folder, null, SeriesLinkState.NeedsReview);
        _db.Db.MetadataMatchCandidates.Add(new MetadataMatchCandidateEntity
        {
            NodeId = folder.Id,
            Rank = 1,
            Provider = "mangaupdates",
            ExternalId = SeriesId,
            Title = "Starlight Academy",
            TitleScore = 1,
            AdjustedScore = 1,
        });
        var archives = await ArchivesOfAsync(folder);
        var other = await _db.AddRecordAsync("9002", "Summer Lesson");
        await _db.AddLinkAsync(archives[1], other, SeriesLinkState.Auto);
        await _db.AddLinkAsync(archives[2], null, SeriesLinkState.DontMatch);

        var result = await SetAsync(folder);

        Assert.Equal((int)SeriesLinkState.CollectionAbout, (await LinkOfAsync(folder))!.State);
        Assert.Empty(await _db.Db.MetadataMatchCandidates.AsNoTracking().Where(c => c.NodeId == folder.Id).ToListAsync());
        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(archives[1]))!.State);
        Assert.Equal((int)SeriesLinkState.DontMatch, (await LinkOfAsync(archives[2]))!.State);
        Assert.Equal(Doujins.Length - 2, result!.Queued); // archives with their own row are not works again
    }

    [Fact]
    public async Task Clear_RemovesOnlyACollectionRow_AndKeepsTheContent()
    {
        var folder = await DoujinFolderAsync();
        await SetAsync(folder);
        var dontMatch = await _db.AddFolderAsync(null, "Elsewhere");
        await _db.AddLinkAsync(dontMatch, null, SeriesLinkState.DontMatch);

        Assert.Equal(MetadataLinkResultCode.Ok, (await Collections().ClearAsync(dontMatch.PublicId, "admin")).Code);
        Assert.NotNull(await LinkOfAsync(dontMatch));

        var (code, change) = await Collections().ClearAsync(folder.PublicId, "admin");
        Assert.Equal(MetadataLinkResultCode.Ok, code);
        Assert.Equal(SeriesLinkState.CollectionAbout, change!.Previous!.State);
        Assert.Null(await LinkOfAsync(folder));
        Assert.True(await _db.Db.FolderMetadataContents.AnyAsync(c => c.NodeId == folder.Id));
        Assert.Contains(await _db.Db.AuditEvents.Select(a => a.Action).ToListAsync(), a => a == AuditActions.MetadataCollectionClear);
        Assert.False(await _db.Db.MetadataRecords.AnyAsync(r => r.Id == _series.Id)); // no other link held it: removed as after an unlink
    }

    // --- Series information: the folder shows its series, nothing below inherits it (promised to MangaList) ---

    [Fact]
    public async Task Resolver_ShowsTheSeriesOnTheFolderOnly_WithoutNumbers_AndStopsInheritance()
    {
        var series = await _db.AddFolderAsync(null, "Starlight Academy (the manga)");
        await _db.AddLinkAsync(series, _series);
        var folder = await DoujinFolderAsync(series, "Fan Works");
        await SetAsync(folder, content: false);
        _db.Db.ChangeTracker.Clear();

        var own = await _db.ResolveAsync(folder);
        Assert.Equal(SeriesInfoState.CollectionAbout, own.State);
        Assert.Equal("Starlight Academy", own.Title);
        Assert.Equal("A synthetic web description.", own.Description);
        Assert.Null(own.OriginStatus);
        Assert.Null(own.OriginVolumes);
        Assert.Null(own.LatestChapter);
        Assert.Equal(SeriesLinkState.CollectionAbout, own.Link!.State);
        Assert.False(own.Link.Inherited);

        var archive = (await ArchivesOfAsync(folder))[0];
        var below = await _db.ResolveAsync(archive);
        Assert.Equal(SeriesInfoState.None, below.State); // not the series above, not the collection's series
        Assert.Null(below.Web);
        Assert.Equal(SeriesLinkState.CollectionAbout, below.Link!.State);
        Assert.True(below.Link.Inherited);

        Assert.Null(await _db.Resolver().ResolveWebRecordAsync(folder));
        Assert.Equal(_series.Id, (await _db.Resolver().ResolveShownRecordAsync(folder))!.Id);
        Assert.Null(await _db.Resolver().ResolveShownRecordAsync(archive));
    }

    // --- Matching below: nearest wins, Don't match / Needs review above block ---

    [Fact]
    public async Task InsideALinkedSeries_TheCollectionReopensMatching()
    {
        await _h.EnableAutomaticAsync();
        var series = await _db.AddFolderAsync(null, "Starlight Academy (the manga)");
        await _db.AddLinkAsync(series, _series);
        var folder = await DoujinFolderAsync(series, "Fan Works");

        Assert.Equal(Doujins.Length, (await SetAsync(folder))!.Queued);
        var (works, _, _) = await Matcher().DetectLibraryAsync(_db.LibraryId, retryUnmatched: false, default);
        Assert.Empty(works); // already queued; and the linked series itself is never a work again

        var archive = (await ArchivesOfAsync(folder))[0];
        await _db.Db.MetadataMatchQueue.Where(q => q.NodeId == archive.Id).ExecuteDeleteAsync();
        var codes = await Matcher().RerunAsync([archive.PublicId], "admin");
        Assert.Equal("ok", codes[archive.PublicId]);
    }

    [Fact]
    public async Task NewDoujinAfterAScan_InACollection_UnderACategory_IsQueuedOnItsOwn_ButWaitsWhileTheFolderIsUndecided()
    {
        // The owner's layout (1.34.0): <library>/Series/<Series Title>/<new doujin>; "Series" is a category folder without a link.
        await _h.EnableAutomaticAsync();
        var category = await _db.AddFolderAsync(null, "Series");
        var marked = await DoujinFolderAsync(category, "Starlight Academy");
        var waiting = await DoujinFolderAsync(category, "Moonlight Academy");
        await _db.AddLinkAsync(waiting, null, SeriesLinkState.NeedsReview); // still one folder-level work waiting in review
        Assert.Equal(Doujins.Length, (await SetAsync(marked))!.Queued);
        // The first matching pass is done: its rows stay, finished (here: waiting for review).
        await _db.Db.MetadataMatchQueue.ExecuteUpdateAsync(q => q.SetProperty(r => r.State, QueueState.Done).SetProperty(r => r.Outcome, (int)MatchBand.NeedsReview));

        var since = DateTimeOffset.UtcNow.AddSeconds(1);
        await Task.Delay(1100);
        var inMarked = await _db.AddArchiveAsync(marked, "[Circle Five (Artist E)] New Story [English].cbz");
        var inWaiting = await _db.AddArchiveAsync(waiting, "[Circle Six] Another New Story.cbz");

        // After the scan: the new doujin in the collection is a work of its own; the one in the undecided folder waits for that folder.
        Assert.Equal(1, await Matcher().EnqueueNewFoldersAsync(_db.LibraryId, since));
        Assert.Equal([inMarked.Id], await _db.Db.MetadataMatchQueue.AsNoTracking()
            .Where(q => q.State == QueueState.Pending).Select(q => q.NodeId).ToListAsync());

        // Deciding the waiting folder as a collection queues everything inside it, the new doujin included.
        await _db.Db.MetadataMatchQueue.ExecuteDeleteAsync();
        Assert.Equal(Doujins.Length + 1, (await SetAsync(waiting))!.Queued);
        Assert.Contains(inWaiting.Id, await QueuedAsync());
    }

    [Theory]
    [InlineData(SeriesLinkState.DontMatch)]
    [InlineData(SeriesLinkState.NeedsReview)]
    public async Task BelowADontMatchOrAWaitingFolder_NothingIsMatched_CollectionIncluded(SeriesLinkState above)
    {
        await _h.EnableAutomaticAsync();
        var top = await _db.AddFolderAsync(null, "Shelf");
        await _db.AddLinkAsync(top, null, above);
        var series = await _db.AddFolderAsync(top, "Starlight Academy (the manga)");
        await _db.AddLinkAsync(series, _series);
        var folder = await DoujinFolderAsync(series, "Fan Works");

        Assert.Equal(0, (await SetAsync(folder))!.Queued);
        var (works, _, _) = await Matcher().DetectLibraryAsync(_db.LibraryId, retryUnmatched: false, default);
        Assert.Empty(works);
        var archive = (await ArchivesOfAsync(folder))[0];
        Assert.Equal("covered_by_folder", (await Matcher().RerunAsync([archive.PublicId], "admin"))[archive.PublicId]);
    }

    [Fact]
    public async Task BelowALinkedSeriesWithoutACollection_ArchivesStayCovered()
    {
        var series = await _db.AddFolderAsync(null, "Starlight Academy (the manga)");
        await _db.AddLinkAsync(series, _series);
        var folder = await DoujinFolderAsync(series, "Fan Works");
        var archive = (await ArchivesOfAsync(folder))[0];

        var (works, _, _) = await Matcher().DetectLibraryAsync(_db.LibraryId, retryUnmatched: false, default);
        Assert.Empty(works);
        Assert.Equal("covered_by_folder", (await Matcher().RerunAsync([archive.PublicId], "admin"))[archive.PublicId]);
    }

    [Fact]
    public async Task LinkingTheSeriesAbove_DoesNotRetireTheCollectionsWorks()
    {
        var series = await _db.AddFolderAsync(null, "Starlight Academy (the manga)");
        var folder = await DoujinFolderAsync(series, "Fan Works");
        await SetAsync(folder, content: false);
        var archive = (await ArchivesOfAsync(folder))[0];
        await _db.AddLinkAsync(archive, null, SeriesLinkState.NeedsReview);
        var loose = await _db.AddArchiveAsync(series, "Starlight Academy v01.cbz");
        await _db.AddLinkAsync(loose, null, SeriesLinkState.NeedsReview);

        _db.Db.ChangeTracker.Clear();
        await _h.Net.Links().LinkAsync(series.PublicId, new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = SeriesId }, "admin");

        Assert.NotNull(await LinkOfAsync(archive)); // inside the collection: kept
        Assert.Null(await LinkOfAsync(loose));      // directly below the series: retired as before
    }

    // --- The works inside are searched with the series as their parody ---

    [Fact]
    public async Task WorksInside_AreSearchedAsDoujinshiOfTheSeries()
    {
        await _h.EnableAutomaticAsync();
        var folder = await DoujinFolderAsync();
        _h.Search["Starlight Academy dj - Summer Lesson"] = [new MuJson.Hit(9101, "Starlight Academy dj - Summer Lesson", Type: "Doujinshi")];
        _h.Records[9101] = MuJson.Get(9101, "Starlight Academy dj - Summer Lesson", type: "Doujinshi");
        await SetAsync(folder);
        var summer = (await ArchivesOfAsync(folder)).Single(a => a.DisplayName.Contains("Summer", StringComparison.Ordinal));
        await _db.Db.MetadataMatchQueue.Where(q => q.NodeId != summer.Id).ExecuteDeleteAsync();

        for (var i = 0; i < 3; i++)
        {
            _db.Db.ChangeTracker.Clear();
            var service = Matcher();
            if (await service.LeaseNextAsync("test") is not { } row)
                break;
            await service.ProcessAsync(row);
        }

        var searches = _h.Handler.Seen.Where(r => r.Uri.AbsolutePath == "/v1/series/search")
            .Select(r => JsonDocument.Parse(r.Body!).RootElement)
            .ToList();
        Assert.Equal("Summer Lesson", searches[0].GetProperty("search").GetString());
        Assert.Equal("Starlight Academy dj - Summer Lesson", searches[1].GetProperty("search").GetString());
        var link = await LinkOfAsync(summer);
        Assert.NotNull(link);
        Assert.Contains(link!.State, new[] { (int)SeriesLinkState.Auto, (int)SeriesLinkState.NeedsReview });
        var top = link.RecordId is { } rid
            ? (await _db.Db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Id == rid)).ExternalId
            : (await _db.Db.MetadataMatchCandidates.AsNoTracking().SingleAsync(c => c.NodeId == summer.Id && c.Rank == 1)).ExternalId;
        Assert.Equal("9101", top);
    }

    // --- The review suggestion and "Accept as a collection" ---

    private async Task<CatalogNodeEntity> WaitingFolderAsync(string candidateFormat = "Comic", double score = 1.0)
    {
        var folder = await DoujinFolderAsync();
        await _db.AddLinkAsync(folder, null, SeriesLinkState.NeedsReview);
        _db.Db.MetadataMatchCandidates.Add(new MetadataMatchCandidateEntity
        {
            NodeId = folder.Id,
            Rank = 1,
            Provider = "mangaupdates",
            ExternalId = SeriesId,
            Title = "Starlight Academy",
            Format = (int)Enum.Parse<MetadataFormat>(candidateFormat),
            TitleScore = score,
            AdjustedScore = score,
        });
        _db.Db.MetadataMatchQueue.Add(new MetadataMatchQueueEntity
        {
            NodeId = folder.Id,
            LibraryId = folder.LibraryId,
            State = QueueState.Done,
            Reason = QueueReason.NewFolder,
            Level = (int)MatchLevel.ReviewOnly,
            Outcome = (int)MatchBand.NeedsReview,
            WorkClass = (int)WorkClass.Ambiguous,
            EnqueuedAt = DateTimeOffset.UtcNow,
        });
        await _db.Db.SaveChangesAsync();
        return folder;
    }

    [Fact]
    public async Task Review_SuggestsTheCollection_AndAcceptingItMarksTheFolder()
    {
        await _h.EnableAutomaticAsync();
        var folder = await WaitingFolderAsync();

        var (_, page) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, null, 50);
        var hint = page!.Items.Single().Collection;
        Assert.NotNull(hint);
        Assert.Equal((1, SeriesId, "Starlight Academy"), (hint!.Rank, hint.ExternalId, hint.Title));

        var (error, result) = await Review().AcceptCollectionAsync(folder.PublicId, hint.Rank, "admin");
        Assert.Null(error);
        Assert.Equal(Doujins.Length, result!.Queued);
        Assert.Equal((int)SeriesLinkState.CollectionAbout, (await LinkOfAsync(folder))!.State);
        var summary = await Review().SummaryAsync(null);
        Assert.Equal((0, 1), (summary!.NeedsReview, summary.Collections));
        var (_, collections) = await Review().ListAsync(MetadataReviewTab.Collections, null, null, 50);
        Assert.Equal(folder.PublicId, collections!.Items.Single().NodeId);
        Assert.Equal(SeriesLinkState.CollectionAbout, collections.Items.Single().Link!.State);
        Assert.Equal("review", (await _db.Db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == AuditActions.MetadataCollection)).Result);
    }

    [Theory]
    [InlineData("Doujinshi", 1.0)]
    [InlineData("Comic", 0.8)]
    public async Task Review_NoSuggestion_ForADoujinshiOrWeakCandidate(string format, double score)
    {
        await WaitingFolderAsync(format, score);
        var (_, page) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, null, 50);
        Assert.Null(page!.Items.Single().Collection);
    }

    [Fact]
    public async Task Review_BulkAccept_UsesEachRowsSuggestion()
    {
        var folder = await WaitingFolderAsync();
        var plain = await _db.AddFolderAsync(null, "Plain Saga");
        await _db.AddArchiveAsync(plain, "Plain Saga v01.cbz");
        await _db.AddLinkAsync(plain, null, SeriesLinkState.NeedsReview);

        var (error, result) = await Review().BulkAsync(new MetadataReviewBulkRequest
        {
            Action = MetadataReviewBulkAction.AcceptCollection,
            NodeIds = [folder.PublicId, plain.PublicId],
        }, "admin");

        Assert.Null(error);
        Assert.Equal("ok", result!.Results.Single(r => r.NodeId == folder.PublicId).Code);
        Assert.Equal("no_suggestion", result.Results.Single(r => r.NodeId == plain.PublicId).Code);
        Assert.Equal((int)SeriesLinkState.CollectionAbout, (await LinkOfAsync(folder))!.State);
        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(plain))!.State);
    }
}
