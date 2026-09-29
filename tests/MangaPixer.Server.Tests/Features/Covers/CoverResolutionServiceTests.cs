namespace com.lifepixer.mangapixer.Tests.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the cover layer at read time (1.29.0, design 2.9): admin choice > "use the file's cover" pin >
/// automatic decision (while its layer is allowed) > file default; a folder shows its cover archive's RESOLVED cover; the
/// "Show saved web covers" library switch and the crop switch are read at request time; the URL token changes with every
/// layer change.
/// </summary>
public sealed class CoverResolutionServiceTests
{
    private static async Task<(CoverLayerTestKit Kit, CatalogNodeEntity Folder, CatalogNodeEntity V1, CatalogNodeEntity V2)> SeedAsync()
    {
        var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series");
        var v1 = await kit.AddBookAsync(folder, "Series v01", 1400, 1000, 1UL);
        var v2 = await kit.AddBookAsync(folder, "Series v02", 700, 1000, 2UL);
        return (kit, folder, v1, v2);
    }

    private static CoverTarget Target(CatalogNodeEntity n) => new(n.Id, n.PublicId, n.Kind == 0);

    private static async Task<CoverResolution> ResolveAsync(CoverLayerTestKit kit, CatalogNodeEntity node) =>
        (await kit.Resolutions().ResolveAsync([Target(node)], default))[node.Id];

    [Fact]
    public async Task NoLayer_TheFileCover_VersionedByContentVersion()
    {
        var (kit, folder, v1, _) = await SeedAsync();
        await using var _ = kit;
        var archive = await ResolveAsync(kit, v1);
        Assert.Equal($"/api/v1/items/{v1.PublicId}/cover?v=1", archive.Url);
        Assert.Equal(CardCoverSource.File, archive.Source);
        Assert.False(archive.Layered);
        // A folder shows its first archive by SortKey.
        Assert.Equal($"/api/v1/items/{v1.PublicId}/cover?v=1", (await ResolveAsync(kit, folder)).Url);
    }

    [Fact]
    public async Task AutomaticCrop_ReachesTheArchiveAndItsSeriesCard_AndTheCropSwitchHidesIt()
    {
        var (kit, folder, v1, _) = await SeedAsync();
        await using var _ = kit;
        await kit.Decisions().DecideAsync(v1.Id, default);

        var archive = await ResolveAsync(kit, v1);
        Assert.True(archive.Layered);
        Assert.Equal(CardCoverSource.Crop, archive.Source);
        Assert.StartsWith($"/api/v1/nodes/{v1.PublicId}/cover?v=", archive.Url, StringComparison.Ordinal);
        var series = await ResolveAsync(kit, folder);
        Assert.Equal(archive.Url, series.Url); // the folder names its cover archive's layered URL
        Assert.Equal(folder.Id, series.NodeId);

        var settings = await kit.SettingsAsync();
        settings.CoverSpreadCropEnabled = false;
        await kit.Db.Db.SaveChangesAsync();
        Assert.Equal(CardCoverSource.File, (await ResolveAsync(kit, v1)).Source);
    }

