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
/// HTTP tests (WebApplicationFactory) for the missing volumes / chapters report (1.28.0): the list with its
/// library and "only missing" filters, the per-node row, 404s, admin-only access, and the DI wiring. The report
/// reads stored rows only. Synthetic names only.
/// </summary>
[Collection("HttpSerial")]
public sealed class MissingReportHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "mrlib1";
    private readonly MangaPixerWebApplicationFactory _factory;

    public MissingReportHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // mrBehind: v01-v03 linked to a record with an English total of 10; mrDone: v01-v02 of 2.
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;
        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Missing Http", RootPath = "/synthetic/mr1", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        async Task SeriesAsync(string pub, string name, int englishVolumes, params string[] archives)
        {
            var folder = Node(pub, lib.Id, null, 0, name);
            db.CatalogNodes.Add(folder);
            await db.SaveChangesAsync();
            foreach (var (a, i) in archives.Select((a, i) => (a, i)))
            {
                var arc = Node($"{pub}a{i}", lib.Id, folder.Id, 1, a);
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
                OriginVolumes = englishVolumes + 4,
                PublishersJson = $"[{{\"name\":\"Print English\",\"kind\":\"english\",\"volumes\":{englishVolumes}}}]",
                FetchedAt = DateTimeOffset.UtcNow,
            };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
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

        await SeriesAsync("mrBehind", "Synthetic Behind", 10, "Synthetic Behind v01", "Synthetic Behind v02", "Synthetic Behind v03");
        await SeriesAsync("mrDone", "Synthetic Done", 2, "Synthetic Done v01", "Synthetic Done v02");
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
    public async Task List_FiltersByLibraryAndMissing()
    {
        var admin = await AdminAsync();

        var all = await OkAsync<MissingReportPageDto>(await admin.GetAsync($"/api/v1/admin/metadata/missing?library={LibPubId}"));
        Assert.Equal(2, all.Total);
        Assert.Equal((2, 1, 1), (all.Summary.Series, all.Summary.Behind, all.Summary.UpToDate));
        var behind = all.Items[0];
        Assert.Equal("mrBehind", behind.NodeId);
        Assert.Equal(MissingVerdict.Behind, behind.Verdict);
        Assert.Equal((3, 10, 7, MissingTotalSource.English), (behind.Volumes!.Have, behind.Volumes.Available, behind.Volumes.BehindBy, behind.Volumes.Source));
        Assert.Equal("Missing Http", behind.LibraryName);
        Assert.Equal("/api/v1/items/mrBehinda0/cover", behind.CoverUrl);

        var missing = await OkAsync<MissingReportPageDto>(await admin.GetAsync($"/api/v1/admin/metadata/missing?library={LibPubId}&onlyMissing=true"));
        Assert.Equal("mrBehind", Assert.Single(missing.Items).NodeId);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/metadata/missing?library=nope")).StatusCode);
    }

    [Fact]
    public async Task ForNode_ReturnsTheRow_Or404()
    {
        var admin = await AdminAsync();

        var row = await OkAsync<MissingSeriesDto>(await admin.GetAsync("/api/v1/admin/metadata/missing/mrDone"));
        Assert.Equal(MissingVerdict.UpToDate, row.Verdict);
        Assert.Equal("Synthetic Done Record", row.RecordTitle);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/metadata/missing/mrDonea0")).StatusCode); // an archive
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/metadata/missing/no-such-node")).StatusCode);
    }

    [Fact]
    public async Task Report_IsAdminOnly()
    {
        await SeedAsync();
        using var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/admin/metadata/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/admin/metadata/missing/mrDone")).StatusCode);

        var admin = await AdminAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = "mrreader", IsAdmin = false });
        if (created.IsSuccessStatusCode)
        {
            var url = (await created.Content.ReadFromJsonAsync<CreateUserResponse>(TestJson.Web))!.ActivationUrl!;
            var token = Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
            (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/activate",
                new ActivateAccountRequest { Token = token, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        }
        using var reader = _factory.CreateClient();
        (await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "mrreader", Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/metadata/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/metadata/missing/mrDone")).StatusCode);
    }

    [Fact]
    public async Task NodeLine_IsForEveryoneWithAccess_404WithoutIt()
    {
        var admin = await AdminAsync();
        using var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/nodes/mrBehind/missing")).StatusCode);

        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = "mrlinereader", IsAdmin = false });
        if (created.IsSuccessStatusCode)
        {
            var url = (await created.Content.ReadFromJsonAsync<CreateUserResponse>(TestJson.Web))!.ActivationUrl!;
            var token = Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
            (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/activate",
                new ActivateAccountRequest { Token = token, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        }
        using var reader = _factory.CreateClient();
        (await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "mrlinereader", Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();

        // No grant for the library: 404, never 403 (a non-member cannot learn the node exists).
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/v1/nodes/mrBehind/missing")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var libId = await db.Libraries.Where(l => l.PublicId == LibPubId).Select(l => l.Id).SingleAsync();
            var userId = await db.Users.Where(u => u.UserName == "mrlinereader").Select(u => u.Id).SingleAsync();
            if (!await db.LibraryGrants.AnyAsync(g => g.UserId == userId && g.LibraryId == libId))
            {
                db.LibraryGrants.Add(new LibraryGrantEntity { UserId = userId, LibraryId = libId, GrantedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
        }

        var row = await OkAsync<MissingSeriesDto>(await reader.GetAsync("/api/v1/nodes/mrBehind/missing"));
        Assert.Equal((MissingVerdict.Behind, 3, 10), (row.Verdict, row.Volumes!.Have, row.Volumes.Available));
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/v1/nodes/mrBehinda0/missing")).StatusCode); // an archive
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/v1/nodes/no-such-node/missing")).StatusCode);
        // The admin report stays admin-only.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/metadata/missing/mrBehind")).StatusCode);
    }

    private const string SeasonLibPubId = "mrlib2";

    // 1.29.0, the live finding's shape: mrSeasons = a loose 000 + Season 1 (chapters 1-3) + Season 2 (4-6), status "8 Chapters";
    // mrRestart = Season 1 (1-3) + Season 2 (1-2), the numbering starts again; mrPrologue = a lone 000 of a 223-chapter record.
    private async Task SeedSeasonsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == SeasonLibPubId))
            return;
        var lib = new LibraryEntity { PublicId = SeasonLibPubId, DisplayName = "Missing Seasons", RootPath = "/synthetic/mr2", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        async Task<CatalogNodeEntity> AddAsync(string pub, long? parent, int kind, string name)
        {
            var node = Node(pub, lib.Id, parent, kind, name);
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            if (kind == 1)
                db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 2 });
            return node;
        }

        async Task SeriesAsync(string pub, string status, string[] loose, params (string Name, int[] Chapters)[] seasons)
        {
            var folder = await AddAsync(pub, null, 0, pub + " Series");
            foreach (var (a, i) in loose.Select((a, i) => (a, i)))
                await AddAsync($"{pub}a{i}", folder.Id, 1, a);
            foreach (var (season, si) in seasons.Select((x, i) => (x, i)))
            {
                var sub = await AddAsync($"{pub}s{si}", folder.Id, 0, season.Name);
                foreach (var c in season.Chapters)
                    await AddAsync($"{pub}s{si}c{c}", sub.Id, 1, $"Synthetic - Chapter {c:D3}");
            }
            var record = new MetadataRecordEntity
            {
                PublicId = pub + "rec",
                Provider = "mangaupdates",
                ExternalId = pub + "1",
                Title = pub + " Record",
                StatusText = status,
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

        await SeriesAsync("mrSeasons", "8 Chapters (Ongoing)", ["000"], ("Season 1", [1, 2, 3]), ("Season 2", [4, 5, 6]));
        await SeriesAsync("mrRestart", "223 Chapters (Ongoing)", ["000"], ("Season 1", [1, 2, 3]), ("Season 2", [1, 2]));
        await SeriesAsync("mrPrologue", "223 Chapters (Ongoing)", ["000"]);
    }

    [Fact]
    public async Task SeasonSubfolders_AreCounted_RestartsGiveNoVerdict_AndAPrologueIsNotProgress()
    {
        await SeedSeasonsAsync();
        var admin = await AdminAsync();

        var page = await OkAsync<MissingReportPageDto>(await admin.GetAsync($"/api/v1/admin/metadata/missing?library={SeasonLibPubId}"));
        Assert.Equal((3, 1, 2), (page.Summary.Series, page.Summary.Behind, page.Summary.NoVerdict));
        var seasons = page.Items.Single(i => i.NodeId == "mrSeasons");
        Assert.Equal(MissingVerdict.Behind, seasons.Verdict);
        Assert.Equal((0, 6, 8, 2), (seasons.Chapters!.Lowest, seasons.Chapters.Have, seasons.Chapters.Available, seasons.Chapters.BehindBy));
        var restarting = page.Items.Single(i => i.NodeId == "mrRestart");
        Assert.Equal(MissingVerdict.Restarts, restarting.Verdict);
        Assert.Null(restarting.Chapters);
        var prologue = page.Items.Single(i => i.NodeId == "mrPrologue");
        Assert.Equal(MissingVerdict.NoUnits, prologue.Verdict);
        Assert.Null(prologue.Chapters);

        var restart = await OkAsync<MissingSeriesDto>(await admin.GetAsync("/api/v1/admin/metadata/missing/mrRestart"));
        Assert.Equal(MissingVerdict.Restarts, restart.Verdict);

        // The series page line (every reader with access) reads the same row: never "chapter 0 of 223".
        var line = await OkAsync<MissingSeriesDto>(await admin.GetAsync("/api/v1/nodes/mrPrologue/missing"));
        Assert.Null(line.Chapters);
        var seasonsLine = await OkAsync<MissingSeriesDto>(await admin.GetAsync("/api/v1/nodes/mrSeasons/missing"));
        Assert.Equal((6, 8), (seasonsLine.Chapters!.Have, seasonsLine.Chapters.Available));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/nodes/mrSeasonss0/missing")).StatusCode); // a Season folder has no own link
    }

    [Fact]
    public async Task Conversion_IsWired_AndGatedByTheFetchSwitch()
    {
        var admin = await AdminAsync();

        // Fetch from the web is off on a fresh instance: refused before any request.
        var one = await admin.PostAsync("/api/v1/admin/metadata/missing/mrBehind/conversion", null);
        Assert.Equal(HttpStatusCode.Conflict, one.StatusCode);
        Assert.Equal("metadata_disabled", (await one.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/admin/metadata/missing/no-such-node/conversion", null)).StatusCode);

        var batch = await OkAsync<MissingConversionBatchResultDto>(
            await admin.PostAsJsonAsync("/api/v1/admin/metadata/missing/conversions", new MissingConversionBatchRequest { Library = LibPubId }, TestJson.Web));
        Assert.Equal((0, "metadata_disabled"), (batch.Looked, batch.StoppedCode));

        // The settings list every approved site, all in.
        var settings = await OkAsync<MetadataSettingsDto>(await admin.GetAsync("/api/v1/admin/metadata/settings"));
        Assert.Equal(new[] { "mangaupdates", "mangadex", "anilist" }, settings.Providers.Select(p => p.Id));
        Assert.All(settings.Providers, p => Assert.True(p.Allowed));
        Assert.False(settings.ConsentRenewalNeeded);
    }
}
