namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of the stage-2 settings (the one Automatic matching switch
/// with its own consent, thresholds validated against their bounds), the folder
/// Content setting (nearest ancestor wins, folders only, detector suggestion) and
/// the background id-only refresh (cadence, gone records, the 100-per-day cap, the
/// automatic gate - zero calls when it is closed).
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class Stage2SettingsRefreshContentTests : IAsyncLifetime
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

    // --- Settings ---

    [Fact]
    public async Task AutomaticSwitch_NeedsItsOwnConsent_AndFetchOn()
    {
        var settings = _h.Net.Settings();
        Assert.Equal("auto_consent_required", await settings.UpdateAsync(new UpdateMetadataSettingsRequest { AutoMatchEnabled = true }, "admin"));
        Assert.Equal("fetch_required", await settings.UpdateAsync(new UpdateMetadataSettingsRequest
        {
            AutoMatchEnabled = true,
            AcceptedAutoConsentVersion = MetadataAutoConsent.CurrentVersion,
        }, "admin"));

        // Fetch (v1 consent) and Automatic (its own consent) in one request.
        Assert.Null(await settings.UpdateAsync(new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = true,
            AcceptedAutoConsentVersion = MetadataAutoConsent.CurrentVersion,
        }, "admin"));
        var dto = await _h.Net.Settings().GetAsync();
        Assert.True(dto.AutoMatchEnabled);
        Assert.Equal(MetadataAutoConsent.CurrentVersion, dto.AcceptedAutoConsentVersion);
        Assert.NotNull(dto.AutoConsentAt);
        Assert.False(dto.Libraries.Single().AutoMatchActive); // the library's own Fetch is still off
        Assert.True(await _db.Db.AuditEvents.AnyAsync(a => a.Action == AuditActions.MetadataAutoMatchEnable && a.Result == $"auto_consent_v{MetadataAutoConsent.CurrentVersion}"));

        // A client re-sending its state does not need to consent again; off needs nothing.
        Assert.Null(await settings.UpdateAsync(new UpdateMetadataSettingsRequest { AutoMatchEnabled = true }, "admin"));
        Assert.Null(await settings.UpdateAsync(new UpdateMetadataSettingsRequest { AutoMatchEnabled = false }, "admin"));
        Assert.False((await _h.Net.Settings().GetAsync()).AutoMatchEnabled);
    }

    [Fact]
    public async Task Thresholds_ValidatedAgainstBounds_StoredAndReset()
    {
        var settings = _h.Net.Settings();
        var dto = await settings.GetAsync();
        Assert.True(dto.ThresholdsAreDefault);
        Assert.Equal(0.92, dto.Thresholds!.AutoTitle);
        Assert.Equal(0.85, dto.ThresholdBounds!.AutoTitleMin);

        foreach (var bad in new[]
        {
            new MetadataMatchThresholdsDto { AutoTitle = 0.80, Margin = 0.10, ReviewFloor = 0.60 },
            new MetadataMatchThresholdsDto { AutoTitle = 0.92, Margin = 0.31, ReviewFloor = 0.60 },
            new MetadataMatchThresholdsDto { AutoTitle = 0.86, Margin = 0.10, ReviewFloor = 0.88 },
        })
        {
            Assert.Equal("invalid_thresholds", await settings.UpdateAsync(new UpdateMetadataSettingsRequest { Thresholds = bad }, "admin"));
        }

        Assert.Null(await settings.UpdateAsync(new UpdateMetadataSettingsRequest
        {
            Thresholds = new MetadataMatchThresholdsDto { AutoTitle = 0.95, Margin = 0.15, ReviewFloor = 0.70 },
        }, "admin"));
        Assert.Equal(new MatchThresholds(0.95, 0.15, 0.70), await _h.Service().ThresholdsAsync());
        Assert.False((await _h.Net.Settings().GetAsync()).ThresholdsAreDefault);

        Assert.Null(await settings.UpdateAsync(new UpdateMetadataSettingsRequest { ResetThresholds = true }, "admin"));
        Assert.Equal(MatchThresholds.Default, await _h.Service().ThresholdsAsync());
    }

    [Fact]
    public async Task Purge_AlsoDeletesStoredReviewCandidates()
    {
        var folder = await _db.AddFolderAsync(null, "Review Folder");
        await _db.AddLinkAsync(folder, null, SeriesLinkState.NeedsReview);
        _db.Db.MetadataMatchCandidates.Add(new MetadataMatchCandidateEntity
        {
            NodeId = folder.Id,
            Rank = 1,
            Provider = "mangaupdates",
            ExternalId = "31",
            Title = "Candidate",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.Db.SaveChangesAsync();

        var (code, _) = await _db.Links().PurgeAsync(_db.LibraryPublicId, "admin");
        Assert.Equal(MetadataLinkResultCode.Ok, code);
        Assert.False(await _db.Db.MetadataMatchCandidates.AnyAsync());
        Assert.False(await _db.Db.NodeSeriesLinks.AnyAsync());
    }

    // --- Folder Content ---

    private MetadataFolderContentService Content() =>
        new(_db.Db, new AuditService(_db.Db), NullLogger<MetadataFolderContentService>.Instance, [new FakeWorkDetector()]);

    [Fact]
    public async Task FolderContent_NearestAncestorWins_ClearFallsBack_FoldersOnly()
    {
        var top = await _db.AddFolderAsync(null, "Doujin Shelf");
        var mid = await _db.AddFolderAsync(top, "Circle");
        var leaf = await _db.AddFolderAsync(mid, "Leaf");
        var archive = await _db.AddArchiveAsync(leaf, "One");

        var (code, dto) = await Content().SetAsync(top.PublicId, MetadataFolderContent.DoujinshiAndAdultOneShots, "admin");
        Assert.Equal(MetadataLinkResultCode.Ok, code);
        Assert.Equal(MetadataFolderContent.DoujinshiAndAdultOneShots, dto!.Content);
        Assert.Equal(MetadataFolderContent.DoujinshiAndAdultOneShots, dto.Suggested); // detector name signal

        var leafDto = (await Content().GetAsync(leaf.PublicId)).Dto!;
        Assert.Null(leafDto.Content);
        Assert.Equal(MetadataFolderContent.DoujinshiAndAdultOneShots, leafDto.Effective);
        Assert.Equal(top.PublicId, leafDto.SourceNodeId);

        await Content().SetAsync(mid.PublicId, MetadataFolderContent.NotDoujinshi, "admin");
        Assert.Equal(MetadataFolderContent.NotDoujinshi, (await Content().GetAsync(leaf.PublicId)).Dto!.Effective);

        await Content().ClearAsync(mid.PublicId, "admin");
        await Content().ClearAsync(top.PublicId, "admin");
        var cleared = (await Content().GetAsync(leaf.PublicId)).Dto!;
        Assert.Equal(MetadataFolderContent.Auto, cleared.Effective);
        Assert.Null(cleared.SourceNodeId);

        Assert.Equal(MetadataLinkResultCode.NotAFolder, (await Content().SetAsync(archive.PublicId, MetadataFolderContent.Auto, "admin")).Code);
        Assert.Equal(MetadataLinkResultCode.InvalidRequest, (await Content().SetAsync(top.PublicId, (MetadataFolderContent)9, "admin")).Code);
    }

    // --- Background id-only refresh ---

    private async Task<MetadataRecordEntity> LinkedRecordAsync(string externalId, MetadataOriginStatus status, TimeSpan age, int fetchState = 0,
        SeriesLinkState state = SeriesLinkState.Confirmed)
    {
        var folder = await _db.AddFolderAsync(null, "Folder " + externalId);
        var record = await _db.AddRecordAsync(externalId, "Record " + externalId);
        record.OriginStatus = (int)status;
        record.FetchedAt = _h.Time.GetUtcNow() - age;
        record.FetchState = fetchState;
        await _db.Db.SaveChangesAsync();
        await _db.AddLinkAsync(folder, record, state);
        return record;
    }

    [Fact]
    public async Task Refresh_OngoingAfter30Days_CompleteAfter90_GoneNever_IdsOnly()
    {
        var ongoing = await LinkedRecordAsync("811", MetadataOriginStatus.Ongoing, TimeSpan.FromDays(31));
        await LinkedRecordAsync("812", MetadataOriginStatus.Ongoing, TimeSpan.FromDays(10));
        await LinkedRecordAsync("813", MetadataOriginStatus.Complete, TimeSpan.FromDays(60));
        var complete = await LinkedRecordAsync("814", MetadataOriginStatus.Complete, TimeSpan.FromDays(91), state: SeriesLinkState.Auto);
        await LinkedRecordAsync("815", MetadataOriginStatus.Ongoing, TimeSpan.FromDays(400), fetchState: 1); // gone
        await LinkedRecordAsync("816", MetadataOriginStatus.Ongoing, TimeSpan.FromDays(400), state: SeriesLinkState.DontMatch);
        _h.Records[811] = MuJson.Get(811, "Record 811 Renamed");
        await _h.EnableAutomaticAsync();

        Assert.Equal(2, await _h.Refresh().RunPassAsync());

        Assert.Equal(["/v1/series/811", "/v1/series/814"], _h.Handler.Seen.Select(s => s.Uri.AbsolutePath).Order().ToArray());
        Assert.All(_h.Handler.Seen, s => Assert.Equal(HttpMethod.Get, s.Method)); // never a search: no name is sent
        _db.Db.ChangeTracker.Clear();
        Assert.Equal("Record 811 Renamed", (await _db.Db.MetadataRecords.SingleAsync(r => r.Id == ongoing.Id)).Title);
        Assert.Equal(1, (await _db.Db.MetadataRecords.SingleAsync(r => r.Id == complete.Id)).FetchState); // 404 -> gone
        Assert.Equal(2, (await _h.Net.ReadSettingsAsync()).MetadataRefreshUsed);
    }

    [Fact]
    public async Task Refresh_ThatContradictsAnAutoLinksReach_MovesItToReview()
    {
        // 1.30.0 (reach): the refreshed record says 5 volumes; the Auto-linked folder holds volumes 1-30 -> Needs review.
        var record = await LinkedRecordAsync("841", MetadataOriginStatus.Ongoing, TimeSpan.FromDays(40), state: SeriesLinkState.Auto);
        var folder = await _db.Db.CatalogNodes.SingleAsync(n => n.DisplayName == "Folder 841");
        for (var v = 1; v <= 30; v++)
            await _db.AddArchiveAsync(folder, $"Synthetic v{v:00}.cbz");
        _h.Records[841] = MuJson.Get(841, "Record 841", status: "5 Volumes (Ongoing)");
        await _h.EnableAutomaticAsync();

        Assert.Equal(1, await _h.Refresh().RunPassAsync());

        _db.Db.ChangeTracker.Clear();
        var link = await _db.Db.NodeSeriesLinks.SingleAsync(l => l.NodeId == folder.Id);
        Assert.Equal((int)SeriesLinkState.NeedsReview, link.State);
        Assert.Null(link.RecordId);
        var candidate = await _db.Db.MetadataMatchCandidates.SingleAsync(c => c.NodeId == folder.Id);
        Assert.Equal(record.ExternalId, candidate.ExternalId);
        Assert.True(((MatchReason)candidate.Reasons).HasFlag(MatchReason.ReachConflict));
    }

    [Fact]
    public async Task Refresh_GateClosedOrDailyCapSpent_MakesNoCall()
    {
        await LinkedRecordAsync("821", MetadataOriginStatus.Ongoing, TimeSpan.FromDays(40));
        await _h.EnableAutomaticAsync(automatic: false);
        Assert.Equal(0, await _h.Refresh().RunPassAsync());

        await _h.EnableAutomaticAsync();
        var row = await _db.Db.AppSettings.FirstAsync();
        row.MetadataRefreshDayUtc = _h.Net.Budget().Today();
        row.MetadataRefreshUsed = MetadataRefreshService.MaxPerDay;
        await _db.Db.SaveChangesAsync();
        Assert.Equal(0, await _h.Refresh().RunPassAsync());
        Assert.Equal(0, _h.Handler.CallCount);

        _h.Time.Advance(TimeSpan.FromDays(1)); // a new UTC day
        _h.Records[821] = MuJson.Get(821, "Record 821");
        Assert.Equal(1, await _h.Refresh().RunPassAsync());
        Assert.Equal(1, _h.Handler.CallCount);
    }
}
