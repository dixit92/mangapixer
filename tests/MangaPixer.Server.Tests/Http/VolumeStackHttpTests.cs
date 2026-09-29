namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for the Volumes view (1.29.0): the wiring of the grouped browse branch, the folder's
/// view state, the stack view with its placeholders, the Volumes | Folders switch through the existing library-preferences
/// endpoint, and access control. Stored rows only, synthetic names.
/// </summary>
[Collection("HttpSerial")]
public sealed class VolumeStackHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "vsLib1";
    private const string SeriesPubId = "vsSeries";
    private const string PlainPubId = "vsPlain";
    private readonly MangaPixerWebApplicationFactory _factory;

    public VolumeStackHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // vsSeries (linked, ongoing): volume 1 lists chapters 1-10 (6 is not on disk; 7.5 is an extra), volume 2 lists 11-20 (11-13 on
    // disk). vsPlain: two unnumbered archives.
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;
        var lib = await VolumeTestData.AddLibraryAsync(db, LibPubId);
        var series = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "VS Series", SeriesPubId);
        foreach (var n in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 12, 13 })
            await VolumeTestData.AddArchiveAsync(db, lib.Id, series.Id, $"VS Series - Chapter {n:000}", $"vsc{n}");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, series.Id, "VS Series - c007.5", "vsc7x5");
        var record = await VolumeTestData.AddRecordAsync(db, status: MetadataOriginStatus.Ongoing);
        await VolumeTestData.LinkAsync(db, series, record.Id);
        await VolumeTestData.AddMapAsync(db, record.Id, 1, null, null, VolumeMapState.Ok, VolumeMapSource.MangaDexAggregate, (1, 1, 10), (2, 11, 20));
        var plain = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "VS Plain", PlainPubId);
        await VolumeTestData.AddArchiveAsync(db, lib.Id, plain.Id, "Alpha", "vspa");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, plain.Id, "Beta", "vspb");
    }

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

    private static Task<PageResponse<CatalogNodeDto>> BrowseAsync(HttpClient client, string query = "") =>
        client.GetAsync($"/api/v1/libraries/{LibPubId}/browse?parentId={SeriesPubId}&{query}").ContinueWith(t => OkAsync<PageResponse<CatalogNodeDto>>(t.Result)).Unwrap();

    private async Task SetSwitchAsync(HttpClient client, string? mode)
    {
        var body = mode is null
            ? new { viewMode = "grid", density = "comfortable", sort = "name", direction = "asc" } as object
            : new { viewMode = "grid", density = "comfortable", sort = "name", direction = "asc", seriesViewMode = mode };
        (await client.PutAsJsonAsync("/api/v1/reading/library-preferences", body)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task VolumeView_ReportsAvailabilityAndTheSwitchState()
    {
        var admin = await AdminAsync();

        var series = await OkAsync<VolumeViewDto>(await admin.GetAsync($"/api/v1/nodes/{SeriesPubId}/volume-view"));
        Assert.Equal((SeriesPubId, true, true, false, 2), (series.NodeId, series.Available, series.Active, series.Consolidated, series.StackCount));

        var plain = await OkAsync<VolumeViewDto>(await admin.GetAsync($"/api/v1/nodes/{PlainPubId}/volume-view"));
        Assert.Equal((false, false, 0), (plain.Available, plain.Active, plain.StackCount));

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/nodes/vsc1/volume-view")).StatusCode); // an archive
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/nodes/no-such-node/volume-view")).StatusCode);
        using var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/nodes/{SeriesPubId}/volume-view")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/nodes/{SeriesPubId}/volumes/1")).StatusCode);
    }

    [Fact]
    public async Task Browse_ReturnsStackEntries_PagedByTheOpaqueCursor_WithTheEntryCountAsTotal()
    {
        var admin = await AdminAsync();

        var first = await BrowseAsync(admin, "pageSize=1");
        Assert.Equal(2, first.TotalCount);
        var stack = Assert.Single(first.Items);
        Assert.Equal((CatalogNodeKind.VolumeStack, $"vs.{SeriesPubId}.1", "Vol. 1"), (stack.Kind, stack.Id, stack.DisplayName));
        Assert.Equal((10, 10, 1, 1, VolumeStackConfidence.Exact),
            (stack.VolumeStack!.PresentCount, stack.VolumeStack.ChapterCount, stack.VolumeStack.MissingCount, stack.VolumeStack.ExtraCount, stack.VolumeStack.Confidence));
        Assert.True(first.HasMore);
        Assert.StartsWith("v:", first.NextCursor);
        Assert.Equal("/api/v1/items/vsc1/cover", stack.CoverUrl);

        var second = await BrowseAsync(admin, $"pageSize=1&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal("Vol. 2", Assert.Single(second.Items).DisplayName);
        Assert.False(second.HasMore);
        Assert.True(second.HasPrevious);
        Assert.Equal(2, second.TotalCount);

        var back = await BrowseAsync(admin, $"pageSize=1&before={Uri.EscapeDataString(second.PrevCursor!)}");
        Assert.Empty(back.Items); // nothing precedes the first entry
        var before = await BrowseAsync(admin, $"pageSize=5&before={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(["Vol. 1"], before.Items.Select(n => n.DisplayName));
    }

    [Fact]
    public async Task Browse_GroupFlat_AndTheFoldersSwitch_ShowTheRealFolder()
    {
        var admin = await AdminAsync();
        try
        {
            var flat = await BrowseAsync(admin, "pageSize=100&group=flat");
            Assert.Equal(13, flat.TotalCount);
            Assert.All(flat.Items, n => Assert.Equal(CatalogNodeKind.Archive, n.Kind));

            await SetSwitchAsync(admin, "Folders");
            Assert.Equal(13, (await BrowseAsync(admin, "pageSize=100")).TotalCount);
            var view = await OkAsync<VolumeViewDto>(await admin.GetAsync($"/api/v1/nodes/{SeriesPubId}/volume-view"));
            Assert.True(view.Available);
            Assert.False(view.Active);
            Assert.Equal(2, (await BrowseAsync(admin, "pageSize=100&group=volumes")).TotalCount);

            await SetSwitchAsync(admin, "Volumes");
            Assert.Equal(2, (await BrowseAsync(admin, "pageSize=100")).TotalCount);
        }
        finally
        {
            await SetSwitchAsync(admin, null);
        }
    }

    [Fact]
    public async Task StackView_ListsChaptersInOrder_WithAPlaceholderWhereAChapterIsMissing()
    {
        var admin = await AdminAsync();

        var one = await OkAsync<VolumeStackDto>(await admin.GetAsync($"/api/v1/nodes/{SeriesPubId}/volumes/1"));

        Assert.Equal((SeriesPubId, "1", "Vol. 1", VolumeStackConfidence.Exact, VolumeListSource.MangaDex), (one.FolderId, one.Key, one.Label, one.Confidence, one.Source));
        Assert.Equal((10, 10, 1, 1), (one.PresentCount, one.ChapterCount, one.MissingCount, one.ExtraCount));
        Assert.Equal((null, "2"), (one.PreviousKey, one.NextKey));
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "7.5", "8", "9", "10"], one.Slots.Select(s => s.Chapter));
        var missing = Assert.Single(one.Slots, s => s.Kind == VolumeSlotKind.Missing);
        Assert.Equal("6", missing.Chapter);
        Assert.Null(missing.Item);
        Assert.All(one.Slots.Where(s => s.Kind == VolumeSlotKind.Item), s =>
        {
            Assert.Equal(CatalogNodeKind.Archive, s.Item!.Kind);
            Assert.Equal(SeriesPubId, s.Item.ParentId);
            Assert.StartsWith("/api/v1/items/", s.Item.CoverUrl);
        });
        Assert.Equal("/api/v1/items/vsc1/cover", one.CoverUrl);

        // Volume 2 is the last volume of an ongoing series: chapters 14-20 are not marked.
        var two = await OkAsync<VolumeStackDto>(await admin.GetAsync($"/api/v1/nodes/{SeriesPubId}/volumes/2"));
        Assert.Equal((3, 10, 0), (two.PresentCount, two.ChapterCount, two.MissingCount));
        Assert.Equal(("1", null), (two.PreviousKey, two.NextKey));
        Assert.DoesNotContain(two.Slots, s => s.Kind == VolumeSlotKind.Missing);
    }

    [Fact]
    public async Task StackView_IsNotFound_ForAnUnknownKeyOrAFolderThatDoesNotGroup()
    {
        var admin = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/nodes/{SeriesPubId}/volumes/9")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/nodes/{PlainPubId}/volumes/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/nodes/no-such-node/volumes/1")).StatusCode);
    }

    [Fact]
    public async Task ReadersNeedAccessToTheLibrary()
    {
        var admin = await AdminAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = "vsreader1", IsAdmin = false });
        created.EnsureSuccessStatusCode();
        var result = await created.Content.ReadFromJsonAsync<CreateUserResponse>(TestJson.Web);
        var token = Uri.UnescapeDataString(result!.ActivationUrl![(result.ActivationUrl!.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/activate", new ActivateAccountRequest { Token = token, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        var reader = _factory.CreateClient();
        (await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "vsreader1", Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();

        // No grant: the folder is invisible.
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/nodes/{SeriesPubId}/volume-view")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/nodes/{SeriesPubId}/volumes/1")).StatusCode);
        Assert.Empty((await OkAsync<PageResponse<CatalogNodeDto>>(await reader.GetAsync($"/api/v1/libraries/{LibPubId}/browse?parentId={SeriesPubId}"))).Items);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var userId = await db.Users.Where(u => u.NormalizedUserName == "VSREADER1").Select(u => u.Id).SingleAsync();
            var libId = await db.Libraries.Where(l => l.PublicId == LibPubId).Select(l => l.Id).SingleAsync();
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = userId, LibraryId = libId, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.True((await OkAsync<VolumeViewDto>(await reader.GetAsync($"/api/v1/nodes/{SeriesPubId}/volume-view"))).Available);
        Assert.Equal(10, (await OkAsync<VolumeStackDto>(await reader.GetAsync($"/api/v1/nodes/{SeriesPubId}/volumes/1"))).PresentCount);
        Assert.Equal(2, (await OkAsync<PageResponse<CatalogNodeDto>>(await reader.GetAsync($"/api/v1/libraries/{LibPubId}/browse?parentId={SeriesPubId}"))).TotalCount);
    }
}
