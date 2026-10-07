namespace com.lifepixer.mangapixer.Tests.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests (1.36.0) of the cover picker on a folder that is NOT a series itself: "Covers from the web" lists the STORED
/// covers of every series linked (Confirmed / Auto) anywhere below it, one entry per series in the folder's order, capped; a folder
/// that IS a series keeps its own covers only; the reasons when there is nothing to offer; Don't match and Collection about parents
/// are included; the choice is re-checked on the server (the node's own series, or a series linked below a non-series folder).
/// Synthetic rows and stored hashes only; nothing is requested.
/// </summary>
public sealed class FolderSeriesCoverPickerTests
{
    private static CoverPickerService Picker(CoverLayerTestKit kit) =>
        new(kit.Db.Db, kit.Resolutions(), new CoverCropService(kit.Db.Db, kit.Renderer, kit.Files), new AuditService(kit.Db.Db),
            NullLogger<CoverPickerService>.Instance);

    private sealed record Linked(CatalogNodeEntity Node, MetadataRecordEntity Record, MetadataRecordEntity Companion);

    private int _ids = 700;

    /// <summary>A series folder with one volume, linked (Auto by default) to a fresh record with a MangaDex companion.</summary>
    private async Task<Linked> AddSeriesAsync(CoverLayerTestKit kit, CatalogNodeEntity parent, string name,
        SeriesLinkState state = SeriesLinkState.Auto)
    {
        var folder = await kit.Db.AddFolderAsync(parent, name);
        await kit.AddBookAsync(folder, name + " v01", 700, 1000, 1UL);
        var record = await kit.Db.AddRecordAsync((++_ids).ToString(System.Globalization.CultureInfo.InvariantCulture), name + " (record)");
        await kit.Db.AddLinkAsync(folder, record, state);
        return new Linked(folder, record, await kit.AddCompanionAsync(record));
    }

    private static async Task<VolumeCoverEntity> AddListedCoverAsync(CoverLayerTestKit kit, MetadataRecordEntity companion, int volume, VolumeCoverState state)
    {
        var cover = new VolumeCoverEntity
        {
            PublicId = "vcl" + companion.Id + "x" + volume + state,
            ProviderRecordId = companion.Id,
            Kind = (int)VolumeCoverKind.Volume,
            Volume = volume,
            Locale = "en",
            RemoteId = "listed-" + volume,
            RemoteFile = "listed-" + volume + ".jpg",
            State = (int)state,
            ListedAt = DateTimeOffset.UtcNow,
        };
        kit.Db.Db.VolumeCovers.Add(cover);
        await kit.Db.Db.SaveChangesAsync();
        return cover;
    }

    [Fact]
    public async Task ANonSeriesFolder_OffersTheStoredCoversOfEverySeriesBelow_PerSeries_InTheFoldersOrder()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        await kit.AddBookAsync(shelf, "Shelf extra", 700, 1000, 9UL);
        var spinoff = await AddSeriesAsync(kit, shelf, "Bravo Spinoff");
        var main = await AddSeriesAsync(kit, shelf, "Alpha Main", SeriesLinkState.Confirmed);
        var group = await kit.Db.AddFolderAsync(shelf, "Charlie Group");
        var side = await AddSeriesAsync(kit, group, "Delta Side");
        var noCovers = await AddSeriesAsync(kit, shelf, "Echo Bare");
        var unlinked = await kit.Db.AddFolderAsync(shelf, "Foxtrot Plain");
        await kit.AddBookAsync(unlinked, "Foxtrot v01", 700, 1000, 3UL);
        var review = await kit.Db.AddFolderAsync(shelf, "Golf Review");
        await kit.Db.AddLinkAsync(review, await kit.Db.AddRecordAsync("790", "Golf"), SeriesLinkState.NeedsReview);
        // The main series split over a second folder: one entry, at its first place.
        var split = await kit.Db.AddFolderAsync(shelf, "Hotel Split");
        await kit.Db.AddLinkAsync(split, main.Record);
        // A one-shot archive linked on its own, loose in the shelf (archives sort after the folders).
        var oneShot = await kit.AddBookAsync(shelf, "India One-shot", 700, 1000, 4UL);
        var oneShotRecord = await kit.Db.AddRecordAsync("791", "India");
        await kit.Db.AddLinkAsync(oneShot, oneShotRecord);
        var oneShotCompanion = await kit.AddCompanionAsync(oneShotRecord);

