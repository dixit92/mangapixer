namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Features.Export;
using Xunit;

/// <summary>
/// 1.37.0: an "Artist folder" is exported as its own state (owner decision 5: not "Unknown") with no record and nothing computed from
/// one; an archive inside with its own link is an item of its own. Synthetic names only.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class ArtistFolderExportTests
{
    [Fact]
    public async Task Export_CarriesTheState_WithoutARecord()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var record = await kit.Db.AddRecordAsync("4545", "Qzv Harbor Tale");
        var artist = await kit.Db.AddFolderAsync(null, "Beta Painter");
        var work = await kit.Db.AddArchiveAsync(artist, "Qzv Harbor Tale.cbz");
        await kit.Db.AddArchiveAsync(artist, "Qzv Quiet Orchard.cbz");
        await kit.Db.AddLinkAsync(artist, null, SeriesLinkState.ArtistFolder);
        await kit.Db.AddLinkAsync(work, record, SeriesLinkState.Auto);

        await kit.RebuildAsync();
        var items = await kit.ItemsAsync();

        var folder = items[artist.PublicId];
        Assert.Equal("ArtistFolder", folder.Link.State);
        Assert.Null(folder.Record);
        Assert.Null(folder.Companions.Mangadex);
        Assert.Null(folder.Companions.Anilist);
        Assert.Empty(folder.OfficialLinks);
        Assert.Null(folder.Volumes);
        Assert.Null(folder.Completion);
        Assert.Null(folder.Refresh);

        var inside = items[work.PublicId];
        Assert.Equal("Auto", inside.Link.State);
        Assert.Equal("Qzv Harbor Tale", inside.Record!.Title);
    }
}
