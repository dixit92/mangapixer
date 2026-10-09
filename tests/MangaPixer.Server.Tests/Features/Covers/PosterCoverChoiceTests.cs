namespace com.lifepixer.mangapixer.Tests.Server.Features.Covers;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests (1.39.0) of "the series poster as a cover choice": a folder whose NEAREST link is a Confirmed / Auto record with
/// a STORED poster offers it in the picker; choosing it stores only the mode (the record is always the nearest link's at resolve time);
/// the card shows the poster, with a URL that changes with the record and its image version; and every stale case (the link gone, no
/// stored poster, the web layer closed) falls back to the automatic cover. Local data only: nothing here sends a request. Posters are
/// synthetic stored hashes.
/// </summary>
public sealed class PosterCoverChoiceTests
{
    private static CoverPickerService Picker(CoverLayerTestKit kit) =>
        new(kit.Db.Db, kit.Resolutions(), new CoverCropService(kit.Db.Db, kit.Renderer, kit.Files), new AuditService(kit.Db.Db),
            NullLogger<CoverPickerService>.Instance);

    private static CoverChoiceRequest Poster => new() { Mode = CoverMode.Poster };

    private sealed record Series(CatalogNodeEntity Folder, CatalogNodeEntity V1, CatalogNodeEntity V2, MetadataRecordEntity Record);

    private int _ids = 900;

    /// <summary>A series folder with two volumes, linked (Auto by default) to a fresh record.</summary>
    private async Task<Series> AddSeriesAsync(CoverLayerTestKit kit, CatalogNodeEntity? parent, string name, SeriesLinkState state = SeriesLinkState.Auto,
        bool poster = true)
    {
        var folder = await kit.Db.AddFolderAsync(parent, name);
        var v1 = await kit.AddBookAsync(folder, name + " v01", 700, 1000, 1UL);
        var v2 = await kit.AddBookAsync(folder, name + " v02", 700, 1000, 2UL);
        var record = await kit.Db.AddRecordAsync((++_ids).ToString(CultureInfo.InvariantCulture), name + " (record)");
        await kit.Db.AddLinkAsync(folder, record, state);
        if (poster)
            await kit.SetPosterAsync(record, 0xAAUL);
        return new Series(folder, v1, v2, record);
    }

    private static string PosterUrl(CatalogNodeEntity node, MetadataRecordEntity record) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/v1/nodes/{node.PublicId}/cover-poster?v={record.PublicId}-{record.ImageVersion}");

    private static async Task<CoverResolution> ResolveAsync(CoverLayerTestKit kit, CatalogNodeEntity node) =>
        (await kit.Resolutions().ResolveAsync([new CoverTarget(node.Id, node.PublicId, node.Kind == 0)], default))[node.Id];

    [Fact]
    public async Task ALinkedSeriesFolder_OffersItsStoredPoster_AlsoWithoutAMangaDexCompanion_AndForAnInheritedLink()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var series = await AddSeriesAsync(kit, null, "Alpha");
        var sub = await kit.Db.AddFolderAsync(series.Folder, "Alpha extras");
        await kit.AddBookAsync(sub, "Alpha extra", 700, 1000, 3UL);
        var picker = Picker(kit);

        var options = (await picker.GetOptionsAsync(series.Folder.PublicId, default))!;
        Assert.NotNull(options.Poster);
        Assert.Equal(PosterUrl(series.Folder, series.Record), options.Poster.ImageUrl);
        Assert.Equal(CoverMode.Automatic, options.Current.Mode);
        // The poster does not need the MangaDex companion the volume covers need: the volume covers' own reason stays.
        Assert.Equal("no_companion", options.WebUnavailableReason);

