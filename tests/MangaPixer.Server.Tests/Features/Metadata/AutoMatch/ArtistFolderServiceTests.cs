namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Artists;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Declared;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Review;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of "Artist folder" (1.37.0) - the owner's three outcomes: (1) the folder is marked (never a work, nothing
/// inside inherits from it, automatic matching keeps working inside it archive by archive, also below a linked series; not below a
/// Don't match or a waiting folder); (2) the artist is declared as the folder's creator (default its name, "Story &amp; art"), keeping
/// its other declared facts, and raises the record by that artist; (3) the folder-level review row goes and the works inside are queued
/// at once (finished unmatched works again). Plus clear, the series information, the review tab, accept and bulk. Synthetic names.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class ArtistFolderServiceTests : IAsyncLifetime
{
    private const string Artist = "Beta Painter";
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;

    private static readonly string[] Works =
    [
        "Qzv Harbor Tale.cbz",
        "Qzv Lantern Road.cbz",
        "Qzv Quiet Orchard.cbz",
    ];

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

    private MetadataAutoMatchService Matcher() => _h.ServiceWithRealMatcher(new DeclaredFactsReader(_db.Db));

    private DeclaredFactsService Declared() => new(_db.Db, new AuditService(_db.Db), _db.Settings(), _db.Resolver(),
        new MetadataProviderRegistry([]), NullLogger<DeclaredFactsService>.Instance, TimeProvider.System,
        new com.lifepixer.mangapixer.Server.Features.Metadata.Authors.NoAuthorAliases());

    private ArtistFolderService Artists()
    {
        _db.Db.ChangeTracker.Clear();
        return new ArtistFolderService(_db.Db, _h.Net.Links(), Declared(), Matcher());
    }

    private MetadataReviewService Review()
    {
        _db.Db.ChangeTracker.Clear();
        return new MetadataReviewService(_db.Db, _h.Net.Links(), _h.Net.Identify(), Matcher(), _h.CarryOver(), new AuditService(_db.Db),
            NullLogger<MetadataReviewService>.Instance, _h.Time, artists: Artists());
    }

    private async Task<CatalogNodeEntity> ArtistFolderAsync(CatalogNodeEntity? parent = null, string name = Artist, IEnumerable<string>? works = null)
    {
        var folder = await _db.AddFolderAsync(parent, name);
        foreach (var n in works ?? Works)
            await _db.AddArchiveAsync(folder, n);
        return folder;
    }

    private async Task<ArtistFolderOutcome> MarkAsync(CatalogNodeEntity folder, string? name = null, string? role = null) =>
        await Artists().SetAsync(folder.PublicId, name is null && role is null ? null : new SetArtistFolderRequest { Name = name, Role = role }, "admin");

    private async Task<NodeSeriesLinkEntity?> LinkOfAsync(CatalogNodeEntity node) =>
        await _db.Db.NodeSeriesLinks.AsNoTracking().FirstOrDefaultAsync(l => l.NodeId == node.Id);

    private async Task<List<(string Value, string? Role)>> OwnCreatorsAsync(CatalogNodeEntity folder) =>
        (await _db.Db.DeclaredFacts.AsNoTracking().Where(f => f.NodeId == folder.Id && f.Key == DeclaredFactKeys.Creator)
            .OrderBy(f => f.Position).Select(f => new { f.Value, f.Role }).ToListAsync())
        .Select(f => (f.Value, f.Role)).ToList();

    private async Task<List<long>> PendingAsync() =>
        await _db.Db.MetadataMatchQueue.AsNoTracking().Where(q => q.State == QueueState.Pending).OrderBy(q => q.NodeId).Select(q => q.NodeId).ToListAsync();

    private async Task<List<CatalogNodeEntity>> ArchivesOfAsync(CatalogNodeEntity folder) =>
        await _db.Db.CatalogNodes.AsNoTracking().Where(n => n.ParentId == folder.Id && n.Kind == 1).OrderBy(n => n.SortKey).ToListAsync();

    private void AddQueueRow(CatalogNodeEntity node, int state, MatchBand? outcome, MatchLevel level = MatchLevel.Archive, WorkClass cls = WorkClass.CollectionLeaf) =>
        _db.Db.MetadataMatchQueue.Add(new MetadataMatchQueueEntity
        {
            NodeId = node.Id,
            LibraryId = node.LibraryId,
            State = state,
            Reason = QueueReason.NewFolder,
            Level = (int)level,
            WorkClass = (int)cls,
            Outcome = outcome is { } o ? (int)o : null,
            EnqueuedAt = DateTimeOffset.UtcNow,
        });

    // --- Outcome 1 + 2 + 3: mark, declare, queue ---

    [Fact]
    public async Task Mark_SetsTheState_DeclaresTheFolderNameAsStoryAndArt_QueuesEveryArchive_AndIsAudited()
    {
        await _h.EnableAutomaticAsync();
        var folder = await ArtistFolderAsync();

        var outcome = await MarkAsync(folder);

        Assert.Equal(MetadataLinkResultCode.Ok, outcome.Code);
        var link = await LinkOfAsync(folder);
        Assert.Equal((int)SeriesLinkState.ArtistFolder, link!.State);
        Assert.Null(link.RecordId);
        Assert.Equal([(Artist, "author")], await OwnCreatorsAsync(folder));
        Assert.True(outcome.Result!.CreatorAdded);
        Assert.Equal((Artist, "author"), (outcome.Result.Artist.Name, outcome.Result.Artist.Role));
        // Every archive is a work of its own; the folder is not one.
        Assert.Equal(Works.Length, outcome.Result.Queued);
        Assert.Equal((await ArchivesOfAsync(folder)).Select(a => a.Id).Order().ToList(), await PendingAsync());
        Assert.Equal((int)MetadataMatchRunTrigger.Rerun, (await _db.Db.MetadataMatchRuns.AsNoTracking().SingleAsync()).Trigger);
        Assert.Contains(AuditActions.MetadataArtistFolder, await _db.Db.AuditEvents.Select(a => a.Action).ToListAsync());
        Assert.Equal(0, _h.Handler.CallCount); // nothing is sent by marking
    }

    [Fact]
    public async Task Mark_WithAutomaticMatchingOff_QueuesNothing_MatchLibraryNowFindsTheArchives()
    {
        var folder = await ArtistFolderAsync();
        var outcome = await MarkAsync(folder);

        Assert.Equal(0, outcome.Result!.Queued);
        Assert.Empty(await PendingAsync());
        var (works, _, _) = await Matcher().DetectLibraryAsync(_db.LibraryId, retryUnmatched: false, default);
        Assert.Equal(Works.Length, works.Count);
        Assert.All(works, w => Assert.Equal((MatchLevel.Archive, WorkClass.ArtistCollection), (w.Level, w.Class)));
    }

    [Fact]
    public async Task Mark_TakesAnEditedNameAndRole_AndKeepsTheFoldersOtherDeclaredFacts()
    {
        var folder = await ArtistFolderAsync(name: "Stuff from the con");
        Assert.Equal(MetadataLinkResultCode.Ok, (await Declared().SetFolderAsync(folder.PublicId, new SetDeclaredFactsRequest
        {
            Type = DeclaredType.Manhwa,
            Creators = [new DeclaredCreatorDto { Name = "Alpha Writer", Role = "writer" }],
        }, "admin")).Code);

        var outcome = await MarkAsync(folder, "  Beta   Painter ", "Artist");

        Assert.Equal(MetadataLinkResultCode.Ok, outcome.Code);
        Assert.Equal([(Artist, "artist"), ("Alpha Writer", "writer")], await OwnCreatorsAsync(folder));
        Assert.True(await _db.Db.DeclaredFacts.AnyAsync(f => f.NodeId == folder.Id && f.Key == DeclaredFactKeys.Type && f.Value == "manhwa"));

        // Marking again with the same artist adds nothing.
        var again = await MarkAsync(folder, Artist, "artist");
        Assert.False(again.Result!.CreatorAdded);
        Assert.Equal(2, (await OwnCreatorsAsync(folder)).Count);
    }

    [Theory]
    [InlineData(null, "painter", "creator_role_invalid")]
    [InlineData("bad\u0001name", null, "creator_name_invalid")]
    public async Task Mark_WithAnInvalidArtist_ChangesNothing(string? name, string? role, string error)
    {
        var folder = await ArtistFolderAsync();
        var outcome = await MarkAsync(folder, name, role);
        Assert.Equal((MetadataLinkResultCode.InvalidRequest, error), (outcome.Code, outcome.Error));
        Assert.Null(await LinkOfAsync(folder));
        Assert.Empty(await OwnCreatorsAsync(folder));
    }

    [Fact]
    public async Task Mark_OnAnArchive_IsRefused()
    {
        var folder = await ArtistFolderAsync();
        var archive = (await ArchivesOfAsync(folder))[0];
        var outcome = await MarkAsync(archive);
        Assert.Equal((MetadataLinkResultCode.NotAFolder, "not_a_folder"), (outcome.Code, outcome.Error));
        Assert.Null(await LinkOfAsync(archive));
    }

    [Fact]
    public async Task Mark_ReplacesTheFoldersWaitingReviewRow_ForgetsItsOwnWork_AndKeepsTheRowsBelow()
    {
        await _h.EnableAutomaticAsync();
        var folder = await ArtistFolderAsync();
        await _db.AddLinkAsync(folder, null, SeriesLinkState.NeedsReview);
        _db.Db.MetadataMatchCandidates.Add(new MetadataMatchCandidateEntity
        {
            NodeId = folder.Id,
            Rank = 1,
            Provider = "mangaupdates",
            ExternalId = "8001",
            Title = "Qzv Harbor Tale",
            TitleScore = 0.7,
            AdjustedScore = 0.7,
        });
        AddQueueRow(folder, QueueState.Done, MatchBand.NeedsReview, MatchLevel.ReviewOnly, WorkClass.Ambiguous);
        var archives = await ArchivesOfAsync(folder);
        await _db.AddLinkAsync(archives[1], await _db.AddRecordAsync("8002", "Qzv Lantern Road"), SeriesLinkState.Auto);
        await _db.AddLinkAsync(archives[2], null, SeriesLinkState.DontMatch);
        await _db.Db.SaveChangesAsync();

        var outcome = await MarkAsync(folder);

        Assert.Equal(SeriesLinkState.NeedsReview, outcome.Result!.Change.Previous!.State);
        Assert.Equal((int)SeriesLinkState.ArtistFolder, (await LinkOfAsync(folder))!.State);
        Assert.Empty(await _db.Db.MetadataMatchCandidates.AsNoTracking().Where(c => c.NodeId == folder.Id).ToListAsync());
        Assert.False(await _db.Db.MetadataMatchQueue.AnyAsync(q => q.NodeId == folder.Id)); // the folder is no longer a work
        Assert.Equal((int)SeriesLinkState.Auto, (await LinkOfAsync(archives[1]))!.State);
        Assert.Equal((int)SeriesLinkState.DontMatch, (await LinkOfAsync(archives[2]))!.State);
        Assert.Equal([archives[0].Id], await PendingAsync()); // archives with a row of their own are not works again
    }

    [Fact]
    public async Task Mark_QueuesFinishedUnmatchedWorksAgain_ButLeavesWaitingOnesAlone()
    {
        await _h.EnableAutomaticAsync();
        var folder = await ArtistFolderAsync(works: [.. Works, "Qzv Silver Gate.cbz"]);
        var archives = await ArchivesOfAsync(folder);
        AddQueueRow(archives[0], QueueState.Done, MatchBand.Unmatched);
        AddQueueRow(archives[1], QueueState.Failed, null);
        AddQueueRow(archives[2], QueueState.Pending, null);
        await _db.Db.SaveChangesAsync();

        var outcome = await MarkAsync(folder);

        Assert.Equal(3, outcome.Result!.Queued); // two finished rows again + the archive that had none
        Assert.Equal(archives.Select(a => a.Id).Order().ToList(), await PendingAsync());
        var pendingRun = await _db.Db.MetadataMatchQueue.AsNoTracking().SingleAsync(q => q.NodeId == archives[2].Id);
        Assert.Null(pendingRun.RunId); // the waiting one was not touched
    }

    // --- Remove ---

    [Fact]
    public async Task Clear_RemovesOnlyAnArtistRow_TheDeclaredArtistStays()
    {
        var folder = await ArtistFolderAsync();
        await MarkAsync(folder);
        var dontMatch = await _db.AddFolderAsync(null, "Elsewhere");
        await _db.AddLinkAsync(dontMatch, null, SeriesLinkState.DontMatch);

        Assert.Equal(MetadataLinkResultCode.Ok, (await Artists().ClearAsync(dontMatch.PublicId, "admin")).Code);
        Assert.NotNull(await LinkOfAsync(dontMatch));

        var (code, change) = await Artists().ClearAsync(folder.PublicId, "admin");
        Assert.Equal(MetadataLinkResultCode.Ok, code);
        Assert.Equal(SeriesLinkState.ArtistFolder, change!.Previous!.State);
        Assert.Null(await LinkOfAsync(folder));
        Assert.Equal([(Artist, "author")], await OwnCreatorsAsync(folder));
        Assert.Contains(AuditActions.MetadataArtistFolderClear, await _db.Db.AuditEvents.Select(a => a.Action).ToListAsync());

        // Back to normal detection: three different titles are a plain collection folder again (no artist evidence).
        var (works, _, _) = await Matcher().DetectLibraryAsync(_db.LibraryId, retryUnmatched: false, default);
        Assert.Equal(Works.Length, works.Count);
        Assert.All(works, w => Assert.Equal((MatchLevel.Archive, WorkClass.CollectionLeaf), (w.Level, w.Class)));
    }

    [Fact]
    public async Task Purge_KeepsArtistFolders_LikeDontMatch()
    {
        var folder = await ArtistFolderAsync();
        await MarkAsync(folder);
        _db.Db.ChangeTracker.Clear();
        await _h.Net.Links().PurgeAsync(null, "admin");
        Assert.Equal((int)SeriesLinkState.ArtistFolder, (await LinkOfAsync(folder))!.State);
    }

    // --- Series information: the folder shows "Artist folder", nothing below inherits from it ---

    [Fact]
    public async Task Resolver_ShowsTheArtistFolderOnItself_AndStopsInheritance_EvenBelowALinkedSeries()
    {
        var record = await _db.AddRecordAsync("8100", "Qzv Big Saga");
        var series = await _db.AddFolderAsync(null, "Qzv Big Saga");
        await _db.AddLinkAsync(series, record);
        var folder = await ArtistFolderAsync(series);
        await MarkAsync(folder);
        _db.Db.ChangeTracker.Clear();

        var own = await _db.ResolveAsync(folder);
        Assert.Equal(SeriesInfoState.ArtistFolder, own.State);
        Assert.Equal(Artist, own.Title);
        Assert.Null(own.Web);
        Assert.Equal(SeriesLinkState.ArtistFolder, own.Link!.State);
        Assert.False(own.Link.Inherited);

        var archive = (await ArchivesOfAsync(folder))[0];
        var below = await _db.ResolveAsync(archive);
        Assert.Equal(SeriesInfoState.None, below.State); // not the series above
        Assert.Null(below.Web);
        Assert.True(below.Link!.Inherited);
        Assert.Null(await _db.Resolver().ResolveWebRecordAsync(archive));
        Assert.Null(await _db.Resolver().ResolveShownRecordAsync(folder));
    }

    // --- Matching inside: nearest wins; Don't match / a waiting folder above still block ---

    [Fact]
    public async Task InsideALinkedSeries_TheArtistFolderReopensMatching()
    {
        await _h.EnableAutomaticAsync();
        var series = await _db.AddFolderAsync(null, "Qzv Big Saga");
        await _db.AddLinkAsync(series, await _db.AddRecordAsync("8100", "Qzv Big Saga"));
        var folder = await ArtistFolderAsync(series);

        Assert.Equal(Works.Length, (await MarkAsync(folder)).Result!.Queued);
        var archive = (await ArchivesOfAsync(folder))[0];
        await _db.Db.MetadataMatchQueue.Where(q => q.NodeId == archive.Id).ExecuteDeleteAsync();
        Assert.Equal("ok", (await Matcher().RerunAsync([archive.PublicId], "admin"))[archive.PublicId]);
    }

    [Theory]
    [InlineData(SeriesLinkState.DontMatch)]
    [InlineData(SeriesLinkState.NeedsReview)]
    public async Task BelowADontMatchOrAWaitingFolder_NothingIsMatched(SeriesLinkState above)
    {
        await _h.EnableAutomaticAsync();
        var top = await _db.AddFolderAsync(null, "Shelf");
        await _db.AddLinkAsync(top, null, above);
        var folder = await ArtistFolderAsync(top);

        Assert.Equal(0, (await MarkAsync(folder)).Result!.Queued);
        Assert.Equal((int)SeriesLinkState.ArtistFolder, (await LinkOfAsync(folder))!.State);
        var archive = (await ArchivesOfAsync(folder))[0];
        Assert.Equal("covered_by_folder", (await Matcher().RerunAsync([archive.PublicId], "admin"))[archive.PublicId]);
    }

    [Fact]
    public async Task LinkingTheSeriesAbove_DoesNotRetireTheArtistFoldersWorks()
    {
        var series = await _db.AddFolderAsync(null, "Qzv Big Saga");
        var folder = await ArtistFolderAsync(series);
        await MarkAsync(folder);
        var archive = (await ArchivesOfAsync(folder))[0];
        await _db.AddLinkAsync(archive, null, SeriesLinkState.NeedsReview);
        var loose = await _db.AddArchiveAsync(series, "Qzv Big Saga v01.cbz");
        await _db.AddLinkAsync(loose, null, SeriesLinkState.NeedsReview);
        await _db.AddRecordAsync("8100", "Qzv Big Saga");

        _db.Db.ChangeTracker.Clear();
        await _h.Net.Links().LinkAsync(series.PublicId, new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = "8100" }, "admin");

        Assert.NotNull(await LinkOfAsync(archive)); // inside the artist folder: kept
        Assert.Null(await LinkOfAsync(loose));      // directly below the series: retired as before
    }

    // --- The declared artist settles the match of a work inside (never sent) ---

    [Fact]
    public async Task AWorkInside_IsMatchedOnItsOwn_AndTheDeclaredArtistPicksTheirRecord()
    {
        await _h.EnableAutomaticAsync();
        _h.Search["Qzv Harbor Tale"] = [new MuJson.Hit(801, "Qzv Harbor Tale (ALPHA Writer)"), new MuJson.Hit(802, "Qzv Harbor Tale (BETA Painter)")];
        _h.Records[801] = MuJson.Get(801, "Qzv Harbor Tale (ALPHA Writer)", authors: ["Alpha Writer"]);
        _h.Records[802] = MuJson.Get(802, "Qzv Harbor Tale (BETA Painter)", authors: [Artist]);
        // The folder's name is not the artist's: the admin typed the name. It is a hint (the folder name is no author tag either).
        var folder = await ArtistFolderAsync(name: "Stuff from the con");
        var harbor = (await ArchivesOfAsync(folder)).Single(a => a.DisplayName.StartsWith("Qzv Harbor", StringComparison.Ordinal));
        await MarkAsync(folder, Artist);
        await _db.Db.MetadataMatchQueue.Where(q => q.NodeId != harbor.Id).ExecuteDeleteAsync();

        _db.Db.ChangeTracker.Clear();
        var service = Matcher();
        var row = await service.LeaseNextAsync("test");
        Assert.Equal(harbor.Id, row!.NodeId);
        await service.ProcessAsync(row);

        _db.Db.ChangeTracker.Clear();
        var link = await _db.Db.NodeSeriesLinks.AsNoTracking().Include(l => l.Record).SingleAsync(l => l.NodeId == harbor.Id);
        Assert.Contains(link.State, new[] { (int)SeriesLinkState.Auto, (int)SeriesLinkState.NeedsReview });
        var top = link.Record?.ExternalId
            ?? (await _db.Db.MetadataMatchCandidates.AsNoTracking().SingleAsync(c => c.NodeId == harbor.Id && c.Rank == 1)).ExternalId;
        Assert.Equal("802", top);
        if (link.State == (int)SeriesLinkState.NeedsReview)
        {
            var other = await _db.Db.MetadataMatchCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.NodeId == harbor.Id && c.ExternalId == "801");
            if (other is not null)
                Assert.Equal(0, other.Reasons & (int)MatchReason.AuthorConflict); // a hint, never a veto: the other record is not ruled out
        }
        Assert.Equal((int)SeriesLinkState.ArtistFolder, (await LinkOfAsync(folder))!.State); // the folder never becomes a work
        Assert.DoesNotContain(_h.Handler.Seen, r => r.Body?.Contains("Painter", StringComparison.OrdinalIgnoreCase) == true); // never sent
    }

    // --- Review: the Collections tab, accept from Needs review / Unmatched, bulk ---

    [Fact]
    public async Task Review_AcceptArtist_FromNeedsReview_ClearsTheRow_AndListsTheFolderInCollections()
    {
        await _h.EnableAutomaticAsync();
        var folder = await ArtistFolderAsync();
        await _db.AddLinkAsync(folder, null, SeriesLinkState.NeedsReview);

        var outcome = await Review().AcceptArtistAsync(folder.PublicId, null, "admin");

        Assert.Equal(MetadataLinkResultCode.Ok, outcome.Code);
        Assert.Equal(Works.Length, outcome.Result!.Queued);
        var summary = await Review().SummaryAsync(null);
        Assert.Equal((0, 0, 1), (summary!.NeedsReview, summary.Collections, summary.ArtistFolders));
        var (_, tab) = await Review().ListAsync(MetadataReviewTab.Collections, null, null, 50);
        var item = tab!.Items.Single();
        Assert.Equal((folder.PublicId, SeriesLinkState.ArtistFolder), (item.NodeId, item.Link!.State));
        Assert.Equal((Artist, "author"), (item.Artist!.Name, item.Artist.Role));
        Assert.Equal("review", (await _db.Db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == AuditActions.MetadataArtistFolder)).Result);
    }

    [Fact]
    public async Task Review_AcceptArtist_FromUnmatched_ClearsTheRow()
    {
        var folder = await ArtistFolderAsync();
        AddQueueRow(folder, QueueState.Done, MatchBand.Unmatched, MatchLevel.ReviewOnly, WorkClass.Ambiguous);
        await _db.Db.SaveChangesAsync();
        Assert.Equal(1, (await Review().SummaryAsync(null))!.Unmatched);

        var outcome = await Review().AcceptArtistAsync(folder.PublicId, new SetArtistFolderRequest { Name = "Gamma Inker" }, "admin");

        Assert.Equal(MetadataLinkResultCode.Ok, outcome.Code);
        Assert.Equal(0, (await Review().SummaryAsync(null))!.Unmatched);
        Assert.Equal([("Gamma Inker", "author")], await OwnCreatorsAsync(folder));
    }

    [Fact]
    public async Task Review_BulkMarkArtistFolder_MarksFoldersWithTheirOwnNames_AndRefusesArchives()
    {
        var one = await ArtistFolderAsync();
        var two = await ArtistFolderAsync(name: "Gamma Inker");
        await _db.AddLinkAsync(one, null, SeriesLinkState.NeedsReview);
        var archive = (await ArchivesOfAsync(two))[0];
        await _db.AddLinkAsync(archive, null, SeriesLinkState.NeedsReview);

        var (error, result) = await Review().BulkAsync(new MetadataReviewBulkRequest
        {
            Action = MetadataReviewBulkAction.MarkArtistFolder,
            NodeIds = [one.PublicId, two.PublicId, archive.PublicId, "nope"],
        }, "admin");

        Assert.Null(error);
        Assert.Equal("ok", result!.Results.Single(r => r.NodeId == one.PublicId).Code);
        Assert.Equal("ok", result.Results.Single(r => r.NodeId == two.PublicId).Code);
        Assert.Equal("not_a_folder", result.Results.Single(r => r.NodeId == archive.PublicId).Code);
        Assert.Equal("not_found", result.Results.Single(r => r.NodeId == "nope").Code);
        Assert.Equal([(Artist, "author")], await OwnCreatorsAsync(one));
        Assert.Equal([("Gamma Inker", "author")], await OwnCreatorsAsync(two));
        Assert.Equal((int)SeriesLinkState.NeedsReview, (await LinkOfAsync(archive))!.State);
    }
}
