namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests (1.32.0) of the background volume-cover pass under a folder cover preference: a series whose every linked
/// folder is under "File covers" fetches no new web cover (the volume LISTS are still kept - the Volumes view and the Missing report
/// need them), it clears again when the preference does, a nearer "Web covers when available" row wins, a series linked from two places
/// keeps downloading while one of them wants web covers, and "covers on their way" counts nothing for a series that fetches none.
/// Recorded MangaDex answers (zero real network).
/// </summary>
public sealed class FolderCoverPreferencePassTests : IAsyncLifetime
{
    private MetadataTestDb _t = null!;
    private VolumePassHarness _h = null!;

    public async Task InitializeAsync()
    {
        _t = await MetadataTestDb.CreateAsync();
        _h = new VolumePassHarness(_t);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _t.DisposeAsync();
    }

    private async Task<(CatalogNodeEntity Shelf, CatalogNodeEntity Series, MetadataRecordEntity Record)> SeriesAsync(string muId, string title)
    {
        var shelf = await _t.AddFolderAsync(null, "Synthetic Collection " + muId);
        var folder = await _t.AddFolderAsync(shelf, "Synthetic Shelf " + muId);
        await _t.AddArchiveAsync(folder, "Synthetic Shelf v01.cbz");
        await _t.AddArchiveAsync(folder, "Synthetic Shelf v02.cbz");
        var record = await _t.AddRecordAsync(muId, title);
        record.OriginVolumes = 43;
        await _t.Db.SaveChangesAsync();
        await _t.AddLinkAsync(folder, record, SeriesLinkState.Auto);
        return (shelf, folder, record);
    }

    private async Task SetPreferenceAsync(CatalogNodeEntity folder, FolderCoverPreference? preference)
    {
        _t.Db.ChangeTracker.Clear();
        await _t.Db.FolderCoverPreferences.Where(f => f.NodeId == folder.Id).ExecuteDeleteAsync();
        if (preference is { } value)
        {
            _t.Db.FolderCoverPreferences.Add(new FolderCoverPreferenceEntity { NodeId = folder.Id, Preference = (int)value });
            await _t.Db.SaveChangesAsync();
        }
    }

    private int ImageRequests() => _h.MangaDexRequests().Count(u => u.Host == MetadataHttp.MangaDexImageHost);

    [Fact]
    public async Task UnderFileCovers_TheListsAreKept_NoCoverIsDownloaded_AndClearingTheRowDownloadsThem()
    {
        await _h.Auto.EnableAutomaticAsync();
        var (shelf, _, record) = await SeriesAsync(MdFixtures.MuBerserk, "Berserk");
        await SetPreferenceAsync(shelf, FolderCoverPreference.File);

        var first = await _h.TickAsync();

        Assert.Null(first.WaitingCode);
        Assert.Equal(0, ImageRequests());                                   // no cover fetched
        Assert.Equal(0, await _t.Db.VolumeCovers.CountAsync(c => c.State == (int)VolumeCoverState.Stored));
        Assert.Empty(_h.Renderer.Requests);
        // The lists are untouched by the preference: the companion, the volume map and the listed covers.
        Assert.True(await _t.Db.MetadataCompanions.AnyAsync(c => c.RecordId == record.Id));
        Assert.True(await _t.Db.SeriesVolumeMaps.AnyAsync(m => m.RecordId == record.Id));
        Assert.True(await _t.Db.VolumeCovers.AnyAsync(c => c.State == (int)VolumeCoverState.Listed));

        // The status counts nothing "on its way" for a series that fetches none.
        Assert.True(await _h.Pass().SkipsCoverDownloadsAsync(record.Id, [(await _t.Db.NodeSeriesLinks.SingleAsync(l => l.RecordId == record.Id)).NodeId]));

        // Back to inheriting: the next tick downloads volume 1 and the volume held.
        await SetPreferenceAsync(shelf, null);
        var second = await _h.TickAsync();
        Assert.Null(second.WaitingCode);
        Assert.Equal(2, ImageRequests());
        Assert.Equal([1, 2], (await _t.Db.VolumeCovers.Where(c => c.State == (int)VolumeCoverState.Stored).OrderBy(c => c.Volume).ToListAsync()).Select(c => c.Volume!.Value));
    }

    [Fact]
    public async Task ANearerWebCoversRow_BeatsFileCoversAbove_AndASeriesLinkedFromTwoFoldersKeepsDownloading()
    {
        await _h.Auto.EnableAutomaticAsync();
        var (shelf, series, record) = await SeriesAsync(MdFixtures.MuBerserk, "Berserk");
        await SetPreferenceAsync(shelf, FolderCoverPreference.File);
        var nodeIds = new[] { (await _t.Db.NodeSeriesLinks.SingleAsync(l => l.RecordId == record.Id)).NodeId };
        var pass = _h.Pass();
        var seriesRows = await pass.EligibleSeriesAsync();
        Assert.Contains(record.Id, await pass.FileCoverSeriesAsync(seriesRows, default));

        // A row on the series folder itself (nearest) says Web covers: the file-covers shelf above does not apply.
        await SetPreferenceAsync(series, FolderCoverPreference.Web);
        Assert.DoesNotContain(record.Id, await pass.FileCoverSeriesAsync(seriesRows, default));
        await SetPreferenceAsync(series, null);

        // The same record linked from a second folder outside the File covers shelf: its covers are shared, so they are fetched.
        var other = await _t.AddFolderAsync(null, "Synthetic Elsewhere");
        await _t.AddArchiveAsync(other, "Synthetic Elsewhere v01.cbz");
        await _t.AddLinkAsync(other, record, SeriesLinkState.Confirmed);
        seriesRows = await _h.Pass().EligibleSeriesAsync();
        Assert.Equal(2, seriesRows.Single(s => s.RecordId == record.Id).NodeIds.Count);
        Assert.DoesNotContain(record.Id, await pass.FileCoverSeriesAsync(seriesRows, default));
        Assert.False(await _h.Pass().SkipsCoverDownloadsAsync(record.Id, seriesRows.Single().NodeIds));
        Assert.NotEmpty(nodeIds);
    }
}
