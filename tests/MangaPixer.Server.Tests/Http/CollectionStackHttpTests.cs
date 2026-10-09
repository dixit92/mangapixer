namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Catalog;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for stacks of stories collected in one volume (1.37.0, "tankoubon stacks"): the folder's view
/// state, the stack card in browse, the stack view, the cover (the record's stored poster, else the first story's cover), a link change
/// regrouping at once through the running app's cache, and access control. Stored rows only, synthetic names, no provider.
/// </summary>
public sealed class CollectionStackHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "csLib1";
    private const string ArtistPubId = "csArtist";
    private const string LatePubId = "csLate";
    private readonly MangaPixerWebApplicationFactory _factory;

    public CollectionStackHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private string TankKey { get; set; } = string.Empty;

    // csArtist (no link of its own): csa (unlinked), csb + csd (linked to the collected volume), csc (another record, alone).
    // csLate: two stories, only the first linked (the second is linked by a test).
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
        {
            TankKey = await db.MetadataRecords.Where(r => r.Title == "Synthetic Collected Volume").Select(r => r.PublicId).SingleAsync();
            return;
        }
        var lib = await VolumeTestData.AddLibraryAsync(db, LibPubId);
        var artist = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Sample Artist", ArtistPubId);
        await VolumeTestData.AddArchiveAsync(db, lib.Id, artist.Id, "Sample Artist - Alpha Story", "csa");
        var b = await VolumeTestData.AddArchiveAsync(db, lib.Id, artist.Id, "Sample Artist - Beta Story", "csb");
        var c = await VolumeTestData.AddArchiveAsync(db, lib.Id, artist.Id, "Sample Artist - Gamma Story", "csc");
        var d = await VolumeTestData.AddArchiveAsync(db, lib.Id, artist.Id, "Sample Artist - Delta Story", "csd");
        var tank = await VolumeTestData.AddRecordAsync(db, "Synthetic Collected Volume", originVolumes: 1, status: MetadataOriginStatus.Complete);
        var other = await VolumeTestData.AddRecordAsync(db, "Synthetic One-shot");
        await VolumeTestData.LinkAsync(db, b, tank.Id, SeriesLinkState.Auto);
        await VolumeTestData.LinkAsync(db, d, tank.Id, SeriesLinkState.Confirmed);
        await VolumeTestData.LinkAsync(db, c, other.Id, SeriesLinkState.Auto);
        var late = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Late Artist", LatePubId);
        var one = await VolumeTestData.AddArchiveAsync(db, lib.Id, late.Id, "Late Story One", "csl1");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, late.Id, "Late Story Two", "csl2");
        await VolumeTestData.LinkAsync(db, one, other.Id, SeriesLinkState.Auto);
        TankKey = tank.PublicId;
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

    private static async Task<PageResponse<CatalogNodeDto>> BrowseAsync(HttpClient client, string folder, string query = "") =>
        await OkAsync<PageResponse<CatalogNodeDto>>(await client.GetAsync($"/api/v1/libraries/{LibPubId}/browse?parentId={folder}&{query}"));

    [Fact]
    public async Task VolumeView_IsAvailable_WithTheCollectionStackCount()
    {
        var admin = await AdminAsync();

        var view = await OkAsync<VolumeViewDto>(await admin.GetAsync($"/api/v1/nodes/{ArtistPubId}/volume-view"));

        Assert.Equal((true, true, 0, 1, false), (view.Available, view.Active, view.StackCount, view.CollectionStackCount, view.HasSeriesStatus));
        Assert.Equal(0, view.MissingVolumes);
    }

    [Fact]
    public async Task Browse_ShowsOneStackedCard_AndFoldersShowsEveryStory()
    {
        var admin = await AdminAsync();

        var page = await BrowseAsync(admin, ArtistPubId);
        Assert.Equal(3, page.TotalCount);
        // Owner (2026-10-08): a stack sorts by its record's title, among the stories.
        Assert.Equal(["Sample Artist - Alpha Story", "Sample Artist - Gamma Story", "Synthetic Collected Volume"], page.Items.Select(n => n.DisplayName));
        var stack = page.Items[2];
        Assert.Equal((CatalogNodeKind.VolumeStack, $"cs.{ArtistPubId}.{TankKey}", ArtistPubId), (stack.Kind, stack.Id, stack.ParentId));
        Assert.Null(stack.VolumeStack);
        Assert.Equal((TankKey, 2), (stack.CollectionStack!.Key, stack.CollectionStack.StoryCount));

        var flat = await BrowseAsync(admin, ArtistPubId, "group=flat");
        Assert.Equal(4, flat.TotalCount);
        Assert.All(flat.Items, n => Assert.Equal(CatalogNodeKind.Archive, n.Kind));
    }

    [Fact]
    public async Task StackView_ListsTheStoriesInFolderOrder()
    {
        var admin = await AdminAsync();

        var stack = await OkAsync<CollectionStackDto>(await admin.GetAsync($"/api/v1/nodes/{ArtistPubId}/collection-stacks/{TankKey}"));

        Assert.Equal((ArtistPubId, TankKey, "Synthetic Collected Volume", 2), (stack.FolderId, stack.Key, stack.Title, stack.StoryCount));
        Assert.Equal(["csb", "csd"], stack.Items.Select(i => i.Id));
        Assert.All(stack.Items, i => Assert.Equal(CatalogNodeKind.Archive, i.Kind));
        Assert.Equal((null, null), (stack.PreviousKey, stack.NextKey));
        Assert.NotNull(stack.CoverUrl);
    }

    [Fact]
    public async Task StackView_IsNotFound_ForAnUnknownKey_AVolumeKey_OrANodeThatIsNotItsFolder()
    {
        var admin = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/nodes/{ArtistPubId}/collection-stacks/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/nodes/{ArtistPubId}/collection-stacks/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/nodes/csb/collection-stacks/{TankKey}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/nodes/{LatePubId}/collection-stacks/{TankKey}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/nodes/no-such-node/collection-stacks/{TankKey}")).StatusCode);
        // The volume-stack route does not answer a story stack.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/nodes/{ArtistPubId}/volumes/{TankKey}")).StatusCode);
        using var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/nodes/{ArtistPubId}/collection-stacks/{TankKey}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/nodes/{ArtistPubId}/collection-stacks/{TankKey}/cover")).StatusCode);
    }

    [Fact]
    public async Task Cover_IsTheStoredPoster_WhileWebCoversAreShown_ElseTheFirstStorysCover()
    {
        var admin = await AdminAsync();

        // No stored poster yet: the card shows the first story's cover; the stack cover route redirects there.
        var before = (await BrowseAsync(admin, ArtistPubId)).Items.Single(n => n.Kind == CatalogNodeKind.VolumeStack);
        Assert.StartsWith("/api/v1/items/csb/cover", before.CoverUrl);
        using var noRedirect = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await noRedirect.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "admin", Password = "TestPassword123!" })).EnsureSuccessStatusCode();

        // A stored poster (what the record fetch saved): the card and the stack view use it, served from the data root.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var record = await db.MetadataRecords.SingleAsync(r => r.PublicId == TankKey);
            record.ImageState = 1;
            record.ImageVersion++;
            await db.SaveChangesAsync();
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
            await scope.ServiceProvider.GetRequiredService<MetadataImageStore>().PublishAsync(record.Id, record.ImageVersion, png);
            // The cover layer's automatic decision for a story linked on its own: its record's poster (what the cover pass decides).
            var storyB = await db.CatalogNodes.SingleAsync(n => n.PublicId == "csb");
            db.NodeAutoCovers.Add(new NodeAutoCoverEntity
            {
                NodeId = storyB.Id,
                Source = (int)AutoCoverSource.Poster,
                Reason = (int)AutoCoverReason.LocalNotCover,
                InputsKey = "seeded",
                Version = 1,
                DecidedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        try
        {
            var card = (await BrowseAsync(admin, ArtistPubId)).Items.Single(n => n.Kind == CatalogNodeKind.VolumeStack);
            Assert.StartsWith($"/api/v1/nodes/{ArtistPubId}/collection-stacks/{TankKey}/cover?v=", card.CoverUrl);
            Assert.Equal(CardCoverSource.Poster, card.CoverSource);
            var view = await OkAsync<CollectionStackDto>(await admin.GetAsync($"/api/v1/nodes/{ArtistPubId}/collection-stacks/{TankKey}"));
            Assert.Equal(card.CoverUrl, view.CoverUrl);
            // Folders: the story shows the poster the cover layer decided ...
            var flatB = (await BrowseAsync(admin, ArtistPubId, "group=flat")).Items.Single(i => i.Id == "csb");
            Assert.Equal(CardCoverSource.Poster, flatB.CoverSource);
            // ... but inside the stack each story shows its OWN page 1, not the record poster again (owner, 2026-10-08).
            Assert.All(view.Items, i =>
            {
                Assert.StartsWith($"/api/v1/items/{i.Id}/cover", i.CoverUrl);
                Assert.Equal(CardCoverSource.File, i.CoverSource);
            });
            var image = await admin.GetAsync(card.CoverUrl);
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
            Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);

            // The library hides saved web covers: the first story's cover again, and the cover route redirects to it.
            await SetWebCoversHiddenAsync(true);
            var hidden = (await BrowseAsync(admin, ArtistPubId)).Items.Single(n => n.Kind == CatalogNodeKind.VolumeStack);
            Assert.StartsWith("/api/v1/items/csb/cover", hidden.CoverUrl);
            var redirect = await noRedirect.GetAsync(card.CoverUrl);
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
            Assert.StartsWith("/api/v1/items/csb/cover", redirect.Headers.Location!.OriginalString);
        }
        finally
        {
            await SetWebCoversHiddenAsync(false);
        }
    }

    [Fact]
    public async Task LinkingASecondStory_StacksThemAtOnce()
    {
        var admin = await AdminAsync();

        var view = await OkAsync<VolumeViewDto>(await admin.GetAsync($"/api/v1/nodes/{LatePubId}/volume-view"));
        Assert.False(view.Available);
        Assert.Equal(2, (await BrowseAsync(admin, LatePubId)).TotalCount);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var two = await db.CatalogNodes.SingleAsync(n => n.PublicId == "csl2");
            var recordId = await db.NodeSeriesLinks.Where(l => l.Node!.PublicId == "csl1").Select(l => l.RecordId).SingleAsync();
            if (!await db.NodeSeriesLinks.AnyAsync(l => l.NodeId == two.Id))
                await VolumeTestData.LinkAsync(db, two, recordId, SeriesLinkState.Confirmed);
        }

        // Same running app, same five-minute cache: the link is part of the key, so the stack shows on the next read.
        view = await OkAsync<VolumeViewDto>(await admin.GetAsync($"/api/v1/nodes/{LatePubId}/volume-view"));
        Assert.Equal((true, 1), (view.Available, view.CollectionStackCount));
        var page = await BrowseAsync(admin, LatePubId);
        Assert.Equal("Synthetic One-shot", Assert.Single(page.Items).DisplayName);
    }

    [Fact]
    public async Task ReadersNeedAccessToTheLibrary()
    {
        var admin = await AdminAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = "csreader1", IsAdmin = false });
        created.EnsureSuccessStatusCode();
        var result = await created.Content.ReadFromJsonAsync<CreateUserResponse>(TestJson.Web);
        var token = Uri.UnescapeDataString(result!.ActivationUrl![(result.ActivationUrl!.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/activate", new ActivateAccountRequest { Token = token, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        var reader = _factory.CreateClient();
        (await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "csreader1", Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/nodes/{ArtistPubId}/collection-stacks/{TankKey}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/nodes/{ArtistPubId}/collection-stacks/{TankKey}/cover")).StatusCode);
    }

    private async Task SetWebCoversHiddenAsync(bool hidden)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = await db.Libraries.SingleAsync(l => l.PublicId == LibPubId);
        lib.WebCoversHidden = hidden;
        await db.SaveChangesAsync();
    }
}
