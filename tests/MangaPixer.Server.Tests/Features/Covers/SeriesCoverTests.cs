namespace com.lifepixer.mangapixer.Tests.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the SERIES cover (1.30.0, owner soak test on 1.29.1): a linked series folder with volume files at
/// the top and a <c>Chapters/</c> subfolder showed chapter 1's page 1 (the first file by name) although it compared volume 1.
/// Now the folder names its local volume 1 and shows that archive's RESOLVED cover - its crop, web cover or file, exactly as
/// volume 1's own card - and Home's Continue reading shows that series cover on its chapter cards. Stored hashes only.
/// </summary>
public sealed class SeriesCoverTests
{
    private const ulong Local = 0x0F0F_3C3C_5A5A_A5A5UL;

    private static ulong Away(int bits) => Local ^ ((1UL << bits) - 1);

    private sealed record Mixed(CatalogNodeEntity Series, CatalogNodeEntity Chapters, CatalogNodeEntity V1, CatalogNodeEntity V2,
        CatalogNodeEntity C1, CatalogNodeEntity C5, MetadataRecordEntity Record);

    /// <summary>The owner's layout: <c>Series/Series v01, v02</c> + <c>Series/Chapters/Series c001, c005</c> ("c" sorts before "v").</summary>
    private static async Task<Mixed> SeedMixedAsync(CoverLayerTestKit kit, string externalId, bool linked = true, int v1Width = 700)
    {
        var series = await kit.Db.AddFolderAsync(null, "Series " + externalId);
        var v1 = await kit.AddBookAsync(series, "Series v01", v1Width, 1000, Local);
        var v2 = await kit.AddBookAsync(series, "Series v02", 700, 1000, Away(30));
        var chapters = await kit.Db.AddFolderAsync(series, "Chapters");
        var c1 = await kit.AddBookAsync(chapters, "Series c001", 700, 1000, Away(40));
        var c5 = await kit.AddBookAsync(chapters, "Series c005", 700, 1000, Away(45));
        var record = await kit.Db.AddRecordAsync(externalId, "Series");
        if (linked)
            await kit.Db.AddLinkAsync(series, record);
        return new Mixed(series, chapters, v1, v2, c1, c5, record);
    }

    private static CoverTarget Target(CatalogNodeEntity n) => new(n.Id, n.PublicId, n.Kind == (int)CatalogNodeKind.Folder);

    private static async Task<CoverResolution> ResolveAsync(CoverLayerTestKit kit, CatalogNodeEntity node) =>
        (await kit.Resolutions().ResolveAsync([Target(node)], default))[node.Id];

    [Fact]
    public async Task MixedSeries_ShowsVolume1_NotChapter1()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var s = await SeedMixedAsync(kit, "301");

        // Before a decision: the folder-native file default - the first archive by name, chapter 1 (the owner's bug).
        Assert.Equal($"/api/v1/items/{s.C1.PublicId}/cover?v=1", (await ResolveAsync(kit, s.Series)).Url);

        await kit.Decisions().DecideSubtreeAsync(s.Series.Id, default);
        var auto = await kit.AutoAsync(s.Series.Id);
        Assert.Equal((int)AutoCoverSource.LocalVolume1, auto!.Source);
        Assert.Equal(s.V1.Id, auto.ArchiveNodeId);
        Assert.Equal((int)AutoCoverReason.SeriesLocalVolume1, auto.Reason);