        // A subfolder inherits the series link: it offers the same poster (its own URL).
        var inherited = (await picker.GetOptionsAsync(sub.PublicId, default))!;
        Assert.Equal(PosterUrl(sub, series.Record), inherited.Poster!.ImageUrl);
    }

    [Fact]
    public async Task NoPoster_WhenItIsNotStored_TheLinkIsNotASeries_OrTheNodeIsNotAFolder()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var noImage = await AddSeriesAsync(kit, null, "Bare", poster: false);
        var dontMatch = await AddSeriesAsync(kit, null, "Refused", SeriesLinkState.DontMatch);
        var review = await AddSeriesAsync(kit, null, "Pending", SeriesLinkState.NeedsReview);
        var collection = await AddSeriesAsync(kit, null, "Anthology", SeriesLinkState.CollectionAbout);
        var unlinked = await kit.Db.AddFolderAsync(null, "Plain");
        await kit.AddBookAsync(unlinked, "Plain v01", 700, 1000, 4UL);
        var series = await AddSeriesAsync(kit, null, "Alpha");
        var picker = Picker(kit);

        foreach (var node in new[] { noImage.Folder, dontMatch.Folder, review.Folder, collection.Folder, unlinked, series.V1 })
            Assert.Null((await picker.GetOptionsAsync(node.PublicId, default))!.Poster);

        // A failed / missing image state is not stored either.
        series.Record.ImageState = 2;
        await kit.Db.Db.SaveChangesAsync();
        Assert.Null((await picker.GetOptionsAsync(series.Folder.PublicId, default))!.Poster);
    }

    [Fact]
    public async Task ANonSeriesFolder_OffersNoPoster_EvenWithSeriesPostersInside()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        var main = await AddSeriesAsync(kit, shelf, "Alpha Main");
        var spinoff = await AddSeriesAsync(kit, shelf, "Bravo Spinoff");
        await kit.AddStoredCoverAsync(await kit.AddCompanionAsync(main.Record), 1, "en", 11UL);
        await kit.AddStoredCoverAsync(await kit.AddCompanionAsync(spinoff.Record), 1, "en", 12UL);
        var picker = Picker(kit);

        var options = (await picker.GetOptionsAsync(shelf.PublicId, default))!;
        Assert.Null(options.Poster);
        Assert.Equal(2, options.WebSeries.Count); // the per-series volume covers stay as they were
        // The server refuses it too: several series' posters below one folder need a record on the choice (out of scope).
        Assert.Equal(CoverChoiceResult.Invalid, (await picker.SetChoiceAsync(shelf.PublicId, Poster, "admin", null, default)).Result);
    }

    [Fact]
    public async Task TheWebLayerBeingClosed_HidesThePoster_AsItHidesTheWebCovers()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var series = await AddSeriesAsync(kit, null, "Alpha");
        var picker = Picker(kit);
        Assert.NotNull((await picker.GetOptionsAsync(series.Folder.PublicId, default))!.Poster);

        var settings = await kit.SettingsAsync();
        settings.MetadataVolumeCoversEnabled = false;
        await kit.Db.Db.SaveChangesAsync();
        var off = (await picker.GetOptionsAsync(series.Folder.PublicId, default))!;
        Assert.Null(off.Poster);
        Assert.Equal("volume_covers_off", off.WebUnavailableReason);
        settings.MetadataVolumeCoversEnabled = true;

        var library = await kit.Db.Db.Libraries.SingleAsync(l => l.Id == kit.Db.LibraryId);
        library.WebCoversHidden = true;
        await kit.Db.Db.SaveChangesAsync();
        var hidden = (await picker.GetOptionsAsync(series.Folder.PublicId, default))!;
        Assert.Null(hidden.Poster);
        Assert.Equal("web_covers_hidden", hidden.WebUnavailableReason);
    }

    [Fact]
    public async Task ChoosingThePoster_StoresOnlyTheMode_AndTheCardShowsTheStoredPoster()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var series = await AddSeriesAsync(kit, null, "Alpha");
        var picker = Picker(kit);
        var automatic = await ResolveAsync(kit, series.Folder);

        var (result, state) = await picker.SetChoiceAsync(series.Folder.PublicId, Poster, "admin", null, default);

        Assert.Equal(CoverChoiceResult.Ok, result);
        Assert.Equal(CoverMode.Poster, state!.Mode);
        var row = await kit.Db.Db.NodeCoverChoices.AsNoTracking().SingleAsync(c => c.NodeId == series.Folder.Id);
        Assert.Equal((int)CoverChoiceMode.Poster, row.Mode);
        Assert.Null(row.ArchiveNodeId);
        Assert.Null(row.VolumeCoverId);
        Assert.Null(row.CropSide);

        var card = await ResolveAsync(kit, series.Folder);
        Assert.Equal(CardCoverSource.Chosen, card.Source);
        Assert.Equal(CoverImageKind.Poster, card.Image);
        Assert.True(card.Layered);
        Assert.True(card.OwnLayer);
        Assert.Equal(series.Record.Id, card.RecordId);
        Assert.Equal(series.Record.ImageVersion, card.ImageVersion);
        Assert.NotEqual(automatic.Url, card.Url);
        Assert.Equal(card.Url, state.ImageUrl);
        // The picker names it as the current choice.
        Assert.Equal(CoverMode.Poster, (await picker.GetOptionsAsync(series.Folder.PublicId, default))!.Current.Mode);

        // Back to Automatic.
        Assert.Equal(CoverMode.Automatic, (await picker.ClearChoiceAsync(series.Folder.PublicId, "admin", default)).State!.Mode);
        Assert.Equal(automatic.Url, (await ResolveAsync(kit, series.Folder)).Url);
    }

    [Fact]
    public async Task TheChosenPoster_FollowsTheRecord_AndANewImageVersionGivesANewUrl()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var series = await AddSeriesAsync(kit, null, "Alpha");
        var picker = Picker(kit);
        await picker.SetChoiceAsync(series.Folder.PublicId, Poster, "admin", null, default);
        var first = await ResolveAsync(kit, series.Folder);

        // The record's poster is replaced (a new image version): the card URL changes.
        await kit.SetPosterAsync(series.Record, 0xBBUL);
        var second = await ResolveAsync(kit, series.Folder);
        Assert.Equal(CoverImageKind.Poster, second.Image);
        Assert.Equal(series.Record.ImageVersion, second.ImageVersion);
        Assert.NotEqual(first.Url, second.Url);

        // The link is re-pointed at another record: the choice follows it (nothing was stored about the old one).
        var oldRecordId = series.Record.Id;
        var next = await kit.Db.AddRecordAsync("990", "Other (record)");
        await kit.SetPosterAsync(next, 0xCCUL);
        var link = await kit.Db.Db.NodeSeriesLinks.SingleAsync(l => l.NodeId == series.Folder.Id);
        link.RecordId = next.Id;
        await kit.Db.Db.SaveChangesAsync();
        var third = await ResolveAsync(kit, series.Folder);
        Assert.Equal(next.Id, third.RecordId);
        Assert.NotEqual(oldRecordId, third.RecordId);
        Assert.NotEqual(second.Url, third.Url);
    }

    [Fact]
    public async Task TheChosenPoster_FallsBackToAutomatic_WhenTheLinkIsGone_ThePosterIsMissing_OrTheWebLayerIsClosed_AndComesBack()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var series = await AddSeriesAsync(kit, null, "Alpha");
        var companion = await kit.AddCompanionAsync(series.Record);
        await kit.AddStoredCoverAsync(companion, null, "en", 0x77UL, VolumeCoverKind.Main);
        await kit.Decisions().DecideSubtreeAsync(series.Folder.Id, default);
        var picker = Picker(kit);
        var automatic = await ResolveAsync(kit, series.Folder);
        await picker.SetChoiceAsync(series.Folder.PublicId, Poster, "admin", null, default);
        var chosen = await ResolveAsync(kit, series.Folder);
        Assert.Equal(CoverImageKind.Poster, chosen.Image);
        Assert.NotEqual(automatic.Url, chosen.Url);

        // The link goes away: the choice row stays but the card is automatic again.
        var link = await kit.Db.Db.NodeSeriesLinks.SingleAsync(l => l.NodeId == series.Folder.Id);
        kit.Db.Db.NodeSeriesLinks.Remove(link);
        await kit.Db.Db.SaveChangesAsync();
        var unlinked = await ResolveAsync(kit, series.Folder);
        Assert.NotEqual(CardCoverSource.Chosen, unlinked.Source);
        Assert.NotEqual(CoverImageKind.Poster, unlinked.Image);
        // The picker does not claim a poster the card does not show; the row stays, so the choice comes back with the link.
        Assert.Equal(CoverMode.Automatic, (await picker.StateAsync(series.Folder, default)).Mode);
        Assert.True(await kit.Db.Db.NodeCoverChoices.AnyAsync(c => c.NodeId == series.Folder.Id));

        // The link is back: so is the poster, at the same URL.
        kit.Db.Db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = series.Folder.Id, LibraryId = series.Folder.LibraryId, State = (int)SeriesLinkState.Auto,
            RecordId = series.Record.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await kit.Db.Db.SaveChangesAsync();
        Assert.Equal(chosen.Url, (await ResolveAsync(kit, series.Folder)).Url);

        // No stored poster any more.
        series.Record.ImageState = 0;
        await kit.Db.Db.SaveChangesAsync();
        Assert.NotEqual(CoverImageKind.Poster, (await ResolveAsync(kit, series.Folder)).Image);
        series.Record.ImageState = 1;
        await kit.Db.Db.SaveChangesAsync();

        // The library stops showing saved web covers (the same rule that hides a chosen volume cover).
        var library = await kit.Db.Db.Libraries.SingleAsync(l => l.Id == kit.Db.LibraryId);
        library.WebCoversHidden = true;
        await kit.Db.Db.SaveChangesAsync();
        Assert.NotEqual(CoverImageKind.Poster, (await ResolveAsync(kit, series.Folder)).Image);
        library.WebCoversHidden = false;
        await kit.Db.Db.SaveChangesAsync();
        Assert.Equal(chosen.Url, (await ResolveAsync(kit, series.Folder)).Url);
    }

    [Fact]
    public async Task SetChoice_Poster_IsCheckedOnTheServer()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var series = await AddSeriesAsync(kit, null, "Alpha");
        var noImage = await AddSeriesAsync(kit, null, "Bare", poster: false);
        var dontMatch = await AddSeriesAsync(kit, null, "Refused", SeriesLinkState.DontMatch);
        var collection = await AddSeriesAsync(kit, null, "Anthology", SeriesLinkState.CollectionAbout);
        var unlinked = await kit.Db.AddFolderAsync(null, "Plain");
        await kit.AddBookAsync(unlinked, "Plain v01", 700, 1000, 4UL);
        var picker = Picker(kit);

        // A linked record without a stored poster: nothing to show, and nothing is requested to get one.
        Assert.Equal(CoverChoiceResult.NotStored, (await picker.SetChoiceAsync(noImage.Folder.PublicId, Poster, "admin", null, default)).Result);
        foreach (var node in new[] { unlinked, dontMatch.Folder, collection.Folder, series.V1 })
            Assert.Equal(CoverChoiceResult.Invalid, (await picker.SetChoiceAsync(node.PublicId, Poster, "admin", null, default)).Result);
        Assert.Equal(CoverChoiceResult.NotFound, (await picker.SetChoiceAsync("nope", Poster, "admin", null, default)).Result);
        Assert.False(await kit.Db.Db.NodeCoverChoices.AnyAsync());

        Assert.Equal(CoverChoiceResult.Ok, (await picker.SetChoiceAsync(series.Folder.PublicId, Poster, "admin", null, default)).Result);
        Assert.Equal(1, await kit.Db.Db.NodeCoverChoices.CountAsync());
    }
}
