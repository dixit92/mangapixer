namespace com.lifepixer.mangapixer.Tests.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the web volume covers on virtual volume stacks (1.29.0, design 7.4): a chapter-only stack gets
/// its volume's stored web cover (preferred language, else origin), a stack with a real volume archive does not, and the
/// cover layer's read-time rules apply - "Volume covers from the web", the library's "Show saved web covers", Don't match,
/// no link. Synthetic rows and stored hashes only.
/// </summary>
public sealed class StackCoverServiceTests
{
    private static VolumeStack Stack(string key, bool hasVolumeArchive = false) => new(
        key, decimal.Parse(key, System.Globalization.CultureInfo.InvariantCulture), "Vol. " + key, VolumeStackConfidence.Exact,
        VolumeListSource.MangaDex, [], [], 2, 2, 0, hasVolumeArchive, "1", "2");

    private static async Task<(CoverLayerTestKit Kit, CatalogNodeEntity Folder, MetadataRecordEntity Record, MetadataRecordEntity Companion)> SeedAsync()
    {
        var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series");
        var record = await kit.Db.AddRecordAsync("401", "Series");
        await kit.Db.AddLinkAsync(folder, record);
        var companion = await kit.AddCompanionAsync(record);
        return (kit, folder, record, companion);
    }

    private static Task<IReadOnlyDictionary<string, StackCover>> ResolveAsync(CoverLayerTestKit kit, CatalogNodeEntity folder, params VolumeStack[] stacks) =>
        new StackCoverService(kit.Db.Db).ResolveAsync(folder.Id, folder.PublicId, folder.LibraryId, stacks, default);

    [Fact]
    public async Task ChapterOnlyStacks_GetTheirVolumesWebCover_PreferredLanguageFirst()
    {
        var (kit, folder, _, companion) = await SeedAsync();
        await using var _ = kit;
        await kit.AddStoredCoverAsync(companion, 1, "ja", 1UL);
        var en1 = await kit.AddStoredCoverAsync(companion, 1, "en", 2UL);
        var ja2 = await kit.AddStoredCoverAsync(companion, 2, "ja", 3UL);
        await kit.AddStoredCoverAsync(companion, 4, "en", 4UL);

        var covers = await ResolveAsync(kit, folder, Stack("1"), Stack("2"), Stack("3"), Stack("4", hasVolumeArchive: true));

        Assert.Equal(["1", "2"], covers.Keys.Order());
        Assert.Equal(en1.Id, covers["1"].VolumeCoverId);
        Assert.Equal(ja2.Id, covers["2"].VolumeCoverId); // no English cover yet: the origin language
        Assert.StartsWith($"/api/v1/nodes/{folder.PublicId}/volumes/1/cover?v=", covers["1"].Url, StringComparison.Ordinal);
        Assert.EndsWith("?v=" + covers["1"].Version, covers["1"].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUrlVersionChangesWithTheStoredCover()
    {
        var (kit, folder, _, companion) = await SeedAsync();
        await using var _ = kit;
        var cover = await kit.AddStoredCoverAsync(companion, 1, "en", 2UL);
        var before = (await ResolveAsync(kit, folder, Stack("1")))["1"].Url;

        var row = await kit.Db.Db.VolumeCovers.SingleAsync(v => v.Id == cover.Id);
        row.StoredVersion = 2;
        await kit.Db.Db.SaveChangesAsync();
        Assert.NotEqual(before, (await ResolveAsync(kit, folder, Stack("1")))["1"].Url);
    }

    [Fact]
    public async Task HiddenWhileTheWebCoverSwitchesAreOff()
    {
        var (kit, folder, _, companion) = await SeedAsync();
        await using var _ = kit;
        await kit.AddStoredCoverAsync(companion, 1, "en", 2UL);

        var library = await kit.Db.Db.Libraries.SingleAsync(l => l.Id == folder.LibraryId);
        library.WebCoversHidden = true;
        await kit.Db.Db.SaveChangesAsync();
        Assert.Empty(await ResolveAsync(kit, folder, Stack("1")));

        library.WebCoversHidden = false;
        var settings = await kit.SettingsAsync();
        settings.MetadataVolumeCoversEnabled = false;
        await kit.Db.Db.SaveChangesAsync();
        Assert.Empty(await ResolveAsync(kit, folder, Stack("1")));

        settings.MetadataVolumeCoversEnabled = true;
        await kit.Db.Db.SaveChangesAsync();
        Assert.Single(await ResolveAsync(kit, folder, Stack("1")));
    }

    [Fact]
    public async Task NothingUnderDontMatch_OrWithoutALinkedSeries()
    {
        var (kit, folder, _, companion) = await SeedAsync();
        await using var _ = kit;
        await kit.AddStoredCoverAsync(companion, 1, "en", 2UL);

        // A unit subfolder inherits the series link; under a Don't match subfolder nothing applies.
        var chapters = await kit.Db.AddFolderAsync(folder, "Chapters");
        Assert.Single(await ResolveAsync(kit, chapters, Stack("1")));
        await kit.Db.AddLinkAsync(chapters, null, SeriesLinkState.DontMatch);
        Assert.Empty(await ResolveAsync(kit, chapters, Stack("1")));

        var unlinked = await kit.Db.AddFolderAsync(null, "Other");
        Assert.Empty(await ResolveAsync(kit, unlinked, Stack("1")));
    }

    [Fact]
    public async Task OnlyStoredVolumeCoversOfTheVolumeItself_NoEditionAlternates_NoFractionalVolumes()
    {
        var (kit, folder, _, companion) = await SeedAsync();
        await using var _ = kit;
        var alternate = await kit.AddStoredCoverAsync(companion, 1, "en", 2UL);
        var row = await kit.Db.Db.VolumeCovers.SingleAsync(v => v.Id == alternate.Id);
        row.Variant = 1; // "1.1": an edition alternate, picker only
        var listed = await kit.AddStoredCoverAsync(companion, 2, "en", 3UL);
        var listedRow = await kit.Db.Db.VolumeCovers.SingleAsync(v => v.Id == listed.Id);
        listedRow.State = (int)VolumeCoverState.Listed; // known, not downloaded
        await kit.Db.Db.SaveChangesAsync();
        await kit.AddStoredCoverAsync(companion, 3, "en", 4UL);

        var covers = await ResolveAsync(kit, folder, Stack("1"), Stack("2"), Stack("2.5"));
        Assert.Empty(covers);
    }
}