        var mainV2 = await kit.AddStoredCoverAsync(main.Companion, 2, "en", 21UL);
        var mainV1Ja = await kit.AddStoredCoverAsync(main.Companion, 1, "ja", 12UL);
        var mainV1 = await kit.AddStoredCoverAsync(main.Companion, 1, "en", 11UL);
        var spinoffMain = await kit.AddStoredCoverAsync(spinoff.Companion, null, "ja", 31UL, VolumeCoverKind.Main);
        var spinoffV1 = await kit.AddStoredCoverAsync(spinoff.Companion, 1, "en", 32UL);
        var sideMain = await kit.AddStoredCoverAsync(side.Companion, null, "ko", 41UL, VolumeCoverKind.Main);
        var oneShotCover = await kit.AddStoredCoverAsync(oneShotCompanion, 1, "en", 51UL);
        // Known but not downloaded, and failed: never offered here (nothing is requested from the picker).
        var listed = await AddListedCoverAsync(kit, spinoff.Companion, 3, VolumeCoverState.Listed);
        await AddListedCoverAsync(kit, noCovers.Companion, 1, VolumeCoverState.Listed);
        await AddListedCoverAsync(kit, side.Companion, 2, VolumeCoverState.Failed);

        var options = (await Picker(kit).GetOptionsAsync(shelf.PublicId, default))!;

        Assert.True(options.WebAvailable);
        Assert.Null(options.WebUnavailableReason);
        Assert.Empty(options.Web);
        Assert.Equal(0, options.WebSeriesMore);
        Assert.Equal([main.Node.PublicId, spinoff.Node.PublicId, side.Node.PublicId, oneShot.PublicId], options.WebSeries.Select(s => s.NodeId));
        Assert.Equal(["Alpha Main", "Bravo Spinoff", "Delta Side", "India One-shot"], options.WebSeries.Select(s => s.DisplayName));
        Assert.Equal("Alpha Main (record)", options.WebSeries[0].SeriesTitle);

        // Within a series: as today - by volume (the main cover last), each by kind, edition, language.
        var mainGroups = options.WebSeries[0].Groups;
        Assert.Equal([1, 2], mainGroups.Select(g => g.Volume));
        Assert.Equal([mainV1.PublicId, mainV1Ja.PublicId], mainGroups[0].Covers.Select(c => c.Id));
        Assert.Equal(mainV2.PublicId, Assert.Single(mainGroups[1].Covers).Id);
        Assert.All(options.WebSeries.SelectMany(s => s.Groups).SelectMany(g => g.Covers), c =>
        {
            Assert.True(c.Stored);
            Assert.Equal(CoverPickerService.VolumeCoverImageUrl(c.Id, 1), c.ImageUrl);
        });
        Assert.Equal([(int?)1, null], options.WebSeries[1].Groups.Select(g => g.Volume));
        Assert.Equal([spinoffV1.PublicId, spinoffMain.PublicId], options.WebSeries[1].Groups.SelectMany(g => g.Covers).Select(c => c.Id));
        Assert.Equal(sideMain.PublicId, options.WebSeries[2].Groups.Single().Covers.Single().Id);
        Assert.Equal(oneShotCover.PublicId, options.WebSeries[3].Groups.Single().Covers.Single().Id);
        Assert.DoesNotContain(options.WebSeries.SelectMany(s => s.Groups).SelectMany(g => g.Covers), c => c.Id == listed.PublicId);

