namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Tests.Server.Features.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for 1.34.0 lane V: the Volumes view of a webtoon named <c>&lt;index&gt; [&lt;chapter&gt; - &lt;title&gt;]</c>
/// whose MangaDex list is near-empty - the view state says "chapters only", no volume is missing, and browse lists the chapter files in
/// chapter order (paged by the opaque cursor). Stored rows only, synthetic names.
/// </summary>
public sealed class WebtoonVolumesHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "wtLib1";
    private const string SeriesPubId = "wtSeries";
    private readonly MangaPixerWebApplicationFactory _factory;

    public WebtoonVolumesHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // wtSeries (linked, a Korean webtoon, 12 English volumes): chapters 0, 0.5, 1-20 without 9; volumes "0" / "1" on MangaDex, the rest unassigned.
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;
        var lib = await VolumeTestData.AddLibraryAsync(db, LibPubId);
        var series = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "WT Series", SeriesPubId);
        await VolumeTestData.AddIndexedChaptersAsync(db, lib.Id, series.Id, 20, 9);
        var record = await VolumeTestData.AddRecordAsync(db, status: MetadataOriginStatus.Ongoing, englishVolumes: 12);
        record.Origin = (int)MetadataOrigin.Korea;
        record.Webtoon = true;
        await db.SaveChangesAsync();
        await VolumeTestData.LinkAsync(db, series, record.Id);
        await VolumeTestData.AddNearEmptyMapAsync(db, record.Id, 20);
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

    [Fact]
    public async Task VolumeView_OfAWebtoonWithANearEmptyList_IsTheChapterList_WithNoVolumeMissing()
    {
        var admin = await AdminAsync();

        var view = await OkAsync<VolumeViewDto>(await admin.GetAsync($"/api/v1/nodes/{SeriesPubId}/volume-view"));

        Assert.Equal((true, true, true, 0), (view.Available, view.Active, view.ChaptersOnly, view.StackCount));
        Assert.True(view.HasSeriesStatus);
        Assert.Equal(0, view.MissingVolumes);
        Assert.Equal(1, view.MissingChapters); // chapter 9
        Assert.Equal((0, 1), (view.Progress!.MissingVolumes, view.Progress.MissingChapters));
        Assert.Empty(view.Progress.Reach!.VolumeFiles);
    }

    [Fact]
    public async Task Browse_ListsTheChapterFilesInChapterOrder_PagedByTheCursor_WithoutPlaceholders()
    {
        var admin = await AdminAsync();

        var first = await OkAsync<PageResponse<CatalogNodeDto>>(
            await admin.GetAsync($"/api/v1/libraries/{LibPubId}/browse?parentId={SeriesPubId}&pageSize=3"));
        Assert.Equal(21, first.TotalCount); // 0, 0.5 and 1-20 without 9
        Assert.Equal(["0001 [0000].cbz", "0002 [0000.5].cbz", "0003 [0001 - Some Title].cbz"], first.Items.Select(n => n.DisplayName));
        Assert.All(first.Items, n => Assert.Equal(CatalogNodeKind.Archive, n.Kind));
        Assert.StartsWith("v:", first.NextCursor);

        var rest = await OkAsync<PageResponse<CatalogNodeDto>>(await admin.GetAsync(
            $"/api/v1/libraries/{LibPubId}/browse?parentId={SeriesPubId}&pageSize=50&cursor={Uri.EscapeDataString(first.NextCursor!)}"));
        Assert.Equal(18, rest.Items.Count);
        Assert.DoesNotContain(rest.Items, n => n.Kind == CatalogNodeKind.VolumeStack);
        Assert.Equal("0022 [0020 - Some Title].cbz", rest.Items[^1].DisplayName);
    }
}
