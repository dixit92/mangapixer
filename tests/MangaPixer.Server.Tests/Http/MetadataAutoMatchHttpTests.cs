namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Flags;
using com.lifepixer.mangapixer.Server.Features.Metadata.Review;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Tests.Server.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of metadata stage 2 (auto-match): wiring (DI,
/// the hosted worker), admin-only endpoints (403 for members), the user flag
/// endpoints (404 for a node the user cannot access), settings consent gating, the
/// review dashboard round trip, runs + "Match this library now", folder Content,
/// missing folders, "automatic matching off makes no outbound call through a full
/// scan", and a log sentinel over a real worker pass. Every outbound request ends
/// in the scripted handler; the fake matcher core stands in for lane A's.
/// </summary>
[Trait("Category", "Http")]
public sealed class MetadataAutoMatchHttpTests
{
    private const string LibPub = "amlib1";
    private const string BerserkId = "51239621230";

    private static void Fakes(IServiceCollection services)
    {
        services.AddSingleton<IWorkDetector, FakeWorkDetector>();
        services.AddSingleton<IMatchQueryPlanner, FakeQueryPlanner>();
        services.AddSingleton<IMatchScorer, FakeMatchScorer>();
        services.AddSingleton(new MetadataRateLimitOptions { AutomaticInterval = TimeSpan.Zero });
    }

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, CatalogNodeKind kind, string name, int availability = 0) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name,
        Availability = availability,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// amLinked (folder, Confirmed link to the Berserk record) + amArc; amPlain (folder, no link);
    /// amReview (folder in review: 2 stored candidates); amAuto (folder, Auto link); amGone (removed folder with Don't match).
    /// </summary>
    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPub))
            return;
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Auto Lib", RootPath = "/synthetic/am", CreatedAt = DateTimeOffset.UtcNow, MetadataEnabled = true };
        var other = new LibraryEntity { PublicId = "amlib2", DisplayName = "Other Lib", RootPath = "/synthetic/am2", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.AddRange(lib, other);
        await db.SaveChangesAsync();
        var linked = Node("amLinked", lib.Id, null, CatalogNodeKind.Folder, "Linked Saga");
        var plain = Node("amPlain", lib.Id, null, CatalogNodeKind.Folder, "Plain Folder");
        var review = Node("amReview", lib.Id, null, CatalogNodeKind.Folder, "Review Saga");
        var auto = Node("amAuto", lib.Id, null, CatalogNodeKind.Folder, "Auto Saga");
        var gone = Node("amGone", lib.Id, null, CatalogNodeKind.Folder, "Gone Saga", availability: 5);
        var otherFolder = Node("amOther", other.Id, null, CatalogNodeKind.Folder, "Other Saga");
        db.CatalogNodes.AddRange(linked, plain, review, auto, gone, otherFolder);
        await db.SaveChangesAsync();
        var arc = Node("amArc", lib.Id, linked.Id, CatalogNodeKind.Archive, "Linked Saga v01");
        db.CatalogNodes.Add(arc);
        await db.SaveChangesAsync();
        db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = arc.Id, ContentVersion = 1, PageCount = 2 });

        var record = new MetadataRecordEntity
        {
            PublicId = "ramberserk",
            Provider = "mangaupdates",
            ExternalId = BerserkId,
            Title = "Berserk",
            FetchedAt = DateTimeOffset.UtcNow,
            SiteUrl = "https://www.mangaupdates.com/series/njeqwry/berserk",
        };
        var autoRecord = new MetadataRecordEntity { PublicId = "ramauto", Provider = "mangaupdates", ExternalId = "777", Title = "Auto Saga", FetchedAt = DateTimeOffset.UtcNow };
        db.MetadataRecords.AddRange(record, autoRecord);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        db.NodeSeriesLinks.AddRange(
            new NodeSeriesLinkEntity { NodeId = linked.Id, LibraryId = lib.Id, State = (int)SeriesLinkState.Confirmed, RecordId = record.Id, CreatedAt = now, UpdatedAt = now },
            new NodeSeriesLinkEntity { NodeId = review.Id, LibraryId = lib.Id, State = (int)SeriesLinkState.NeedsReview, MatchMethod = 3, CreatedAt = now, UpdatedAt = now },
            new NodeSeriesLinkEntity { NodeId = auto.Id, LibraryId = lib.Id, State = (int)SeriesLinkState.Auto, RecordId = autoRecord.Id, MatchMethod = 3, CreatedAt = now, UpdatedAt = now },
            new NodeSeriesLinkEntity { NodeId = gone.Id, LibraryId = lib.Id, State = (int)SeriesLinkState.DontMatch, CreatedAt = now, UpdatedAt = now });
        var run = new MetadataMatchRunEntity { PublicId = "mmseed", LibraryId = lib.Id, Trigger = 1, Status = 1, StartedAt = now, Candidates = 2, Processed = 2 };
        db.MetadataMatchRuns.Add(run);
        await db.SaveChangesAsync();
        db.MetadataMatchQueue.AddRange(
            new MetadataMatchQueueEntity { NodeId = review.Id, LibraryId = lib.Id, State = 2, Level = 1, WorkClass = 1, Outcome = 1, OutcomeReasons = 1, RunId = run.Id, EnqueuedAt = now, CompletedAt = now },
            new MetadataMatchQueueEntity { NodeId = auto.Id, LibraryId = lib.Id, State = 2, Level = 1, WorkClass = 1, Outcome = 2, RunId = run.Id, EnqueuedAt = now, CompletedAt = now });
        db.MetadataMatchCandidates.AddRange(
            new MetadataMatchCandidateEntity
            {
                NodeId = review.Id,
                Rank = 1,
                Provider = "mangaupdates",
                ExternalId = BerserkId,
                Title = "Berserk",
                TitleScore = 0.9,
                AdjustedScore = 0.9,
                Reasons = 1,
                ImageRemoteUrl = "https://cdn.mangaupdates.com/image/i1.png",
                CreatedAt = now,
            },
            new MetadataMatchCandidateEntity { NodeId = review.Id, Rank = 2, Provider = "mangaupdates", ExternalId = "888", Title = "Berserk!", TitleScore = 0.88, AdjustedScore = 0.88, CreatedAt = now });
        await db.SaveChangesAsync();
    }

    private static async Task<ApiError> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!;

    private static async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static async Task EnableAsync(HttpClient admin, bool automatic)
    {
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = automatic ? true : null,
            AcceptedAutoConsentVersion = automatic ? MetadataAutoConsent.CurrentVersion : null,
        })).EnsureSuccessStatusCode();
    }

    // --- Wiring ---

    [Fact]
    public async Task Wiring_Stage2ServicesResolve_AndTheWorkerIsAHostedService()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.NotNull(sp.GetRequiredService<MetadataAutoMatchService>());
        Assert.NotNull(sp.GetRequiredService<MetadataReviewService>());
        Assert.NotNull(sp.GetRequiredService<MetadataFlagService>());
        Assert.NotNull(sp.GetRequiredService<MetadataFolderContentService>());
        Assert.NotNull(sp.GetRequiredService<MetadataCarryOverService>());
        Assert.NotNull(sp.GetRequiredService<MetadataPostScanHook>());
        Assert.NotNull(sp.GetRequiredService<MetadataRefreshService>());
        Assert.Single(factory.Services.GetServices<IHostedService>().OfType<MetadataAutoMatchHostedService>());
        // The matcher core is registered; with web lookups off the worker still waits and sends nothing.
        Assert.True(sp.GetRequiredService<MetadataAutoMatchService>().MatcherAvailable);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var runs = await OkAsync<MetadataMatchRunsDto>(await admin.GetAsync("/api/v1/admin/metadata/runs"));
        Assert.Equal("metadata_disabled", runs.Status.WaitingCode);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    // --- Authorization ---

    [Fact]
    public async Task AdminEndpoints_Are403ForMembers()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true, configureServices: Fakes);
        await SeedAsync(factory);
        var reader = await factory.CreateReaderClientAsync("amreader", LibPub);
        var calls = new List<Func<Task<HttpResponseMessage>>>
        {
            () => reader.GetAsync("/api/v1/admin/metadata/review"),
            () => reader.GetAsync("/api/v1/admin/metadata/review/summary"),
            () => reader.PostAsJsonAsync("/api/v1/admin/metadata/review/amReview/accept", new MetadataReviewAcceptRequest { Rank = 1 }),
            () => reader.PostAsJsonAsync("/api/v1/admin/metadata/review/bulk", new MetadataReviewBulkRequest { Action = MetadataReviewBulkAction.Confirm, NodeIds = ["amAuto"] }),
            () => reader.GetAsync("/api/v1/admin/metadata/runs"),
            () => reader.PostAsync("/api/v1/admin/metadata/runs/mmseed/cancel", null),
            () => reader.GetAsync($"/api/v1/admin/metadata/libraries/{LibPub}/match/estimate"),
            () => reader.PostAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}/match", new MetadataMatchLibraryRequest()),
            () => reader.GetAsync("/api/v1/admin/metadata/flags"),
            () => reader.PostAsJsonAsync("/api/v1/admin/metadata/flags/flx/resolve", new ResolveMetadataFlagRequest { Outcome = MetadataFlagState.Dismissed }),
            () => reader.PostAsJsonAsync("/api/v1/admin/metadata/missing/amGone/reattach", new MetadataReattachRequest { TargetNodeId = "amPlain" }),
            () => reader.DeleteAsync("/api/v1/admin/metadata/missing/amGone"),
            () => reader.GetAsync("/api/v1/admin/metadata/folders/amPlain/content"),
            () => reader.PutAsJsonAsync("/api/v1/admin/metadata/folders/amPlain/content", new SetFolderMetadataContentRequest { Content = MetadataFolderContent.NotDoujinshi }),
            () => reader.PostAsync("/api/v1/admin/metadata/folders/amPlain/content/rematch", null),
        };
        foreach (var call in calls)
            Assert.Equal(HttpStatusCode.Forbidden, (await call()).StatusCode);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    // --- Flags ---

    [Fact]
    public async Task Flags_MemberFlagsTheAnchor_OnceUntilResolved_NonMemberGets404_AdminResolves()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true, configureServices: Fakes);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var reader = await factory.CreateReaderClientAsync("amflagger", LibPub);
        var outsider = await factory.CreateReaderClientAsync("amoutsider", "amlib2");

        // From the archive: the flag lands on the folder holding the link (the anchor).
        var created = await reader.PostAsJsonAsync("/api/v1/nodes/amArc/series-info/flags",
            new CreateMetadataFlagRequest { Reason = MetadataFlagReason.WrongSeries, Note = "Not this one." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var flag = (await created.Content.ReadFromJsonAsync<MetadataMyFlagDto>(TestJson.Web))!;
        Assert.Equal("amLinked", flag.AnchorNodeId);
        Assert.Equal(MetadataFlagState.Open, flag.State);

        var again = await reader.PostAsJsonAsync("/api/v1/nodes/amLinked/series-info/flags", new CreateMetadataFlagRequest { Reason = MetadataFlagReason.Other });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("flag_exists", (await ErrorAsync(again)).Error);
        var mine = await OkAsync<MetadataMyFlagStateDto>(await reader.GetAsync("/api/v1/nodes/amLinked/series-info/flags/mine"));
        Assert.False(mine.CanFlag);
        Assert.Equal(flag.FlagId, mine.Flag!.FlagId);

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync("/api/v1/nodes/amLinked/series-info/flags",
            new CreateMetadataFlagRequest { Reason = MetadataFlagReason.WrongSeries })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync("/api/v1/nodes/amLinked/series-info/flags/mine")).StatusCode);

        var noWeb = await reader.PostAsJsonAsync("/api/v1/nodes/amPlain/series-info/flags", new CreateMetadataFlagRequest { Reason = MetadataFlagReason.Other });
        Assert.Equal("no_web_data", (await ErrorAsync(noWeb)).Error);
        var tooLong = await reader.PostAsJsonAsync("/api/v1/nodes/amAuto/series-info/flags",
            new CreateMetadataFlagRequest { Reason = MetadataFlagReason.Other, Note = new string('x', 501) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var list = await OkAsync<MetadataFlagPageDto>(await admin.GetAsync("/api/v1/admin/metadata/flags"));
        var listed = Assert.Single(list.Items);
        Assert.Equal("Not this one.", listed.Note);
        Assert.Equal("amflagger", listed.ReporterDisplayName);
        Assert.Equal(BerserkId, listed.ExternalId);
        var summary = await OkAsync<MetadataReviewSummaryDto>(await admin.GetAsync("/api/v1/admin/metadata/review/summary"));
        Assert.Equal(1, summary.OpenFlags);
        var flagsTab = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=Flags"));
        Assert.Equal("Not this one.", Assert.Single(Assert.Single(flagsTab.Items).Flags).Note);

        var resolved = await OkAsync<MetadataFlagDto>(await admin.PostAsJsonAsync($"/api/v1/admin/metadata/flags/{flag.FlagId}/resolve",
            new ResolveMetadataFlagRequest { Outcome = MetadataFlagState.Unlinked }));
        Assert.Equal(MetadataFlagState.Unlinked, resolved.State);
        Assert.Null(resolved.CurrentLink);
        Assert.Equal("admin", resolved.ResolvedByDisplayName);
        var after = await OkAsync<MetadataMyFlagStateDto>(await reader.GetAsync("/api/v1/nodes/amLinked/series-info/flags/mine"));
        Assert.Equal(MetadataFlagState.Unlinked, after.Flag!.State);
        Assert.False(after.CanFlag); // no web data any more
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/admin/metadata/flags/{flag.FlagId}/resolve",
            new ResolveMetadataFlagRequest { Outcome = MetadataFlagState.Open })).StatusCode);
    }

    [Fact]
    public async Task Flags_DailyCapIsMax20Or2PercentOfVisibleAnchors()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true, configureServices: Fakes);
        await SeedAsync(factory);
        var reader = await factory.CreateReaderClientAsync("amcapped", LibPub);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var userId = await db.Users.Where(u => u.UserName == "amcapped").Select(u => u.Id).SingleAsync();
            var anchor = await db.CatalogNodes.Where(n => n.PublicId == "amAuto").Select(n => new { n.Id, n.LibraryId }).SingleAsync();
            Assert.Equal(20, await scope.ServiceProvider.GetRequiredService<MetadataFlagService>().DailyCapAsync(userId)); // 2 anchors -> the floor
            for (var i = 0; i < 20; i++)
            {
                db.MetadataFlags.Add(new MetadataFlagEntity
                {
                    PublicId = $"flcap{i}",
                    NodeId = anchor.Id,
                    LibraryId = anchor.LibraryId,
                    ReporterUserId = userId,
                    Reason = 3,
                    State = (int)MetadataFlagState.Dismissed,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }
        var limited = await reader.PostAsJsonAsync("/api/v1/nodes/amLinked/series-info/flags", new CreateMetadataFlagRequest { Reason = MetadataFlagReason.WrongDetails });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("flag_limit", (await ErrorAsync(limited)).Error);
    }

    // --- Settings ---

    [Fact]
    public async Task Settings_AutomaticSwitchNeedsItsConsent_ThresholdsAreValidated()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var noConsent = await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { AutoMatchEnabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, noConsent.StatusCode);
        Assert.Equal("auto_consent_required", (await ErrorAsync(noConsent)).Error);
        var noFetch = await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings",
            new UpdateMetadataSettingsRequest { AutoMatchEnabled = true, AcceptedAutoConsentVersion = MetadataAutoConsent.CurrentVersion });
        Assert.Equal("fetch_required", (await ErrorAsync(noFetch)).Error);
        var bad = await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings",
            new UpdateMetadataSettingsRequest { Thresholds = new MetadataMatchThresholdsDto { AutoTitle = 0.99, Margin = 0.01, ReviewFloor = 0.5 } });
        Assert.Equal("invalid_thresholds", (await ErrorAsync(bad)).Error);

        await EnableAsync(admin, automatic: true);
        var settings = await OkAsync<MetadataSettingsDto>(await admin.GetAsync("/api/v1/admin/metadata/settings"));
        Assert.True(settings.AutoMatchEnabled);
        Assert.Equal(MetadataAutoConsent.CurrentVersion, settings.AcceptedAutoConsentVersion);
        Assert.Equal(0, factory.Handler.CallCount); // turning it on sends nothing by itself
    }

    // --- Review dashboard ---

    [Fact]
    public async Task Review_ListsStoredCandidates_AcceptLinksTheChosenOne_BulkConfirmsAutoLinks()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: Fakes);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var summary = await OkAsync<MetadataReviewSummaryDto>(await admin.GetAsync($"/api/v1/admin/metadata/review/summary?library={LibPub}"));
        Assert.Equal((1, 1, 1, 0, 1), (summary.NeedsReview, summary.AutoLinked, summary.Confirmed, summary.DontMatch, summary.MissingFolders)); // the Don't match row sits on a removed folder

        var page = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=NeedsReview"));
        var item = Assert.Single(page.Items);
        Assert.Equal("amReview", item.NodeId);
        Assert.Null(item.CoverUrl); // a folder without archives has no cover
        Assert.Equal(WorkClass.Series, item.WorkClass);
        Assert.Equal(["close_second"], item.Reasons);
        Assert.Equal([1, 2], item.Candidates.Select(c => c.Rank).ToArray());
        Assert.NotNull(item.Candidates[0].ImageToken); // a token only; nothing fetched on list
        Assert.Null(item.Candidates[1].ImageToken);
        Assert.Equal("mmseed", item.RunId);
        Assert.Equal(0, factory.Handler.CallCount);

        await EnableAsync(admin, automatic: false);
        var accepted = await OkAsync<NodeSeriesLinkChangeDto>(await admin.PostAsJsonAsync("/api/v1/admin/metadata/review/amReview/accept",
            new MetadataReviewAcceptRequest { Rank = 1 }));
        Assert.Equal(SeriesLinkState.Confirmed, accepted.Link!.State);
        Assert.Equal(BerserkId, accepted.Link.ExternalId);
        Assert.Equal(SeriesLinkState.NeedsReview, accepted.Previous!.State);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/admin/metadata/review/amPlain/accept",
            new MetadataReviewAcceptRequest { Rank = 1 })).StatusCode); // no stored candidate

        var bulk = await OkAsync<MetadataReviewBulkResultDto>(await admin.PostAsJsonAsync("/api/v1/admin/metadata/review/bulk",
            new MetadataReviewBulkRequest { Action = MetadataReviewBulkAction.Confirm, NodeIds = ["amAuto", "nope"] }));
        Assert.Equal(1, bulk.Succeeded);
        Assert.Equal("not_found", bulk.Results.Single(r => r.NodeId == "nope").Code);
        var allConfirmed = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=Confirmed&limit=10"));
        Assert.Equal("/api/v1/items/amArc/cover?v=1", allConfirmed.Items.Single(i => i.NodeId == "amLinked").CoverUrl); // its first archive, as browse
        var confirmed = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=Confirmed&limit=1"));
        Assert.Equal(3, confirmed.Total);
        Assert.True(confirmed.HasMore);
        var next = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync($"/api/v1/admin/metadata/review?tab=Confirmed&limit=1&cursor={confirmed.NextCursor}"));
        Assert.NotEqual(confirmed.Items[0].NodeId, next.Items[0].NodeId);

        var runs = await OkAsync<MetadataMatchRunsDto>(await admin.GetAsync("/api/v1/admin/metadata/runs"));
        Assert.Equal(1, runs.Items.Single(r => r.RunId == "mmseed").ReviewAcceptedTop); // local-only counter
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/metadata/review?tab=99")).StatusCode);
    }

    [Fact]
    public async Task Review_RowsCarryTheirContainingFolder_NullAtTheLibraryTopLevel()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: Fakes);
        await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            // A nested work in review: a subfolder of amPlain.
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var lib = await db.Libraries.SingleAsync(l => l.PublicId == LibPub);
            var plain = await db.CatalogNodes.SingleAsync(n => n.PublicId == "amPlain");
            var nested = Node("amNested", lib.Id, plain.Id, CatalogNodeKind.Folder, "Nested Saga");
            db.CatalogNodes.Add(nested);
            await db.SaveChangesAsync();
            var now = DateTimeOffset.UtcNow;
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = nested.Id,
                LibraryId = lib.Id,
                State = (int)SeriesLinkState.NeedsReview,
                MatchMethod = 3,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var page = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=NeedsReview"));
        Assert.Null(page.Items.Single(i => i.NodeId == "amReview").ParentNodeId);
        Assert.Equal("amPlain", page.Items.Single(i => i.NodeId == "amNested").ParentNodeId);
    }

    [Fact]
    public async Task MissingFolders_ListedAndReattached_FolderContentRoundTrip()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true, configureServices: Fakes);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var missing = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=MissingFolders"));
        var row = Assert.Single(missing.Items);
        Assert.True(row.Missing);
        Assert.Equal(SeriesLinkState.DontMatch, row.Link!.State);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/admin/metadata/missing/amGone/reattach",
            new MetadataReattachRequest { TargetNodeId = "amArc" })).StatusCode); // not a folder
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/admin/metadata/missing/amGone/reattach",
            new MetadataReattachRequest { TargetNodeId = "amOther" })).StatusCode); // another library
        var reattached = await OkAsync<MetadataReattachResultDto>(await admin.PostAsJsonAsync("/api/v1/admin/metadata/missing/amGone/reattach",
            new MetadataReattachRequest { TargetNodeId = "amPlain" }));
        Assert.True(reattached.Link);
        Assert.Equal(0, (await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=MissingFolders"))).Total);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/v1/admin/metadata/missing/amPlain")).StatusCode); // live folder

        var set = await OkAsync<FolderMetadataContentDto>(await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/amLinked/content",
            new SetFolderMetadataContentRequest { Content = MetadataFolderContent.DoujinshiAndAdultOneShots }));
        Assert.Equal(MetadataFolderContent.DoujinshiAndAdultOneShots, set.Content);
        Assert.Equal(new MetadataContentRematchDto { Affected = 0, Queued = 0 }, set.Rematch); // the doujinshi rule changed; nothing below to redo
        Assert.Equal(new MetadataContentRematchDto { Affected = 0, Queued = 0 },
            await OkAsync<MetadataContentRematchDto>(await admin.PostAsync("/api/v1/admin/metadata/folders/amLinked/content/rematch", null)));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/admin/metadata/folders/nope/content/rematch", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/amArc/content",
            new SetFolderMetadataContentRequest { Content = MetadataFolderContent.NotDoujinshi })).StatusCode);
        var cleared = await OkAsync<FolderMetadataContentDto>(await admin.DeleteAsync("/api/v1/admin/metadata/folders/amLinked/content"));
        Assert.Null(cleared.Content);
        Assert.Equal(MetadataFolderContent.Auto, cleared.Effective);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    // --- Runs, "Match this library now", and a real worker pass ---

    [Fact]
    public async Task MatchLibraryNow_EstimateQueueRunAndCancel()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true, configureServices: Fakes);
        await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var libId = await db.Libraries.Where(l => l.PublicId == LibPub).Select(l => l.Id).SingleAsync();
            var plain = await db.CatalogNodes.SingleAsync(n => n.PublicId == "amPlain");
            db.CatalogNodes.AddRange(Node("amP1", libId, plain.Id, CatalogNodeKind.Archive, "Plain v01"), Node("amP2", libId, plain.Id, CatalogNodeKind.Archive, "Plain v02"));
            await db.SaveChangesAsync();
        }
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin, automatic: true);

        var estimate = await OkAsync<MetadataMatchEstimateDto>(await admin.GetAsync($"/api/v1/admin/metadata/libraries/{LibPub}/match/estimate"));
        Assert.Equal(1, estimate.Candidates); // amPlain; the others are linked / in review
        Assert.True(estimate.AutomaticAvailable);
        Assert.False(estimate.FirstRun); // the seeded "mmseed" run was a bulk run of this library

        var started = await admin.PostAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}/match", new MetadataMatchLibraryRequest { ReviewFirst = true });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<MetadataMatchRunDto>(TestJson.Web))!;
        Assert.Equal(MetadataMatchRunTrigger.Bulk, run.Trigger);
        Assert.True(run.ReviewFirst);

        var runs = await OkAsync<MetadataMatchRunsDto>(await admin.GetAsync($"/api/v1/admin/metadata/runs?library={LibPub}"));
        Assert.Equal(1, runs.Status.Pending);
        Assert.True(runs.Status.Active);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}/match",
            new MetadataMatchLibraryRequest())).StatusCode); // everything is queued already

        var cancelled = await OkAsync<MetadataMatchRunDto>(await admin.PostAsync($"/api/v1/admin/metadata/runs/{run.RunId}/cancel", null));
        Assert.Equal(MetadataMatchRunStatus.Cancelled, cancelled.Status);
        Assert.Equal(0, factory.Handler.CallCount); // queueing and cancelling never contact the provider
    }

    [Fact]
    public async Task WorkerPass_AutoLinksOverHttpWiring_AndNoFolderNameReachesAnyLogLine()
    {
        const string sentinel = "Qzvxsentinel";
        var sink = new CollectingSink();
        using var factory = new MetadataNetworkWebApplicationFactory(sink: sink, configureServices: Fakes);
        await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var libId = await db.Libraries.Where(l => l.PublicId == LibPub).Select(l => l.Id).SingleAsync();
            var folder = Node("amSentinel", libId, null, CatalogNodeKind.Folder, $"{sentinel} Saga");
            db.CatalogNodes.Add(folder);
            await db.SaveChangesAsync();
            db.CatalogNodes.AddRange(Node("amS1", libId, folder.Id, CatalogNodeKind.Archive, $"{sentinel} Saga v01"),
                Node("amS2", libId, folder.Id, CatalogNodeKind.Archive, $"{sentinel} Saga v02"));
            await db.SaveChangesAsync();
        }
        factory.Handler.Respond = request => request.Method == HttpMethod.Post
            ? ScriptedHandler.Json(MuJson.Search(new MuJson.Hit(9901, $"{sentinel} Saga")))
            : ScriptedHandler.Json(MuJson.Get(9901, $"{sentinel} Saga"));
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin, automatic: true);
        (await admin.PostAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}/match", new MetadataMatchLibraryRequest())).EnsureSuccessStatusCode();

        var worker = factory.Services.GetServices<IHostedService>().OfType<MetadataAutoMatchHostedService>().Single();
        Assert.Equal(1, await worker.RunPassAsync(CancellationToken.None));

        var info = await OkAsync<SeriesInfoDto>(await admin.GetAsync("/api/v1/nodes/amSentinel/series-info"));
        Assert.Equal(SeriesLinkState.Auto, info.Link!.State);
        var autoTab = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=AutoLinked"));
        Assert.Contains(autoTab.Items, i => i.NodeId == "amSentinel" && i.Link!.MatchMethod == MetadataMatchMethod.Auto);
        Assert.Equal(2, factory.Handler.CallCount);

        Assert.Contains(sink.Events, e => e.MessageTemplate.Text.Contains("Automatic matching decided", StringComparison.Ordinal));
        foreach (var e in sink.Events)
        {
            var rendered = e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Value.ToString())) + " " + e.Exception;
            Assert.False(rendered.Contains(sentinel, StringComparison.OrdinalIgnoreCase), "A name leaked into a log line: " + e.MessageTemplate.Text);
        }
    }

    // --- The privacy guard: automatic matching off makes no outbound call through a full scan ---

    [Fact]
    public async Task FullScan_AutomaticOff_MakesNoOutboundCall_On_QueuesWithoutCalling()
    {
        var root = Path.Combine(Path.GetTempPath(), "mangapixer-amscan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Scan Saga"));
        await File.WriteAllTextAsync(Path.Combine(root, "Scan Saga", "s01.cbz"), "x");
        await File.WriteAllTextAsync(Path.Combine(root, "Scan Saga", "s02.cbz"), "y");
        try
        {
            using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true, configureServices: Fakes);
            var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
            var library = await OkAsync<LibraryDto>(await admin.PostAsJsonAsync("/api/v1/admin/libraries",
                new RegisterLibraryRequest { DisplayName = "Scan Lib", RootPath = root }));
            await EnableAsync(admin, automatic: false);
            (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{library.Id}", new UpdateMetadataLibraryRequest { FetchEnabled = true })).EnsureSuccessStatusCode();

            await ScanAndWaitAsync(factory, admin, library.Id);
            await Task.Delay(300);
            using (var scope = factory.Services.CreateScope())
                Assert.False(await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().MetadataMatchQueue.AnyAsync());
            Assert.Equal(0, factory.Handler.CallCount);

            // On: the post-scan hook queues the new folder - and still sends nothing itself.
            await EnableAsync(admin, automatic: true);
            Directory.CreateDirectory(Path.Combine(root, "Second Saga"));
            await File.WriteAllTextAsync(Path.Combine(root, "Second Saga", "t01.cbz"), "z");
            await File.WriteAllTextAsync(Path.Combine(root, "Second Saga", "t02.cbz"), "w");
            await ScanAndWaitAsync(factory, admin, library.Id);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var queued = 0;
            while (DateTime.UtcNow < deadline && queued == 0)
            {
                using var scope = factory.Services.CreateScope();
                queued = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().MetadataMatchQueue.CountAsync();
                if (queued == 0)
                    await Task.Delay(200);
            }
            Assert.Equal(1, queued);
            Assert.Equal(0, factory.Handler.CallCount);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // --- Re-check of works in review under the current rules (1.31.0) ---

    private static void RespondWithTheReviewSaga(MetadataNetworkWebApplicationFactory factory) =>
        factory.Handler.Respond = request => request.Method == HttpMethod.Post
            ? ScriptedHandler.Json(MuJson.Search(new MuJson.Hit(9902, "Review Saga")))
            : ScriptedHandler.Json(MuJson.Get(9902, "Review Saga"));

    [Fact]
    public async Task WorkerPass_ChecksAnOlderReviewRowAgain_StampsTheRevision_AndDoesNotRepeatIt()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: Fakes);
        await SeedAsync(factory); // the seeded Needs review row was scored before the revision stamp existed
        RespondWithTheReviewSaga(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin, automatic: true);
        var worker = factory.Services.GetServices<IHostedService>().OfType<MetadataAutoMatchHostedService>().Single();

        Assert.Equal(1, await worker.RunPassAsync(CancellationToken.None)); // queues the stale row by itself, then scores it
        Assert.True(factory.Handler.CallCount > 0);
        var calls = factory.Handler.CallCount;
        Assert.Equal(0, await worker.RunPassAsync(CancellationToken.None)); // once per revision

        Assert.Equal(calls, factory.Handler.CallCount);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var row = await db.MetadataMatchQueue.AsNoTracking().SingleAsync(q => q.Reason == QueueReason.Recheck);
        Assert.Equal((QueueState.Done, MatcherRules.Revision), (row.State, row.RulesRevision));
        Assert.Equal((int)MetadataMatchRunTrigger.Recheck, (await db.MetadataMatchRuns.AsNoTracking().SingleAsync(r => r.Id == row.RunId)).Trigger);
        var summary = await OkAsync<MetadataReviewSummaryDto>(await admin.GetAsync($"/api/v1/admin/metadata/review/summary?library={LibPub}"));
        Assert.Equal(0, summary.RecheckPending);
    }

    [Fact]
    public async Task Review_SaysWhichRowsAreBeingCheckedAgain_WhileTheyWait()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: Fakes);
        await SeedAsync(factory);
        RespondWithTheReviewSaga(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin, automatic: true);
        var summaryUrl = $"/api/v1/admin/metadata/review/summary?library={LibPub}";
        Assert.Equal(0, (await OkAsync<MetadataReviewSummaryDto>(await admin.GetAsync(summaryUrl))).RecheckPending);

        using (var scope = factory.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<MetadataAutoMatchService>().QueueOutdatedReviewsAsync());

        var waiting = await OkAsync<MetadataReviewSummaryDto>(await admin.GetAsync(summaryUrl));
        Assert.Equal((1, 1), (waiting.RecheckPending, waiting.Pending));
        var item = Assert.Single((await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=NeedsReview"))).Items);
        Assert.True(item.CheckingAgain);
        Assert.Equal(2, item.Candidates.Count); // the earlier result is still what the row shows
        var auto = Assert.Single((await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=AutoLinked"))).Items);
        Assert.False(auto.CheckingAgain);

        var worker = factory.Services.GetServices<IHostedService>().OfType<MetadataAutoMatchHostedService>().Single();
        Assert.Equal(1, await worker.RunPassAsync(CancellationToken.None));
        Assert.Equal(0, (await OkAsync<MetadataReviewSummaryDto>(await admin.GetAsync(summaryUrl))).RecheckPending);
        Assert.False(Assert.Single((await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=NeedsReview"))).Items).CheckingAgain);
    }

    private static async Task ScanAndWaitAsync(MetadataNetworkWebApplicationFactory factory, HttpClient admin, string libraryPublicId)
    {
        long before;
        using (var scope = factory.Services.CreateScope())
            before = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().ScanRuns.CountAsync(s => s.Status == 2);
        var scan = await admin.PostAsync($"/api/v1/admin/libraries/{libraryPublicId}/scan", null);
        Assert.True(scan.IsSuccessStatusCode, $"scan: {(int)scan.StatusCode}");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().ScanRuns.CountAsync(s => s.Status == 2) > before)
                return;
            await Task.Delay(200);
        }
        Assert.Fail("The scan did not complete in time.");
    }
}
