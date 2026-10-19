namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The tag index through the app's public surface (1.45.0 lane F): the background builder indexes a series after an admin links it (the
/// link write's nudge, the debounced pass - wiring, owner decision 6), and the admin creator API (<c>/api/v1/admin/metadata/creators</c>):
/// admin only, CSRF-checked, merge / split audited with public ids only, a merged id answering with its target and <c>redirectedFrom</c>.
/// Written against the stored tables and plain JSON so the same file proves red on the seam's no-op nudge. Synthetic names only.
/// </summary>
public sealed class FacetIndexHttpTests
{
    private static readonly Dictionary<string, string?> BuilderOn = new()
    {
        ["Facets:Index:Enabled"] = "true",
        ["Facets:Index:DebounceSeconds"] = "0",
        ["Facets:Index:StartDelaySeconds"] = "3600",
        ["Facets:Index:SweepSeconds"] = "3600",
    };

    private static async Task<T> WithDbAsync<T>(ApiTokenTestFactory factory, Func<MangaPixerDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>());
    }

    private static async Task<(long LibraryId, long FolderId, long RecordId)> SeedSeriesAsync(ApiTokenTestFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var library = new LibraryEntity { PublicId = "fxlib1", DisplayName = "Facets", RootPath = "/synthetic/facets-http", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var folder = new CatalogNodeEntity
        {
            PublicId = "fxFolder", LibraryId = library.Id, Kind = (int)CatalogNodeKind.Folder, DisplayName = "Series Alpha",
            RelativePath = "Series Alpha", PathKey = "series alpha", SortKey = "0series alpha",
            Availability = (int)CatalogNodeAvailability.Available, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(folder);
        await db.SaveChangesAsync();
        foreach (var i in new[] { 1, 2 })
        {
            var archive = new CatalogNodeEntity
            {
                PublicId = "fxArc" + i, LibraryId = library.Id, ParentId = folder.Id, Kind = (int)CatalogNodeKind.Archive,
                DisplayName = $"Series Alpha v0{i}.cbz", RelativePath = $"Series Alpha/v0{i}.cbz", PathKey = $"series alpha/v0{i}.cbz",
                SortKey = $"1series alpha v0{i}.cbz", Availability = (int)CatalogNodeAvailability.Available, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(archive);
        }
        var record = new MetadataRecordEntity
        {
            PublicId = "fxrec1", Provider = "mangaupdates", ExternalId = "777001", Title = "Series Alpha",
            CreatorsJson = "[{\"name\":\"Tanaka Hiroshi\",\"role\":\"author\",\"providerId\":\"7\"}]",
            GenresJson = "[\"Action\"]", StartYear = 2001, Origin = 0, FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        return (library.Id, folder.Id, record.Id);
    }

    [Fact]
    public async Task AnAdminLink_IsIndexedInTheBackground_WorkCreatorAndValues()
    {
        using var factory = new ApiTokenTestFactory(BuilderOn);
        var admin = await factory.AdminAsync();
        var (libraryId, folderId, recordId) = await SeedSeriesAsync(factory);

        var link = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/fxFolder/link", new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = "777001" });
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(45);
        var indexed = false;
        while (!indexed && DateTime.UtcNow < deadline)
        {
            indexed = await WithDbAsync(factory, db => db.FacetIndexLibraries.AnyAsync(l => l.LibraryId == libraryId));
            if (!indexed)
                await Task.Delay(200);
        }
        Assert.True(indexed, "the builder never indexed the library after the link's nudge");

        var work = await WithDbAsync(factory, db => db.FacetWorks.AsNoTracking().SingleAsync());
        Assert.Equal(("rec:" + recordId, recordId), (work.WorkKey, work.RecordId!.Value));
        var keys = await WithDbAsync(factory, db => (from e in db.FacetEntries
                                                     join v in db.FacetValues on e.ValueId equals v.Id
                                                     where e.LocationNodeId == folderId && e.LibraryId == libraryId
                                                     select v.ValueKey).ToListAsync());
        Assert.Contains("g:action", keys);
        Assert.Contains("y:2001", keys);
        Assert.Contains("t:manga", keys);
        var creator = await WithDbAsync(factory, db => db.Creators.AsNoTracking().SingleAsync());
        Assert.Contains("c:" + creator.Id, keys);
        Assert.Equal("Tanaka Hiroshi", creator.DisplayName);
    }

    private static async Task<(string A, string B)> SeedCreatorsAsync(ApiTokenTestFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var now = DateTimeOffset.UtcNow;
        var a = new CreatorEntity { PublicId = "cr_aaa1", DisplayName = "Kenta Mori", DisplayNameSource = 1, CreatedAt = now, UpdatedAt = now };
        var b = new CreatorEntity { PublicId = "cr_bbb2", DisplayName = "Tanaka Hiroshi", DisplayNameSource = 1, CreatedAt = now, UpdatedAt = now };
        db.Creators.AddRange(a, b);
        await db.SaveChangesAsync();
        db.CreatorNames.AddRange(
            new CreatorNameEntity { CreatorId = a.Id, Name = "Kenta Mori", NameKey = "kenta mori", Source = 2 },
            new CreatorNameEntity { CreatorId = b.Id, Name = "Tanaka Hiroshi", NameKey = "hiroshi tanaka", Source = 0 },
            new CreatorNameEntity { CreatorId = b.Id, Name = "Pen Alias", NameKey = "alias pen", Source = 2 });
        db.CreatorProviderIds.AddRange(
            new CreatorProviderIdEntity { CreatorId = b.Id, Provider = "mangaupdates", ExternalId = "7" },
            new CreatorProviderIdEntity { CreatorId = b.Id, Provider = "mangaupdates", ExternalId = "8" });
        await db.SaveChangesAsync();
        return (a.PublicId, b.PublicId);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestJson.Web));

    [Fact]
    public async Task TheCreatorRoutes_AreAdminOnly_AndRefuseAPostWithoutTheCsrfHeader()
    {
        using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        var (a, b) = await SeedCreatorsAsync(factory);
        await factory.CreateUserAsync("reader1", "ReaderPassword123!", isAdmin: false);
        var reader = await factory.LoginAsync("reader1", "ReaderPassword123!");
        var anon = factory.CreateClient();

        foreach (var client in new[] { reader, anon })
        {
            var expected = client == anon ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            Assert.Equal(expected, (await client.GetAsync("/api/v1/admin/metadata/creators?q=tanaka")).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/v1/admin/metadata/creators/{a}")).StatusCode);
            Assert.Equal(expected, (await client.PostAsJsonAsync($"/api/v1/admin/metadata/creators/{a}/merge", new { intoId = b })).StatusCode);
            Assert.Equal(expected, (await client.PostAsJsonAsync($"/api/v1/admin/metadata/creators/{b}/split", new { names = new[] { "Pen Alias" } })).StatusCode);
            Assert.Equal(expected, (await client.PutAsJsonAsync($"/api/v1/admin/metadata/creators/{a}/display-name", new { displayName = "X" })).StatusCode);
        }

        var noCsrf = factory.CreateClient();
        (await noCsrf.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "admin", Password = ApiTokenTestFactory.AdminPassword })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await noCsrf.PostAsJsonAsync($"/api/v1/admin/metadata/creators/{a}/merge", new { intoId = b })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await noCsrf.PutAsJsonAsync($"/api/v1/admin/metadata/creators/{a}/display-name", new { displayName = "X" })).StatusCode);
        Assert.Null(await WithDbAsync(factory, db => db.Creators.Where(c => c.PublicId == a).Select(c => c.MergedIntoId).SingleAsync()));

        var found = await JsonAsync(await admin.GetAsync("/api/v1/admin/metadata/creators?q=hiroshi"));
        Assert.Equal(b, Assert.Single(found.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/metadata/creators/cr_nothere")).StatusCode);
    }

    [Fact]
    public async Task AMerge_IsAuditedWithIdsOnly_AndTheMergedIdAnswersWithItsTarget()
    {
        using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        var (a, b) = await SeedCreatorsAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/admin/metadata/creators/{a}/merge", new { intoId = a })).StatusCode);
        var merge = await admin.PostAsJsonAsync($"/api/v1/admin/metadata/creators/{a}/merge", new { intoId = b });
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);
        var merged = await JsonAsync(merge);
        Assert.Equal(b, merged.GetProperty("id").GetString());
        Assert.Contains(a, merged.GetProperty("mergedFrom").EnumerateArray().Select(x => x.GetString()));

        var redirected = await admin.GetAsync($"/api/v1/admin/metadata/creators/{a}");
        Assert.Equal(HttpStatusCode.OK, redirected.StatusCode);
        var body = await JsonAsync(redirected);
        Assert.Equal((b, a), (body.GetProperty("id").GetString(), body.GetProperty("redirectedFrom").GetString()));
        Assert.Equal(new[] { "mangaupdates:7", "mangaupdates:8" }, body.GetProperty("providerIds").EnumerateArray().Select(x => x.GetString()).ToArray());

        var audit = await WithDbAsync(factory, db => db.AuditEvents.AsNoTracking().Where(e => e.Action == AuditActions.MetadataCreatorMerge).ToListAsync());
        var row = Assert.Single(audit);
        Assert.Equal((AuditResults.Success, a + ">" + b), (row.Result, row.CorrelationId));
        Assert.NotNull(row.ActorUserId);
        Assert.True(await WithDbAsync(factory, db => db.CreatorOverrides.AnyAsync(o => o.Kind == 0 && o.Subject == a)));
    }

    [Fact]
    public async Task ASplit_MovesTheChosenIdsAndSpellings_PinsThem_AndIsAudited()
    {
        using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        var (_, b) = await SeedCreatorsAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/admin/metadata/creators/{b}/split", new { names = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/admin/metadata/creators/{b}/split",
            new { providerIds = new[] { "mangaupdates:7", "mangaupdates:8" }, names = new[] { "Tanaka Hiroshi", "Pen Alias" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/admin/metadata/creators/{b}/split", new { providerIds = new[] { "mangaupdates:9" } })).StatusCode);

        var split = await admin.PostAsJsonAsync($"/api/v1/admin/metadata/creators/{b}/split", new { providerIds = new[] { "mangaupdates:8" }, names = new[] { "Pen Alias" } });
        Assert.Equal(HttpStatusCode.OK, split.StatusCode);
        var created = await JsonAsync(split);
        var newId = created.GetProperty("id").GetString()!;
        Assert.NotEqual(b, newId);
        Assert.Equal(new[] { "mangaupdates:8" }, created.GetProperty("providerIds").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(new[] { "Pen Alias" }, created.GetProperty("names").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray());

        var rest = await JsonAsync(await admin.GetAsync($"/api/v1/admin/metadata/creators/{b}"));
        Assert.Equal(new[] { "mangaupdates:7" }, rest.GetProperty("providerIds").EnumerateArray().Select(x => x.GetString()).ToArray());
        var pins = await WithDbAsync(factory, db => db.CreatorOverrides.AsNoTracking().Where(o => o.Kind == 1).Select(o => o.Subject).ToListAsync());
        Assert.Equal(new[] { "alias pen", "mangaupdates:8" }, pins.Order(StringComparer.Ordinal).ToArray());
        var audit = Assert.Single(await WithDbAsync(factory, db => db.AuditEvents.AsNoTracking().Where(e => e.Action == AuditActions.MetadataCreatorSplit).ToListAsync()));
        Assert.Equal(b + ">" + newId, audit.CorrelationId);
    }

    [Fact]
    public async Task TheDisplayName_IsSetAndCleared()
    {
        using var factory = new ApiTokenTestFactory();
        var admin = await factory.AdminAsync();
        var (a, _) = await SeedCreatorsAsync(factory);

        var set = await JsonAsync(await admin.PutAsJsonAsync($"/api/v1/admin/metadata/creators/{a}/display-name", new { displayName = "  Chosen  Name " }));
        Assert.Equal(("Chosen Name", "admin"), (set.GetProperty("displayName").GetString(), set.GetProperty("displayNameSource").GetString()));
        var cleared = await admin.PutAsJsonAsync($"/api/v1/admin/metadata/creators/{a}/display-name", new { displayName = (string?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.False(await WithDbAsync(factory, db => db.CreatorOverrides.AnyAsync(o => o.Kind == 2)));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/creators/cr_nothere/display-name", new { displayName = "X" })).StatusCode);
    }
}
