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
/// HTTP tests for the 1.29.0 settings of volume covers and virtual volumes: the global switches and the preferred cover
/// language (Metadata Manager settings), the two SEPARATE library toggles ("Show series information" and "Show saved web
/// covers") plus the library Volumes-view override, the per-user series view (library preferences) and the per-folder
/// view override. Pure settings: nothing here sends a request anywhere.
/// </summary>
[Collection("HttpSerial")]
public sealed class VolumeSettingsHttpTests
{
    private const string LibPub = "vvlib1";

    private static async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPub))
            return;
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Volumes Lib", RootPath = "/synthetic/vv", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var folder = Node("vvSeries", lib.Id, null, 0, "Series");
        db.CatalogNodes.Add(folder);
        await db.SaveChangesAsync();
        db.CatalogNodes.Add(Node("vvArc", lib.Id, folder.Id, 1, "Series c001"));
        await db.SaveChangesAsync();
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

    [Fact]
    public async Task GlobalSettings_DefaultOn_English_AndChangeWithoutConsent_ButRejectABadLanguage()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var initial = await OkAsync<MetadataSettingsDto>(await admin.GetAsync("/api/v1/admin/metadata/settings"));
        Assert.Equal(("en", true, true, true, false),
            (initial.PreferredCoverLanguage, initial.VolumeCoversEnabled, initial.SpreadCropEnabled, initial.VirtualVolumesEnabled,
                initial.VolumeCoversDisabledByConfig));

        var changed = await OkAsync<MetadataSettingsDto>(await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings",
            new UpdateMetadataSettingsRequest
            {
                PreferredCoverLanguage = "pt-br",
                VolumeCoversEnabled = false,
                SpreadCropEnabled = false,
                VirtualVolumesEnabled = false,
            }, TestJson.Web));
        Assert.Equal(("pt-br", false, false, false),
            (changed.PreferredCoverLanguage, changed.VolumeCoversEnabled, changed.SpreadCropEnabled, changed.VirtualVolumesEnabled));
        Assert.False(changed.FetchEnabled); // no consent was needed, and none was given

        foreach (var bad in new[] { "EN", "english", "e", "en_US", "en-", "zh-hant-tw" })
        {
            var response = await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings",
                new UpdateMetadataSettingsRequest { PreferredCoverLanguage = bad }, TestJson.Web);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_cover_language", (await response.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        }
        var after = await OkAsync<MetadataSettingsDto>(await admin.GetAsync("/api/v1/admin/metadata/settings"));
        Assert.Equal("pt-br", after.PreferredCoverLanguage);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task LibraryToggles_WebCoversAreSeparateFromSeriesInformation_AndTheVolumesOverrideResets()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        MetadataLibrarySettingsDto Lib(MetadataSettingsDto s) => s.Libraries.Single(l => l.LibraryId == LibPub);
        var initial = Lib(await OkAsync<MetadataSettingsDto>(await admin.GetAsync("/api/v1/admin/metadata/settings")));
        Assert.Equal((true, true, (ViewSwitch?)null), (initial.ShowSeriesInfo, initial.ShowWebCovers, initial.VirtualVolumes));

        var hidden = Lib(await OkAsync<MetadataSettingsDto>(await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}",
            new UpdateMetadataLibraryRequest { ShowWebCovers = false, VirtualVolumes = ViewSwitch.Off }, TestJson.Web)));
        Assert.Equal((true, false, (ViewSwitch?)ViewSwitch.Off), (hidden.ShowSeriesInfo, hidden.ShowWebCovers, hidden.VirtualVolumes));

        var infoOff = Lib(await OkAsync<MetadataSettingsDto>(await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}",
            new UpdateMetadataLibraryRequest { ShowSeriesInfo = false, ShowWebCovers = true }, TestJson.Web)));
        Assert.Equal((false, true, (ViewSwitch?)ViewSwitch.Off), (infoOff.ShowSeriesInfo, infoOff.ShowWebCovers, infoOff.VirtualVolumes));

        var reset = Lib(await OkAsync<MetadataSettingsDto>(await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}",
            new UpdateMetadataLibraryRequest { ResetVirtualVolumes = true }, TestJson.Web)));
        Assert.Null(reset.VirtualVolumes);

        using var scope = factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().Libraries.AsNoTracking().SingleAsync(l => l.PublicId == LibPub);
        Assert.Equal((true, false, (int?)null), (row.MetadataSeriesInfoHidden, row.WebCoversHidden, row.VirtualVolumes));
    }

    [Fact]
    public async Task SeriesView_IsRememberedPerUser_AndAnOlderClientsPutFallsBackToTheDefault()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var initial = await OkAsync<LibraryViewPreferencesDto>(await admin.GetAsync("/api/v1/reading/library-preferences"));
        Assert.Null(initial.SeriesViewMode);

        (await admin.PutAsJsonAsync("/api/v1/reading/library-preferences", initial with { SeriesViewMode = SeriesViewMode.Folders }, TestJson.Web))
            .EnsureSuccessStatusCode();
        Assert.Equal(SeriesViewMode.Folders,
            (await OkAsync<LibraryViewPreferencesDto>(await admin.GetAsync("/api/v1/reading/library-preferences"))).SeriesViewMode);

        // A client that predates the field sends none: back to the default.
        (await admin.PutAsJsonAsync("/api/v1/reading/library-preferences", initial with { SeriesViewMode = null }, TestJson.Web))
            .EnsureSuccessStatusCode();
        Assert.Null((await OkAsync<LibraryViewPreferencesDto>(await admin.GetAsync("/api/v1/reading/library-preferences"))).SeriesViewMode);
    }

    [Fact]
    public async Task FolderViewSettings_AdminOnly_FoldersOnly_AndNullInheritsAgain()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        const string Url = "/api/v1/admin/folders/vvSeries/view-settings";

        Assert.Null((await OkAsync<FolderViewSettingsDto>(await admin.GetAsync(Url))).VirtualVolumes);
        var on = await OkAsync<FolderViewSettingsDto>(await admin.PutAsJsonAsync(Url,
            new UpdateFolderViewSettingsRequest { VirtualVolumes = ViewSwitch.On }, TestJson.Web));
        Assert.Equal(("vvSeries", (ViewSwitch?)ViewSwitch.On), (on.NodeId, on.VirtualVolumes));
        Assert.Equal(ViewSwitch.On, (await OkAsync<FolderViewSettingsDto>(await admin.GetAsync(Url))).VirtualVolumes);

        var inherit = await OkAsync<FolderViewSettingsDto>(await admin.PutAsJsonAsync(Url, new UpdateFolderViewSettingsRequest(), TestJson.Web));
        Assert.Null(inherit.VirtualVolumes);
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().FolderViewSettings.CountAsync());

        var archive = await admin.PutAsJsonAsync("/api/v1/admin/folders/vvArc/view-settings",
            new UpdateFolderViewSettingsRequest { VirtualVolumes = ViewSwitch.On }, TestJson.Web);
        Assert.Equal(HttpStatusCode.BadRequest, archive.StatusCode);
        Assert.Equal("not_a_folder", (await archive.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/folders/vvNope/view-settings")).StatusCode);

        var reader = await factory.CreateReaderClientAsync("vvreader", LibPub);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(Url,
            new UpdateFolderViewSettingsRequest { VirtualVolumes = ViewSwitch.Off }, TestJson.Web)).StatusCode);
    }
}
