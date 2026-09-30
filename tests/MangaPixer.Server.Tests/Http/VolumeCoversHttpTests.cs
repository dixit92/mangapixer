namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Threading.RateLimiting;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Tests.Server.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of the MangaDex companion and the volume-cover pass (1.29.0), through the REAL DI
/// graph: the settings card lists MangaDex; the hosted service's volume-cover tick reaches the provider only through the
/// scripted handler (recorded MangaDex answers, a fake worker renderer) and stores covers; the status endpoint; the
/// companion endpoints (admin-only, 404 / 400, "Change MangaDex match...", "Not on MangaDex", the volume-covers switch);
/// an admin Refresh also reads the companion; removing MangaDex from the allowlist pauses the pass but keeps the stored
/// covers; "Delete stored volume covers". No title, folder name or image address reaches a log line.
/// </summary>
[Trait("Category", "Http")]
[Collection("HttpSerial")]
public sealed class VolumeCoversHttpTests
{
    private const string LibPub = "vclib1";
    private const string Sentinel = "Qzvvolsentinel";

    private static void Services(IServiceCollection services, FakeCoverRenderer renderer)
    {
        var fast = new TokenBucketRateLimiterOptions { TokenLimit = 1000, TokensPerPeriod = 1000, ReplenishmentPeriod = TimeSpan.FromMilliseconds(10), QueueLimit = 10 };
        services.AddSingleton(new MetadataRateLimitOptions { AutomaticInterval = TimeSpan.Zero, MangaDexApi = fast, MangaDexImages = fast });
        services.AddSingleton<ICoverRenderer>(renderer);
    }

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, CatalogNodeKind kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Volume Lib", RootPath = "/synthetic/vc", CreatedAt = DateTimeOffset.UtcNow, MetadataEnabled = true };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var folder = Node("vcFolder", lib.Id, null, CatalogNodeKind.Folder, $"{Sentinel} Saga");
        var unlinked = Node("vcLoose", lib.Id, null, CatalogNodeKind.Folder, $"{Sentinel} Loose");
        db.CatalogNodes.AddRange(folder, unlinked);
        await db.SaveChangesAsync();
        var archive = Node("vcA1", lib.Id, folder.Id, CatalogNodeKind.Archive, $"{Sentinel} Saga v01");
        db.CatalogNodes.Add(archive);
        await db.SaveChangesAsync();
        db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = archive.Id, ContentVersion = 1, PageCount = 2 });
        var record = new MetadataRecordEntity
        {
            PublicId = "vcrec",
            Provider = "mangaupdates",
            ExternalId = MuFixtures.BerserkId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title = "Berserk",
            OriginVolumes = 43,
            FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = folder.Id,
            LibraryId = lib.Id,
            State = (int)SeriesLinkState.Auto,
            RecordId = record.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpClient> StartAsync(MetadataNetworkWebApplicationFactory factory, bool automatic = true)
    {
        await SeedAsync(factory);
        factory.Handler.Respond = request =>
            request.RequestUri!.Host == MetadataHttp.MangaDexImageHost
                ? ScriptedHandler.Bytes([.. MuFixtures.Png, (byte)request.RequestUri.AbsolutePath.Length], "image/jpeg")
                : MdFixtures.Route(request)!; // null: the fixture routing of MangaUpdates / AniList (ScriptedHandler falls back)
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = automatic,
            AcceptedAutoConsentVersion = automatic ? MetadataAutoConsent.CurrentVersion : null,
        })).EnsureSuccessStatusCode();
        return admin;
    }

    private static MetadataAutoMatchHostedService Worker(MetadataNetworkWebApplicationFactory factory) =>
        factory.Services.GetServices<IHostedService>().OfType<MetadataAutoMatchHostedService>().Single();

    private static async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static int MangaDexCalls(MetadataNetworkWebApplicationFactory factory) =>
        factory.Handler.Seen.Count(s => s.Uri.Host is MetadataHttp.MangaDexApiHost or MetadataHttp.MangaDexImageHost);

    [Fact]
    public async Task TheSettingsCard_ListsMangaDex_WithItsHostsAndWhatIsSent()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true, configureServices: s => Services(s, new FakeCoverRenderer()));
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var settings = await OkAsync<MetadataSettingsDto>(await admin.GetAsync("/api/v1/admin/metadata/settings"));

        var card = Assert.Single(settings.Providers, p => p.Id == "mangadex");
        Assert.Equal("MangaDex", card.Name);
        Assert.Equal(new[] { "api.mangadex.org", "uploads.mangadex.org" }, card.Hosts);
        Assert.Contains("already linked to MangaUpdates", card.UsedFor, StringComparison.Ordinal);
        Assert.Contains("never a folder or file name", card.Sends, StringComparison.Ordinal);
        Assert.True(card.Allowed);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task TheWorkerTick_ThroughDI_FindsTheCompanion_StoresCovers_AndLogsNoNames()
    {
        var sink = new CollectingSink();
        var renderer = new FakeCoverRenderer();
        using var factory = new MetadataNetworkWebApplicationFactory(sink: sink, configureServices: s => Services(s, renderer));
        var admin = await StartAsync(factory);

        await Worker(factory).RunVolumeCoversAsync(CancellationToken.None);

        Assert.Equal(5, MangaDexCalls(factory)); // search, cover list, aggregate, aggregate in the preferred language, the volume 1 image
        Assert.DoesNotContain(factory.Handler.Seen, s => Uri.UnescapeDataString(s.Uri.ToString()).Contains(Sentinel, StringComparison.Ordinal));
        var status = await OkAsync<CoverPassStatusDto>(await admin.GetAsync("/api/v1/admin/metadata/volume-covers/status"));
        Assert.Equal((0, 1, (string?)null), (status.SeriesPending, status.CoversStored, status.Waiting));
        Assert.True(status.CoversListed > 0);
        Assert.Single(renderer.Requests);

        var companions = await OkAsync<List<CompanionDto>>(await admin.GetAsync("/api/v1/admin/metadata/nodes/vcFolder/companions"));
        var mangaDex = Assert.Single(companions, c => c.Provider == "mangadex");
        Assert.Equal((CompanionState.Auto, "MangaDex", "https://mangadex.org/title/" + MdFixtures.BerserkId), (mangaDex.State, mangaDex.ProviderName, mangaDex.SiteUrl));
        // An archive inside the linked folder resolves to the same series.
        Assert.Single(await OkAsync<List<CompanionDto>>(await admin.GetAsync("/api/v1/admin/metadata/nodes/vcA1/companions")), c => c.Provider == "mangadex");

        foreach (var e in sink.Events)
        {
            var rendered = e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Value.ToString())) + " " + e.Exception;
            Assert.False(rendered.Contains(Sentinel, StringComparison.OrdinalIgnoreCase), "A name leaked into a log line: " + e.MessageTemplate.Text);
            Assert.False(rendered.Contains("Berserk", StringComparison.OrdinalIgnoreCase), "A title leaked into a log line: " + e.MessageTemplate.Text);
            Assert.False(rendered.Contains("uploads.mangadex", StringComparison.OrdinalIgnoreCase), "An image address leaked into a log line: " + e.MessageTemplate.Text);
        }
    }

    [Fact]
    public async Task ChangingThePreferredLanguage_ReadsTheReleasedChaptersAgain_ForThatLanguage()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: s => Services(s, new FakeCoverRenderer()));
        var admin = await StartAsync(factory);
        await Worker(factory).RunVolumeCoversAsync(CancellationToken.None);
        Assert.Contains(factory.Handler.Seen, s => Uri.UnescapeDataString(s.Uri.Query) == "?includeUnavailable=1&translatedLanguage[]=en");

        var saved = await OkAsync<MetadataSettingsDto>(await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings",
            new UpdateMetadataSettingsRequest { PreferredCoverLanguage = "fr" }));
        Assert.Equal("fr", saved.PreferredCoverLanguage);
        await Worker(factory).RunVolumeCoversAsync(CancellationToken.None);

        Assert.Contains(factory.Handler.Seen, s => Uri.UnescapeDataString(s.Uri.Query) == "?includeUnavailable=1&translatedLanguage[]=fr");
        using var scope = factory.Services.CreateScope();
        var map = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().SeriesVolumeMaps.SingleAsync();
        Assert.Equal(("fr", "[]"), (map.ReleasedLanguage, map.ReleasedChaptersJson));
    }

    [Fact]
    public async Task CompanionEndpoints_AreAdminOnly_And404ForAnUnlinkedNode()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: s => Services(s, new FakeCoverRenderer()));
        var admin = await StartAsync(factory);
        var reader = await factory.CreateReaderClientAsync("vcreader", LibPub);

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/metadata/nodes/vcFolder/companions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/v1/admin/metadata/nodes/vcFolder/companions/mangadex",
            new CompanionReferenceRequest { Reference = MdFixtures.BerserkId })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync("/api/v1/admin/metadata/nodes/vcFolder/companions/mangadex")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/v1/admin/metadata/nodes/vcFolder/companions/mangadex/recheck", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/metadata/volume-covers/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync("/api/v1/admin/metadata/volume-covers")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/metadata/nodes/vcLoose/companions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/metadata/nodes/no-such-node/companions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/vcLoose/companions/mangadex",
            new CompanionReferenceRequest { Reference = MdFixtures.BerserkId })).StatusCode);
        Assert.Equal(0, MangaDexCalls(factory));
    }

    [Fact]
    public async Task ChangeMatch_ByReference_OneGetById_ThenNotOnMangaDex_ThenTheSwitch()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: s => Services(s, new FakeCoverRenderer()));
        var admin = await StartAsync(factory, automatic: false); // admin actions need only "Fetch from the web"

        var bad = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/vcFolder/companions/mangadex", new CompanionReferenceRequest { Reference = "berserk" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_reference", (await bad.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);

        var set = await OkAsync<List<CompanionDto>>(await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/vcFolder/companions/mangadex",
            new CompanionReferenceRequest { Reference = "https://mangadex.org/title/" + MdFixtures.ChainsawManId + "/chainsaw-man" }));
        Assert.Equal(CompanionState.Confirmed, Assert.Single(set).State);
        Assert.Equal(["/manga/" + MdFixtures.ChainsawManId], factory.Handler.Seen.Select(s => s.Uri.AbsolutePath));

        var unknown = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/vcFolder/companions/mangadex",
            new CompanionReferenceRequest { Reference = "00000000-0000-4000-8000-000000000000" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var none = await OkAsync<List<CompanionDto>>(await admin.DeleteAsync("/api/v1/admin/metadata/nodes/vcFolder/companions/mangadex"));
        Assert.Equal((CompanionState.None, (string?)null), (none[0].State, none[0].SiteUrl));

        // "Volume covers from the web" off: companion requests are refused before any call.
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { VolumeCoversEnabled = false })).EnsureSuccessStatusCode();
        var calls = factory.Handler.CallCount;
        var off = await admin.PostAsync("/api/v1/admin/metadata/nodes/vcFolder/companions/mangadex/recheck", null);
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Equal("volume_covers_off", (await off.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal(calls, factory.Handler.CallCount);
    }

    [Fact]
    public async Task AnAdminRefresh_AlsoReadsTheCompanion_WithAutomaticMatchingOff()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: s => Services(s, new FakeCoverRenderer()));
        var admin = await StartAsync(factory, automatic: false);

        var refreshed = await OkAsync<MetadataRefreshResultDto>(await admin.PostAsync("/api/v1/admin/metadata/nodes/vcFolder/refresh", null));

        Assert.Equal("Ok", refreshed.State);
        var paths = factory.Handler.Seen.Where(s => s.Uri.Host == MetadataHttp.MangaDexApiHost).Select(s => s.Uri.AbsolutePath).ToList();
        Assert.Equal(["/manga", "/cover", $"/manga/{MdFixtures.BerserkId}/aggregate", $"/manga/{MdFixtures.BerserkId}/aggregate"], paths);
        Assert.DoesNotContain(factory.Handler.Seen, s => s.Uri.Host == MetadataHttp.MangaDexImageHost); // lists only, no images
    }

    [Fact]
    public async Task StoringTheVolumeList_RechecksTheRecordsAutoLinks_AFolderFarPastItDropsToReview()
    {
        // 1.30.0 (reach): the wiring of the reach check - the admin Refresh reads the MangaDex volume list, the store runs the check.
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: s => Services(s, new FakeCoverRenderer()));
        var admin = await StartAsync(factory, automatic: false);
        long farId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var lib = await db.Libraries.SingleAsync(l => l.PublicId == LibPub);
            var record = await db.MetadataRecords.SingleAsync(r => r.PublicId == "vcrec");
            var far = Node("vcFar", lib.Id, null, CatalogNodeKind.Folder, $"{Sentinel} Far");
            db.CatalogNodes.Add(far);
            await db.SaveChangesAsync();
            db.CatalogNodes.Add(Node("vcFarA", lib.Id, far.Id, CatalogNodeKind.Archive, $"{Sentinel} Far c2000"));
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = far.Id,
                LibraryId = lib.Id,
                State = (int)SeriesLinkState.Auto,
                RecordId = record.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            farId = far.Id;
        }

        (await admin.PostAsync("/api/v1/admin/metadata/nodes/vcFolder/refresh", null)).EnsureSuccessStatusCode();

        using var check = factory.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        Assert.Equal((int)SeriesLinkState.NeedsReview, (await after.NodeSeriesLinks.SingleAsync(l => l.NodeId == farId)).State);
        Assert.Equal((int)SeriesLinkState.Auto, (await after.NodeSeriesLinks.SingleAsync(l => l.Node!.PublicId == "vcFolder")).State);
        var candidate = await after.MetadataMatchCandidates.SingleAsync(c => c.NodeId == farId);
        Assert.Equal((int)MatchReason.ReachConflict, candidate.Reasons);
    }

    [Fact]
    public async Task DeletingAllFetchedWebData_AlsoRemovesTheMangaDexRecord_AndItsCoverFiles()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: s => Services(s, new FakeCoverRenderer()));
        var admin = await StartAsync(factory);
        await Worker(factory).RunVolumeCoversAsync(CancellationToken.None);
        Assert.Single(Directory.GetFiles(Path.Combine(factory.DataRoot, "volume-covers"), "*.webp", SearchOption.AllDirectories));

        (await admin.PostAsJsonAsync("/api/v1/admin/metadata/purge", new MetadataPurgeRequest { LibraryId = null })).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        Assert.False(await db.MetadataRecords.AnyAsync());
        Assert.False(await db.VolumeCovers.AnyAsync());
        Assert.False(await db.MetadataCompanions.AnyAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(factory.DataRoot, "volume-covers"), "*.webp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RemovingMangaDex_PausesThePass_StoredCoversStay_UntilDeleted()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: s => Services(s, new FakeCoverRenderer()));
        var admin = await StartAsync(factory);
        await Worker(factory).RunVolumeCoversAsync(CancellationToken.None);
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { RemovedProviders = ["mangadex"] })).EnsureSuccessStatusCode();
        var calls = factory.Handler.CallCount;

        await Worker(factory).RunVolumeCoversAsync(CancellationToken.None);

        Assert.Equal(calls, factory.Handler.CallCount);
        var status = await OkAsync<CoverPassStatusDto>(await admin.GetAsync("/api/v1/admin/metadata/volume-covers/status"));
        Assert.Equal(("provider_not_allowed", 1), (status.Waiting, status.CoversStored));
        var coverFiles = Directory.GetFiles(Path.Combine(factory.DataRoot, "volume-covers"), "*.webp", SearchOption.AllDirectories);
        Assert.Single(coverFiles);

        var deleted = await OkAsync<CoverPassStatusDto>(await admin.DeleteAsync("/api/v1/admin/metadata/volume-covers"));
        Assert.Equal((0, 0), (deleted.CoversStored, deleted.CoversListed));
        Assert.Empty(Directory.GetFiles(Path.Combine(factory.DataRoot, "volume-covers"), "*.webp", SearchOption.AllDirectories));
        Assert.Equal(calls, factory.Handler.CallCount); // local only
    }
}