    [Fact]
    public async Task Choice_BeatsPin_BeatsAutomatic_AndEveryChangeGivesANewUrl()
    {
        var (kit, folder, v1, v2) = await SeedAsync();
        await using var _ = kit;
        await kit.Decisions().DecideAsync(v1.Id, default);
        var automatic = (await ResolveAsync(kit, folder)).Url;

        // Pin: the folder's FILE cover (its cover archive's page 1, not that archive's crop).
        var choice = new NodeCoverChoiceEntity { NodeId = folder.Id, Mode = (int)CoverChoiceMode.FilePinned, Version = 1, SetAt = DateTimeOffset.UtcNow };
        kit.Db.Db.NodeCoverChoices.Add(choice);
        await kit.Db.Db.SaveChangesAsync();
        var pinned = await ResolveAsync(kit, folder);
        Assert.Equal($"/api/v1/items/{v1.PublicId}/cover?v=1", pinned.Url);
        Assert.Equal(CardCoverSource.File, pinned.Source);

        // Another archive's cover.
        choice.Mode = (int)CoverChoiceMode.Archive;
        choice.ArchiveNodeId = v2.Id;
        choice.Version = 2;
        await kit.Db.Db.SaveChangesAsync();
        var chosen = await ResolveAsync(kit, folder);
        Assert.Equal(CardCoverSource.Chosen, chosen.Source);
        Assert.Equal(v2.Id, chosen.ArchiveNodeId);
        Assert.StartsWith($"/api/v1/nodes/{folder.PublicId}/cover?v=", chosen.Url, StringComparison.Ordinal);

        // The other half of v01 on the folder.
        choice.Mode = (int)CoverChoiceMode.Crop;
        choice.ArchiveNodeId = null;
        choice.CropSide = (int)CoverCropSide.Left;
        choice.Version = 3;
        await kit.Db.Db.SaveChangesAsync();
        var crop = await ResolveAsync(kit, folder);
        Assert.Equal(CoverImageKind.Crop, crop.Image);
        Assert.Equal(CoverCropSide.Left, crop.CropSide);
        Assert.Equal(v1.Id, crop.ArchiveNodeId);

        Assert.Equal(4, new[] { automatic, pinned.Url, chosen.Url, crop.Url }.Distinct().Count());

        // The chosen archive is deleted -> back to automatic.
        kit.Db.Db.NodeCoverChoices.Remove(choice);
        await kit.Db.Db.SaveChangesAsync();
        Assert.Equal(automatic, (await ResolveAsync(kit, folder)).Url);
    }

    [Fact]
    public async Task WebCovers_ShowOnlyWhileTheLibraryShowsSavedWebCovers_AndVolumeCoversAreOn()
    {
        var (kit, folder, _, v2) = await SeedAsync();
        await using var _ = kit;
        var record = await kit.Db.AddRecordAsync("201", "Series");
        await kit.Db.AddLinkAsync(folder, record);
        var companion = await kit.AddCompanionAsync(record);
        var web = await kit.AddStoredCoverAsync(companion, 2, "en", ulong.MaxValue);
        await kit.Decisions().DecideAsync(v2.Id, default);

        var shown = await ResolveAsync(kit, v2);
        Assert.Equal(CardCoverSource.WebVolume, shown.Source);
        Assert.Equal(web.PublicId, shown.VolumeCoverPublicId);

        var library = await kit.Db.Db.Libraries.SingleAsync(l => l.Id == kit.Db.LibraryId);
        library.WebCoversHidden = true;
        await kit.Db.Db.SaveChangesAsync();
        Assert.Equal(CardCoverSource.File, (await ResolveAsync(kit, v2)).Source);

        library.WebCoversHidden = false;
        // "Show series information" off does NOT hide saved web covers (owner Q9: two separate switches).
        library.MetadataSeriesInfoHidden = true;
        var settings = await kit.SettingsAsync();
        await kit.Db.Db.SaveChangesAsync();
        Assert.Equal(CardCoverSource.WebVolume, (await ResolveAsync(kit, v2)).Source);

        settings.MetadataVolumeCoversEnabled = false;
        await kit.Db.Db.SaveChangesAsync();
        Assert.Equal(CardCoverSource.File, (await ResolveAsync(kit, v2)).Source);
    }

    [Fact]
    public void Token_IsShortOpaque_AndChangesWithEveryPart()
    {
        var a = CoverResolutionService.Token("a", 1, "crop", 5L, 2L, 1);
        Assert.Equal(a, CoverResolutionService.Token("a", 1, "crop", 5L, 2L, 1));
        Assert.NotEqual(a, CoverResolutionService.Token("a", 2, "crop", 5L, 2L, 1));
        Assert.NotEqual(a, CoverResolutionService.Token("a", 1, "crop", 5L, 3L, 1));
        Assert.NotEqual(a, CoverResolutionService.Token("c", 1, "crop", 5L, 2L, 1));
        Assert.NotEqual(a, CoverResolutionService.Token("a", 1, "crop", 5L, 2L, 2));
        Assert.InRange(a.Length, 1, 12);
        Assert.Matches("^[0-9a-z]+$", a);
    }
}