        // The local options are unchanged.
        Assert.Contains(options.Local, o => o.Kind == CoverOptionKind.File);
        Assert.Contains(options.Local, o => o.Kind == CoverOptionKind.Archive);
    }

    [Fact]
    public async Task AFolderThatIsASeries_KeepsItsOwnCoversOnly_AlsoWithASeriesLinkedInsideIt()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        var main = await AddSeriesAsync(kit, shelf, "Alpha Main");
        var inner = await AddSeriesAsync(kit, main.Node, "Alpha Gaiden");
        var own = await kit.AddStoredCoverAsync(main.Companion, 1, "en", 11UL);
        var notOwn = await kit.AddStoredCoverAsync(inner.Companion, 1, "en", 12UL);
        var picker = Picker(kit);

        var options = (await picker.GetOptionsAsync(main.Node.PublicId, default))!;
        Assert.True(options.WebAvailable);
        Assert.Empty(options.WebSeries);
        Assert.Equal(own.PublicId, options.Web.Single().Covers.Single().Id);

        // A subfolder of a series inherits it: its own series' covers, as before.
        var part = await kit.Db.AddFolderAsync(main.Node, "Alpha Part 2");
        options = (await picker.GetOptionsAsync(part.PublicId, default))!;
        Assert.Empty(options.WebSeries);
        Assert.Equal(own.PublicId, options.Web.Single().Covers.Single().Id);

        // The server refuses the inner series' cover for the series folder (the picker never offers it).
        var (result, _) = await picker.SetChoiceAsync(main.Node.PublicId, new CoverChoiceRequest { Mode = CoverMode.VolumeCover, VolumeCoverId = notOwn.PublicId },
            "admin", null, default);
        Assert.Equal(CoverChoiceResult.Invalid, result);
        (result, _) = await picker.SetChoiceAsync(main.Node.PublicId, new CoverChoiceRequest { Mode = CoverMode.VolumeCover, VolumeCoverId = own.PublicId },
            "admin", null, default);
        Assert.Equal(CoverChoiceResult.Ok, result);
    }

    [Fact]
    public async Task NothingToOffer_SaysWhy()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var picker = Picker(kit);
        var plain = await kit.Db.AddFolderAsync(null, "Plain");
        await kit.AddBookAsync(plain, "Plain v01", 700, 1000, 1UL);
        var options = (await picker.GetOptionsAsync(plain.PublicId, default))!;
        Assert.Equal((false, "not_linked"), (options.WebAvailable, options.WebUnavailableReason));
        Assert.Empty(options.WebSeries);

        // Don't match with nothing linked below: today's reason.
        var blocked = await kit.Db.AddFolderAsync(null, "Blocked");
        await kit.Db.AddLinkAsync(blocked, null, SeriesLinkState.DontMatch);
        Assert.Equal("dont_match", (await picker.GetOptionsAsync(blocked.PublicId, default))!.WebUnavailableReason);

        // Series linked below, but none with a stored cover (not downloaded yet, or no companion).
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        var bare = await AddSeriesAsync(kit, shelf, "Bare");
        await AddListedCoverAsync(kit, bare.Companion, 1, VolumeCoverState.Listed);
        var lonely = await kit.Db.AddFolderAsync(shelf, "Lonely");
        await kit.Db.AddLinkAsync(lonely, await kit.Db.AddRecordAsync("799", "Lonely"));
        options = (await picker.GetOptionsAsync(shelf.PublicId, default))!;
        Assert.Equal((false, "no_series_covers"), (options.WebAvailable, options.WebUnavailableReason));
        Assert.Empty(options.WebSeries);

        // With a stored cover: offered; then the same switches as for a series close it.
        await kit.AddStoredCoverAsync(bare.Companion, 1, "en", 5UL);
        Assert.Single((await picker.GetOptionsAsync(shelf.PublicId, default))!.WebSeries);
        var library = await kit.Db.Db.Libraries.SingleAsync(l => l.Id == kit.Db.LibraryId);
        library.WebCoversHidden = true;
        await kit.Db.Db.SaveChangesAsync();
        options = (await picker.GetOptionsAsync(shelf.PublicId, default))!;
        Assert.Equal((false, "web_covers_hidden"), (options.WebAvailable, options.WebUnavailableReason));
        library.WebCoversHidden = false;
        var settings = await kit.SettingsAsync();
        settings.MetadataVolumeCoversEnabled = false;
        await kit.Db.Db.SaveChangesAsync();
        options = (await picker.GetOptionsAsync(shelf.PublicId, default))!;
        Assert.Equal((false, "volume_covers_off"), (options.WebAvailable, options.WebUnavailableReason));
        Assert.Empty(options.WebSeries);
    }

    [Theory]
    [InlineData(SeriesLinkState.DontMatch)]
    [InlineData(SeriesLinkState.CollectionAbout)]
    [InlineData(SeriesLinkState.NeedsReview)]
    public async Task DontMatch_CollectionAbout_AndNeedsReviewParents_OfferTheSeriesBelowToo(SeriesLinkState parentState)
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var parent = await kit.Db.AddFolderAsync(null, "Parent");
        await kit.Db.AddLinkAsync(parent, parentState == SeriesLinkState.DontMatch ? null : await kit.Db.AddRecordAsync("780", "About"), parentState);
        var series = await AddSeriesAsync(kit, parent, "Kilo");
        var cover = await kit.AddStoredCoverAsync(series.Companion, 1, "en", 7UL);
        var picker = Picker(kit);

        var options = (await picker.GetOptionsAsync(parent.PublicId, default))!;
        Assert.True(options.WebAvailable);
        Assert.Equal(cover.PublicId, options.WebSeries.Single().Groups.Single().Covers.Single().Id);

        var (result, state) = await picker.SetChoiceAsync(parent.PublicId, new CoverChoiceRequest { Mode = CoverMode.VolumeCover, VolumeCoverId = cover.PublicId },
            "admin", null, default);
        Assert.Equal(CoverChoiceResult.Ok, result);
        Assert.Equal(CoverMode.VolumeCover, state!.Mode);
        var resolved = await kit.Resolutions().ResolveOneAsync(parent, default);
        Assert.Equal(CardCoverSource.Chosen, resolved!.Source);
        Assert.Equal(cover.Id, resolved.VolumeCoverId);
    }

    [Fact]
    public async Task ManySeriesBelow_AreCapped_AndTheRestCounted()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        var all = new List<Linked>();
        for (var i = 0; i < CoverPickerService.MaxWebSeries + 3; i++)
        {
            var s = await AddSeriesAsync(kit, shelf, "Series " + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
            await kit.AddStoredCoverAsync(s.Companion, 1, "en", (ulong)(100 + i));
            all.Add(s);
        }

        var options = (await Picker(kit).GetOptionsAsync(shelf.PublicId, default))!;
        Assert.Equal(all.Take(CoverPickerService.MaxWebSeries).Select(s => s.Node.PublicId), options.WebSeries.Select(s => s.NodeId));
        Assert.Equal(3, options.WebSeriesMore);
    }

    [Fact]
    public async Task TheChoice_IsReCheckedOnTheServer()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        var inside = await AddSeriesAsync(kit, shelf, "Lima");
        var elsewhere = await AddSeriesAsync(kit, await kit.Db.AddFolderAsync(null, "Other shelf"), "Mike");
        var insideCover = await kit.AddStoredCoverAsync(inside.Companion, 1, "en", 1UL);
        var elsewhereCover = await kit.AddStoredCoverAsync(elsewhere.Companion, 1, "en", 2UL);
        var notDownloaded = await AddListedCoverAsync(kit, inside.Companion, 2, VolumeCoverState.Listed);
        var picker = Picker(kit);
        Task<(CoverChoiceResult Result, CoverStateDto? State)> Choose(CatalogNodeEntity node, string coverId) =>
            picker.SetChoiceAsync(node.PublicId, new CoverChoiceRequest { Mode = CoverMode.VolumeCover, VolumeCoverId = coverId }, "admin", null, default);

        Assert.Equal(CoverChoiceResult.Invalid, (await Choose(shelf, elsewhereCover.PublicId)).Result); // a series not below the folder
        Assert.Equal(CoverChoiceResult.Invalid, (await Choose(shelf, "vc-unknown")).Result);
        Assert.Equal(CoverChoiceResult.NotStored, (await Choose(shelf, notDownloaded.PublicId)).Result);
        // An archive is never "a folder with series below".
        var loose = await kit.AddBookAsync(shelf, "Loose", 700, 1000, 8UL);
        Assert.Equal(CoverChoiceResult.Invalid, (await Choose(loose, insideCover.PublicId)).Result);
        Assert.False(await kit.Db.Db.NodeCoverChoices.AnyAsync());

        Assert.Equal(CoverChoiceResult.Ok, (await Choose(shelf, insideCover.PublicId)).Result);
        var row = await kit.Db.Db.NodeCoverChoices.AsNoTracking().SingleAsync();
        Assert.Equal((shelf.Id, (int)CoverChoiceMode.VolumeCover, (long?)insideCover.Id), (row.NodeId, row.Mode, row.VolumeCoverId));
    }
}
