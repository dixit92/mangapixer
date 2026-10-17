namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for the browse filter by series completion (1.44.0 lane C):
/// <c>GET /api/v1/libraries/{id}/browse?completion=&lt;answer&gt;</c> keeps exactly the linked series folders the Completion tab gives that
/// answer, drops other folders and loose files, pages and counts honestly, applies inside a category folder, does nothing where a level
/// holds no linked series, ignores unknown values, and works for a reader with access. Stored rows only; synthetic names.
/// The response is read through a local shape so the file also compiles against a server without the filter (the red run).
/// </summary>
public sealed class BrowseCompletionHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "bclib1";
    private static readonly string[] Answers = ["HaveItAll", "FinishedMissing", "UpToDate", "MissingSome", "CantTell"];
    private readonly MangaPixerWebApplicationFactory _factory;

    public BrowseCompletionHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Node(string Id, string DisplayName, string Kind);

    private sealed record Counts(int HaveItAll, int FinishedMissing, int UpToDate, int MissingSome, int CantTell);

    private sealed record Page(List<Node> Items, int TotalCount, string? NextCursor, bool HasMore, int? LinkedSeries, Counts? CompletionCounts);

    // Root: five answered series + one with tracking off, a plain folder with two files, a loose file, and a category folder ("Shelf")
    // holding one series and one plain folder.
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;
        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Completion Http", RootPath = "/synthetic/bc1", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        async Task<CatalogNodeEntity> FolderAsync(string pub, long? parent, string name, params string[] archives)
        {
            var folder = Node(pub, lib.Id, parent, 0, name);
            db.CatalogNodes.Add(folder);
            await db.SaveChangesAsync();
            foreach (var a in archives)
                await ArchiveAsync($"{pub}-{a}", folder.Id, $"{name} {a}");
            return folder;
        }

        async Task ArchiveAsync(string pub, long? parent, string name)
        {
            var arc = Node(pub, lib.Id, parent, 1, name);
            db.CatalogNodes.Add(arc);
            await db.SaveChangesAsync();
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = arc.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 2 });
            await db.SaveChangesAsync();
        }

        async Task SeriesAsync(string pub, long? parent, string name, MetadataOriginStatus status, int originVolumes, int english,
            string englishStatus, int mapVolumes, bool trackingOff, params string[] archives)
        {
            var folder = await FolderAsync(pub, parent, name, archives);
            var record = new MetadataRecordEntity
            {
                PublicId = pub + "rec",
                Provider = "mangaupdates",
                ExternalId = pub + "1",
                Title = name + " Record",
                Origin = (int)MetadataOrigin.Japan,
                OriginStatus = (int)status,
                OriginVolumes = originVolumes,
                PublishersJson = $"[{{\"name\":\"Synthetic Press\",\"kind\":\"english\",\"volumes\":{english},\"status\":\"{englishStatus}\"}}]",
                FetchedAt = DateTimeOffset.UtcNow,
            };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
            if (mapVolumes > 0)
            {
                var volumes = Enumerable.Range(1, mapVolumes).Select(v =>
                    $"{{\"v\":\"{v}\",\"c\":[{string.Join(",", Enumerable.Range(3 * (v - 1) + 1, 3).Select(c => $"\"{c}\""))}]}}");
                db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
                {
                    RecordId = record.Id,
                    Source = (int)VolumeMapSource.MangaDexAggregate,
                    State = (int)VolumeMapState.Ok,
                    VolumesJson = "[" + string.Join(",", volumes) + "]",
                    ChaptersPerVolume = 3,
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
                State = (int)(pub.EndsWith('a') ? SeriesLinkState.Auto : SeriesLinkState.Confirmed),
                RecordId = record.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            if (trackingOff)
            {
                db.DeclaredFacts.Add(new DeclaredFactEntity
                {
                    LibraryId = lib.Id,
                    NodeId = folder.Id,
                    Key = DeclaredFactKeys.Tracking,
                    Value = DeclaredFactKeys.TrackingOff,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        var complete = MetadataOriginStatus.Complete;
        var ongoing = MetadataOriginStatus.Ongoing;
        await SeriesAsync("bcAlpha", null, "Alpha Done", complete, 3, 3, "complete", 0, false, "v01", "v02", "v03");
        await SeriesAsync("bcBeta", null, "Beta Gap", complete, 3, 3, "complete", 0, false, "v01", "v03");
        await SeriesAsync("bcGamma", null, "Gamma Behind", ongoing, 6, 4, "ongoing", 4, false, "v01", "v02");
        await SeriesAsync("bcDelta", null, "Delta Current", ongoing, 6, 2, "ongoing", 6, false, "v01", "v02");
        await SeriesAsync("bcEpsila", null, "Epsilon Done", complete, 2, 2, "complete", 0, false, "v01", "v02");
        await SeriesAsync("bcZeta", null, "Zeta Untracked", complete, 3, 3, "complete", 0, true, "v01", "v02", "v03");
        await FolderAsync("bcPlain", null, "Plain Folder", "01", "02");
        await ArchiveAsync("bcLoose", null, "Loose File");
        var shelf = await FolderAsync("bcShelf", null, "Shelf");
        await SeriesAsync("bcEta", shelf.Id, "Eta Done", complete, 2, 2, "complete", 0, false, "v01", "v02");
        await FolderAsync("bcShelfPlain", shelf.Id, "Shelf Plain", "01");
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

    private static Task<Page> BrowseAsync(HttpClient client, string query) =>
        client.GetAsync($"/api/v1/libraries/{LibPubId}/browse?sort=name&direction=asc&{query}").ContinueWith(t => OkAsync<Page>(t.Result)).Unwrap();

    /// <summary>The Completion tab's answer of every linked folder of the library (the oracle), by folder public id.</summary>
    private static async Task<Dictionary<string, SeriesAnswer>> TabAnswersAsync(HttpClient admin)
    {
        var tab = await OkAsync<OfficialReleasesPageDto>(await admin.GetAsync($"/api/v1/admin/metadata/official-releases?library={LibPubId}&filter=All&limit=200"));
        return tab.Items.ToDictionary(i => i.NodeId, i => i.Progress.Answer);
    }

    [Fact]
    public async Task Completion_KeepsExactlyTheSeriesTheCompletionTabGivesThatAnswer_AndNothingElse()
    {
        var admin = await AdminAsync();
        var tab = await TabAnswersAsync(admin);
        var root = await BrowseAsync(admin, "pageSize=100");
        Assert.Equal(9, root.TotalCount); // six series, Plain Folder, Shelf, Loose File
        var rootIds = root.Items.Select(i => i.Id).ToHashSet();

        var kept = new List<string>();
        foreach (var answer in Answers)
        {
            var page = await BrowseAsync(admin, $"pageSize=100&completion={answer}");
            var expected = tab.Where(t => t.Value.ToString() == answer && rootIds.Contains(t.Key)).Select(t => t.Key).Order().ToList();
            Assert.Equal(expected, page.Items.Select(i => i.Id).Order().ToList());
            Assert.Equal(expected.Count, page.TotalCount);
            Assert.All(page.Items, i => Assert.Equal("Folder", i.Kind));
            kept.AddRange(page.Items.Select(i => i.Id));
        }

        // Every answered root series once; the folder with tracking off, the plain folder, the category folder and the loose file never.
        Assert.Equal(["bcAlpha", "bcBeta", "bcDelta", "bcEpsila", "bcGamma"], kept.Order().ToList());
        Assert.Equal(["bcAlpha", "bcEpsila"], (await BrowseAsync(admin, "completion=HaveItAll")).Items.Select(i => i.Id).ToList());
        Assert.Contains("bcBeta", (await BrowseAsync(admin, "completion=FinishedMissing")).Items.Select(i => i.Id));
        Assert.Contains("bcGamma", (await BrowseAsync(admin, "completion=MissingSome")).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Completion_PagesAndCountsHonestly()
    {
        var admin = await AdminAsync();
        var first = await BrowseAsync(admin, "pageSize=1&completion=haveitall");
        Assert.Equal(2, first.TotalCount);
        Assert.True(first.HasMore);
        Assert.Equal("bcAlpha", Assert.Single(first.Items).Id);
        var second = await BrowseAsync(admin, $"pageSize=1&completion=HaveItAll&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(2, second.TotalCount);
        Assert.False(second.HasMore);
        Assert.Equal("bcEpsila", Assert.Single(second.Items).Id);

        // The level holds six linked series (the untracked one too); the counts are the five answered ones, as the Completion tab says.
        var tab = await TabAnswersAsync(admin);
        Assert.Equal(6, first.LinkedSeries);
        var counts = first.CompletionCounts!;
        Assert.Equal(5, counts.HaveItAll + counts.FinishedMissing + counts.UpToDate + counts.MissingSome + counts.CantTell);
        int Tab(SeriesAnswer a) => tab.Count(t => t.Value == a && t.Key != "bcEta");
        Assert.Equal((Tab(SeriesAnswer.HaveItAll), Tab(SeriesAnswer.FinishedMissing), Tab(SeriesAnswer.UpToDate), Tab(SeriesAnswer.MissingSome),
            Tab(SeriesAnswer.CantTell)), (counts.HaveItAll, counts.FinishedMissing, counts.UpToDate, counts.MissingSome, counts.CantTell));

        // No filter: the level still says how many linked series it holds, and carries no counts.
        var plain = await BrowseAsync(admin, "pageSize=100");
        Assert.Equal(6, plain.LinkedSeries);
        Assert.Null(plain.CompletionCounts);
    }

    [Fact]
    public async Task Completion_InsideACategoryFolder_AppliesThere()
    {
        var admin = await AdminAsync();
        var shelf = await BrowseAsync(admin, "parentId=bcShelf&pageSize=100");
        Assert.Equal(2, shelf.TotalCount);
        Assert.Equal(1, shelf.LinkedSeries);

        var done = await BrowseAsync(admin, "parentId=bcShelf&pageSize=100&completion=HaveItAll");
        Assert.Equal("bcEta", Assert.Single(done.Items).Id);
        Assert.Equal(1, done.TotalCount);
        Assert.Equal(1, done.CompletionCounts!.HaveItAll);
        Assert.Empty((await BrowseAsync(admin, "parentId=bcShelf&pageSize=100&completion=MissingSome")).Items);
    }

    [Fact]
    public async Task Completion_WhereTheLevelHoldsNoLinkedSeries_ChangesNothing()
    {
        var admin = await AdminAsync();
        // A plain folder of files: the filter does nothing there (it never empties a level without series).
        var plain = await BrowseAsync(admin, "parentId=bcPlain&pageSize=100&completion=HaveItAll");
        Assert.Equal(2, plain.TotalCount);
        Assert.Equal(["Plain Folder 01", "Plain Folder 02"], plain.Items.Select(i => i.DisplayName).ToList());
        Assert.Equal(0, plain.LinkedSeries);
        Assert.Null(plain.CompletionCounts);

        // Inside a series (its volumes, in whichever view it opens): unchanged by the filter.
        var series = await BrowseAsync(admin, "parentId=bcAlpha&pageSize=100");
        var filtered = await BrowseAsync(admin, "parentId=bcAlpha&pageSize=100&completion=CantTell");
        Assert.True(series.TotalCount > 0);
        Assert.Equal(series.Items.Select(i => i.Id), filtered.Items.Select(i => i.Id));
        Assert.Equal(series.TotalCount, filtered.TotalCount);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("1")]
    [InlineData("")]
    public async Task Completion_AnUnknownValue_IsIgnored(string value)
    {
        var admin = await AdminAsync();
        var page = await BrowseAsync(admin, $"pageSize=100&completion={Uri.EscapeDataString(value)}");
        Assert.Equal(9, page.TotalCount);
        Assert.Null(page.CompletionCounts);
    }

    [Fact]
    public async Task Completion_AReaderWithAccessToTheLibrary_GetsTheSameList()
    {
        var admin = await AdminAsync();
        var created = await OkAsync<CreateUserResponse>(await admin.PostAsJsonAsync("/api/v1/admin/users",
            new CreateUserRequest { Username = "bcreader", Password = "BcReaderPass123!", IsAdmin = false }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/v1/admin/users/{created.User.Id}/grants/{LibPubId}", null)).StatusCode);
        var reader = await LoginAndChangePasswordAsync("bcreader", "BcReaderPass123!", "BcReaderPass456!");

        var page = await BrowseAsync(reader, "pageSize=100&completion=HaveItAll");
        Assert.Equal(["bcAlpha", "bcEpsila"], page.Items.Select(i => i.Id).ToList());
    }

    private async Task<HttpClient> LoginAndChangePasswordAsync(string username, string oldPassword, string newPassword)
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = oldPassword })).EnsureSuccessStatusCode();
        var csrf = await client.GetFromJsonAsync<CsrfTokenDto>("/api/v1/auth/csrf");
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
        (await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest { CurrentPassword = oldPassword, NewPassword = newPassword })).EnsureSuccessStatusCode();
        var fresh = _factory.CreateClient();
        (await fresh.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = newPassword })).EnsureSuccessStatusCode();
        var freshCsrf = await fresh.GetFromJsonAsync<CsrfTokenDto>("/api/v1/auth/csrf");
        fresh.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", freshCsrf!.Token);
        return fresh;
    }
}