        var folder = await ResolveAsync(kit, s.Series);
        Assert.Equal($"/api/v1/items/{s.V1.PublicId}/cover?v=1", folder.Url);
        Assert.Equal(CardCoverSource.File, folder.Source);
        Assert.Equal(s.Series.Id, folder.NodeId);
        Assert.True(folder.OwnLayer);
        // The folder decision hashes nothing (volume 1's own decision does the comparing) and is not repeated.
        Assert.Equal(0, kit.Hasher.Calls);
        Assert.Equal(CoverDecisionOutcome.Unchanged, await kit.Decisions().DecideAsync(s.Series.Id, default));
        // The Chapters subfolder of the series (no web cover for its volume) keeps its own file default.
        Assert.Equal($"/api/v1/items/{s.C1.PublicId}/cover?v=1", (await ResolveAsync(kit, s.Chapters)).Url);
    }

    [Fact]
    public async Task TheSeriesCover_IsWhatever_Volume1sOwnCardShows()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        // Volume 1 is a jacket spread: its card shows the front half, and so does the series.
        var s = await SeedMixedAsync(kit, "302", v1Width: 1400);
        var companion = await kit.AddCompanionAsync(s.Record);
        await kit.Decisions().DecideSubtreeAsync(s.Series.Id, default);
        var v1 = await ResolveAsync(kit, s.V1);
        Assert.Equal(CardCoverSource.Crop, v1.Source);
        var series = await ResolveAsync(kit, s.Series);
        Assert.Equal(v1.Url, series.Url);
        Assert.Equal(CardCoverSource.Crop, series.Source);

        // The English volume 1 cover arrives and the crop is clearly not it: volume 1 takes the web cover - so does the series.
        var english = await kit.AddStoredCoverAsync(companion, 1, "en", ~kit.Renderer.HashBySide[CoverCropSides.Right]);
        await kit.Decisions().DecideSubtreeAsync(s.Series.Id, default);
        v1 = await ResolveAsync(kit, s.V1);
        Assert.Equal((CardCoverSource.WebVolume, english.PublicId), (v1.Source, v1.VolumeCoverPublicId));
        series = await ResolveAsync(kit, s.Series);
        Assert.Equal(v1.Url, series.Url);
        Assert.Equal(CardCoverSource.WebVolume, series.Source);

        // "Show saved web covers" off: volume 1's own page 1 again - never chapter 1 - on both.
        var library = await kit.Db.Db.Libraries.SingleAsync(l => l.Id == kit.Db.LibraryId);
        library.WebCoversHidden = true;
        await kit.Db.Db.SaveChangesAsync();
        v1 = await ResolveAsync(kit, s.V1);
        Assert.Equal($"/api/v1/items/{s.V1.PublicId}/cover?v=1", v1.Url);
        Assert.Equal(v1.Url, (await ResolveAsync(kit, s.Series)).Url);

        // An admin's choice on volume 1 reaches the series too.
        kit.Db.Db.NodeCoverChoices.Add(new NodeCoverChoiceEntity
        {
            NodeId = s.V1.Id,
            Mode = (int)CoverChoiceMode.FilePinned,
            Version = 1,
            SetAt = DateTimeOffset.UtcNow,
        });
        await kit.Db.Db.SaveChangesAsync();
        Assert.Equal($"/api/v1/items/{s.V1.PublicId}/cover?v=1", (await ResolveAsync(kit, s.Series)).Url);
    }

    [Fact]
    public async Task OnlyAnOriginLanguageVolume1Cover_TheSeriesKeepsYourVolume1()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var s = await SeedMixedAsync(kit, "303");
        var companion = await kit.AddCompanionAsync(s.Record);
        await kit.AddStoredCoverAsync(companion, 1, "ja", Away(35));

        await kit.Decisions().DecideSubtreeAsync(s.Series.Id, default);
        Assert.Equal((int)AutoCoverReason.OtherLanguageKept, (await kit.AutoAsync(s.V1.Id))!.Reason);
        Assert.Equal($"/api/v1/items/{s.V1.PublicId}/cover?v=1", (await ResolveAsync(kit, s.Series)).Url);
    }

    [Fact]
    public async Task Volume1Gone_TheFolderFallsBack_AndIsDecidedAgain()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var s = await SeedMixedAsync(kit, "304");
        var companion = await kit.AddCompanionAsync(s.Record);
        var w1 = await kit.AddStoredCoverAsync(companion, 1, "en", Away(50));
        await kit.Decisions().DecideSubtreeAsync(s.Series.Id, default);
        var before = (await kit.AutoAsync(s.Series.Id))!.Version;

        var v1 = await kit.Db.Db.CatalogNodes.SingleAsync(n => n.Id == s.V1.Id);
        v1.Availability = (int)CatalogNodeAvailability.Tombstoned;
        await kit.Db.Db.SaveChangesAsync();
        // Until the folder is decided again: its file default, never a broken image.
        Assert.Equal($"/api/v1/items/{s.C1.PublicId}/cover?v=1", (await ResolveAsync(kit, s.Series)).Url);

        // Decided again without a local volume 1: the web volume 1 cover (owner Q3, unchanged), a new URL version.
        Assert.Equal(CoverDecisionOutcome.Decided, await kit.Decisions().DecideAsync(s.Series.Id, default));
        var auto = await kit.AutoAsync(s.Series.Id);
        Assert.Equal((int)AutoCoverSource.WebVolume, auto!.Source);
        Assert.Equal(w1.Id, auto.VolumeCoverId);
        Assert.Null(auto.ArchiveNodeId);
        Assert.True(auto.Version > before);
    }

    [Fact]
    public async Task UnlinkedAndDontMatch_KeepTheFolderNativeRule()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var s = await SeedMixedAsync(kit, "305", linked: false);
        await kit.Decisions().DecideSubtreeAsync(s.Series.Id, default);
        Assert.Null(await kit.AutoAsync(s.Series.Id));
        Assert.Equal($"/api/v1/items/{s.C1.PublicId}/cover?v=1", (await ResolveAsync(kit, s.Series)).Url);

        var dontMatch = await SeedMixedAsync(kit, "306", linked: false);
        await kit.Db.AddLinkAsync(dontMatch.Series, null, SeriesLinkState.DontMatch);
        await kit.Decisions().DecideSubtreeAsync(dontMatch.Series.Id, default);
        Assert.Null(await kit.AutoAsync(dontMatch.Series.Id));
    }

    // ----- Home: Continue reading (the series cover on archive cards) --------------------------------------------------

    [Fact]
    public async Task ContinueReading_AChapterShowsItsSeriesCover_AVolumeItsOwn()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var s = await SeedMixedAsync(kit, "311");
        await kit.Decisions().DecideSubtreeAsync(s.Series.Id, default);

        var covers = await kit.Resolutions().SeriesCoversAsync([Target(s.C5), Target(s.V2)], default);
        Assert.Equal($"/api/v1/items/{s.V1.PublicId}/cover?v=1", covers[s.C5.Id].Url);
        Assert.Equal(s.C5.Id, covers[s.C5.Id].NodeId);
        // An archive that IS a volume keeps its own cover (a volume cover already).
        Assert.False(covers.ContainsKey(s.V2.Id));
    }

    [Fact]
    public async Task ContinueReading_NoSeriesCoverOfItsOwn_TheCardKeepsItsCover()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        // A linked chapters-only series without any web cover: its folder shows chapter 1 (the file default) - not a series
        // cover, so a chapter card keeps its own page.
        var chapters = await kit.Db.AddFolderAsync(null, "Chapter Series");
        var c1 = await kit.AddBookAsync(chapters, "Chapter Series c001", 700, 1000, Local);
        var c2 = await kit.AddBookAsync(chapters, "Chapter Series c002", 700, 1000, Away(30));
        var record = await kit.Db.AddRecordAsync("312", "Chapter Series");
        await kit.Db.AddLinkAsync(chapters, record);
        await kit.Decisions().DecideSubtreeAsync(chapters.Id, default);
        Assert.Empty(await kit.Resolutions().SeriesCoversAsync([Target(c2)], default));

        // The web volume 1 cover arrives: now the series has a cover of its own, and chapter 2 shows it.
        var companion = await kit.AddCompanionAsync(record);
        var w1 = await kit.AddStoredCoverAsync(companion, 1, "en", Away(50));
        await kit.Decisions().DecideSubtreeAsync(chapters.Id, default);
        var covers = await kit.Resolutions().SeriesCoversAsync([Target(c2)], default);
        Assert.Equal(w1.PublicId, covers[c2.Id].VolumeCoverPublicId);

        // Unlinked and one-shots linked on their own archive: nothing.
        var loose = await kit.Db.AddFolderAsync(null, "Loose");
        var looseChapter = await kit.AddBookAsync(loose, "Loose c003", 700, 1000, Local);
        var oneShot = await kit.AddBookAsync(null, "One Shot", 700, 1000, Local);
        await kit.Db.AddLinkAsync(oneShot, await kit.Db.AddRecordAsync("313", "One Shot"));
        Assert.Empty(await kit.Resolutions().SeriesCoversAsync([Target(looseChapter), Target(oneShot)], default));
        Assert.NotNull(c1);
    }
}
