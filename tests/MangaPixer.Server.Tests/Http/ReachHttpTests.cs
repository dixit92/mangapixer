namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for a series folder's reach and official releases (1.30.0): the Volumes view's progress,
/// the stack's "available in English" field, the "Also in Volume N" field on chapter cards (Folders view, Volumes view, stack
/// slots), the Missing report's progress and upgrade count, and the Official releases tab (filters, order, admin-only, 404). All
/// from stored rows; synthetic names only.
/// </summary>
[Collection("HttpSerial")]
public sealed class ReachHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "rclib1";
    private readonly MangaPixerWebApplicationFactory _factory;

    public ReachHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // rcOwner: volumes 1-14 + chapters 43-57 (volumes 15-19 as chapters; 3 chapters per volume), English 15 volumes (ongoing).
    // rcMixed: volumes 1-2 + chapters 5-8 (volume 2 = chapters 4-6: 5 and 6 are also in it).
    // rcDone:  volumes 1-3 of a complete series, English 3 volumes (complete).
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;
        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Reach Http", RootPath = "/synthetic/rc1", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        async Task SeriesAsync(string pub, string name, IEnumerable<string> archives, MetadataOriginStatus status, int originVolumes,
            string englishNotes, int? latest, int mapVolumes, int perVolume)
        {
            var folder = Node(pub, lib.Id, null, 0, name);
            db.CatalogNodes.Add(folder);
            await db.SaveChangesAsync();
            foreach (var a in archives)
            {
                var arc = Node($"{pub}-{a.Replace(' ', '-')}", lib.Id, folder.Id, 1, $"{name} {a}");
                db.CatalogNodes.Add(arc);
                await db.SaveChangesAsync();
                db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = arc.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 2 });
            }
            var record = new MetadataRecordEntity
            {
                PublicId = pub + "rec",
                Provider = "mangaupdates",
                ExternalId = pub + "1",
                Title = name + " Record",
                Origin = (int)MetadataOrigin.Japan,
                OriginStatus = (int)status,
                OriginVolumes = originVolumes,
                LatestChapter = latest,
                PublishersJson = englishNotes,
                FetchedAt = DateTimeOffset.UtcNow,
            };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
            if (mapVolumes > 0)
            {
                var volumes = Enumerable.Range(1, mapVolumes).Select(v =>
                    $"{{\"v\":\"{v}\",\"c\":[{string.Join(",", Enumerable.Range(perVolume * (v - 1) + 1, perVolume).Select(c => $"\"{c}\""))}]}}");
                db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
                {
                    RecordId = record.Id,
                    Source = (int)VolumeMapSource.MangaDexAggregate,
                    State = (int)VolumeMapState.Ok,
                    VolumesJson = "[" + string.Join(",", volumes) + "]",
                    ChaptersPerVolume = perVolume,
                    KnownVolumeCount = mapVolumes,
                    ContentHash = "h" + pub,
                    Version = 1,
                    FetchedAt = DateTimeOffset.UtcNow,
                });
            }
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = folder.Id,
                LibraryId = lib.Id,
                State = (int)SeriesLinkState.Confirmed,
                RecordId = record.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        static string English(int volumes, string status) =>
            $"[{{\"name\":\"Synthetic Press\",\"kind\":\"english\",\"volumes\":{volumes},\"status\":\"{status}\"}}]";

        await SeriesAsync("rcOwner", "Synthetic Reach",
            Enumerable.Range(1, 14).Select(v => $"v{v:00}").Concat(Enumerable.Range(43, 15).Select(c => $"c{c:000}")),
            MetadataOriginStatus.Ongoing, 22, English(15, "ongoing"), 57, 19, 3);
        await SeriesAsync("rcMixed", "Synthetic Mixed", ["v01", "v02", "c005", "c006", "c007", "c008"],
            MetadataOriginStatus.Ongoing, 5, English(2, "ongoing"), 8, 3, 3);
        await SeriesAsync("rcDone", "Synthetic Done", ["v01", "v02", "v03"], MetadataOriginStatus.Complete, 3, English(3, "complete"), null, 0, 0);
    }

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, int kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == 0 ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<HttpClient> AdminAsync()
    {
        await SeedAsync();
        return await _factory.LoginAsAdminWithChangedPasswordAsync();
    }

    private static async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    [Fact]
    public async Task VolumeView_CarriesTheProgress_AndTheStackOfAnOfficialVolumeHeldAsChaptersSaysSo()
    {
        var admin = await AdminAsync();

        var view = await OkAsync<VolumeViewDto>(await admin.GetAsync("/api/v1/nodes/rcOwner/volume-view"));
        var progress = view.Progress!;
        Assert.Equal(("en", "Synthetic Press", 15, MetadataOriginStatus.Ongoing, 22),
            (progress.Trackers.Language, progress.Trackers.OfficialPublisher, progress.Trackers.OfficialVolumes, progress.Trackers.OfficialStatus,
                progress.Trackers.OriginVolumes));
        Assert.Equal((57, 19), (progress.Reach!.ReachChapter, progress.Reach.ReachVolume));
        Assert.Equal([(1, 14)], progress.Reach.VolumeFiles.Select(s => (s.From, s.To)));
        Assert.Equal([(43, 57)], progress.Reach.Chapters.Select(s => (s.From, s.To)));
        Assert.Equal([15], progress.UpgradeVolumes);
        Assert.Equal((1, 0, 0), (progress.UpgradeCount, progress.MissingVolumes, progress.MissingChapters));
        Assert.Equal(SeriesCompletion.None, progress.Completion);

        var page = await OkAsync<PageResponse<CatalogNodeDto>>(await admin.GetAsync($"/api/v1/libraries/{LibPubId}/browse?parentId=rcOwner&group=volumes&limit=100"));
        var stacks = page.Items.Where(i => i.VolumeStack is not null).ToDictionary(i => i.VolumeStack!.Key, i => i.VolumeStack!);
        Assert.Equal("en", stacks["15"].OfficialRelease);
        Assert.Null(stacks["16"].OfficialRelease);
        var stack = await OkAsync<VolumeStackDto>(await admin.GetAsync("/api/v1/nodes/rcOwner/volumes/15"));
        Assert.Equal("en", stack.OfficialRelease);
    }

    [Fact]
    public async Task ChapterCards_HeldByAVolumeFile_SayAlsoInVolume_InEveryView()
    {
        var admin = await AdminAsync();

        var flat = await OkAsync<PageResponse<CatalogNodeDto>>(await admin.GetAsync($"/api/v1/libraries/{LibPubId}/browse?parentId=rcMixed&group=flat"));
        var also = flat.Items.ToDictionary(i => i.DisplayName, i => i.AlsoInVolume);
        Assert.Equal(("2", "2", null, null, null), (also["Synthetic Mixed c005"], also["Synthetic Mixed c006"], also["Synthetic Mixed c007"],
            also["Synthetic Mixed v02"], also["Synthetic Mixed v01"]));

        var stack = await OkAsync<VolumeStackDto>(await admin.GetAsync("/api/v1/nodes/rcMixed/volumes/2"));
        var slots = stack.Slots.Where(s => s.Item is not null).ToDictionary(s => s.Item!.DisplayName, s => s.Item!.AlsoInVolume);
        Assert.Equal("2", slots["Synthetic Mixed c005"]);
        Assert.Null(slots["Synthetic Mixed v02"]);

        var view = await OkAsync<VolumeViewDto>(await admin.GetAsync("/api/v1/nodes/rcMixed/volume-view"));
        Assert.Equal((2, 8), (view.Progress!.Reach!.OverlapChapters, view.Progress.Reach.ReachChapter));
        Assert.Equal([(7, 8)], view.Progress.Reach.Chapters.Select(s => (s.From, s.To)));
    }

    [Fact]
    public async Task MissingReport_ReadsTheSameEngine_AnUpgradeIsNeverBehind()
    {
        var admin = await AdminAsync();

        var page = await OkAsync<MissingReportPageDto>(await admin.GetAsync($"/api/v1/admin/metadata/missing?library={LibPubId}"));
        Assert.Equal(1, page.Summary.Upgrades);
        var owner = page.Items.Single(i => i.NodeId == "rcOwner");
        Assert.Equal(MissingVerdict.UpToDate, owner.Verdict);
        Assert.Equal([15], owner.Progress!.UpgradeVolumes);
        var mixed = page.Items.Single(i => i.NodeId == "rcMixed");
        Assert.NotEqual(MissingVerdict.Mixed, mixed.Verdict); // a one-folder mix gets numbers now
        Assert.Equal(8, mixed.Chapters!.Have);
    }

    [Fact]
    public async Task OfficialReleases_ListsUpgradesAndFinishedSeries_WithFilters()
    {
        var admin = await AdminAsync();

        var toAct = await OkAsync<OfficialReleasesPageDto>(await admin.GetAsync($"/api/v1/admin/metadata/official-releases?library={LibPubId}"));
        Assert.Equal("en", toAct.Language);
        Assert.Equal((3, 1, 0, 1), (toAct.Summary.Series, toAct.Summary.Upgrades, toAct.Summary.FinishedNotHeld, toAct.Summary.CompleteCollections));
        var row = Assert.Single(toAct.Items);
        Assert.Equal(("rcOwner", "Synthetic Reach Record", "Reach Http"), (row.NodeId, row.RecordTitle, row.LibraryName));

        var complete = await OkAsync<OfficialReleasesPageDto>(await admin.GetAsync($"/api/v1/admin/metadata/official-releases?library={LibPubId}&filter=Complete"));
        var done = Assert.Single(complete.Items);
        Assert.Equal(("rcDone", SeriesCompletion.CompleteCollection, CompletionBasis.OfficialVolumes),
            (done.NodeId, done.Progress.Completion, done.Progress.CompletionBasis));

        var all = await OkAsync<OfficialReleasesPageDto>(await admin.GetAsync($"/api/v1/admin/metadata/official-releases?library={LibPubId}&filter=All&limit=2"));
        Assert.Equal((3, 2, "2"), (all.Total, all.Items.Count, all.NextCursor));
        Assert.Equal(["rcOwner", "rcDone"], all.Items.Select(i => i.NodeId)); // upgrades first, then complete collections

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/metadata/official-releases?library=no-such-lib")).StatusCode);
    }

    [Fact]
    public async Task OfficialReleases_IsAdminOnly()
    {
        await SeedAsync();
        using var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/admin/metadata/official-releases")).StatusCode);

        var admin = await AdminAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = "rcreader", IsAdmin = false });
        if (created.IsSuccessStatusCode)
        {
            var url = (await created.Content.ReadFromJsonAsync<CreateUserResponse>(TestJson.Web))!.ActivationUrl!;
            var token = Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
            (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/activate",
                new ActivateAccountRequest { Token = token, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        }
        using var reader = _factory.CreateClient();
        (await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "rcreader", Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/metadata/official-releases")).StatusCode);
    }
}
