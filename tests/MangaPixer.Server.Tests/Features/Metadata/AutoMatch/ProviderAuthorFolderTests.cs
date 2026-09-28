namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the provider-author half of the artist-folder rule (1.28.0): the author set is read
/// from the records linked (Confirmed / Auto) in the library - nothing is requested - and the cached snapshot
/// follows link changes even when the catalog revision does not change. Real <see cref="WorkDetector"/>,
/// synthetic names only.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class ProviderAuthorFolderTests : IAsyncLifetime
{
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private async Task<MetadataRecordEntity> RecordAsync(string id, string creatorsJson)
    {
        var record = await _db.AddRecordAsync(id, "Synthetic " + id);
        record.CreatorsJson = creatorsJson;
        await _db.Db.SaveChangesAsync();
        return record;
    }

    /// <summary>"Given Family/" with unbracketed "Given Family - Title" archives: review only by shape alone.</summary>
    private async Task<CatalogNodeEntity> AuthorFolderAsync()
    {
        var folder = await _db.AddFolderAsync(null, "Given Family");
        foreach (var title in new[] { "Given Family - Alpha Story", "Given Family - Beta Tale", "Given Family - Gamma Saga" })
            await _db.AddArchiveAsync(folder, title);
        return folder;
    }

    [Fact]
    public async Task AuthorSet_IsTheAuthorsAndArtistsOfConfirmedAndAutoLinks_InThisLibraryOnly()
    {
        var confirmed = await RecordAsync("101", """[{"name":"Given Family","role":"author"},{"name":"Some Editor","role":"other"}]""");
        var auto = await RecordAsync("102", """[{"name":"Other Artist","role":"artist"},{"name":"given family","role":"author"}]""");
        var elsewhere = await RecordAsync("103", """[{"name":"Foreign Author","role":"author"}]""");
        var a = await _db.AddFolderAsync(null, "Linked Saga");
        var b = await _db.AddFolderAsync(null, "Auto Saga");
        var c = await _db.AddFolderAsync(null, "Review Saga");
        var d = await _db.AddFolderAsync(null, "Skipped Saga");
        await _db.AddLinkAsync(a, confirmed);
        await _db.AddLinkAsync(b, auto, SeriesLinkState.Auto);
        await _db.AddLinkAsync(c, null, SeriesLinkState.NeedsReview);
        await _db.AddLinkAsync(d, null, SeriesLinkState.DontMatch);
        var other = await _db.AddLibraryAsync("pa2", "Other");
        var foreign = await _db.AddFolderAsync(null, "Foreign Saga", other.Id);
        await _db.AddLinkAsync(foreign, elsewhere);

        var set = await LibraryTreeSnapshot.LoadProviderAuthorsAsync(_db.Db, _db.LibraryId, CancellationToken.None);

        Assert.Equal(["Given Family", "Other Artist"], set.Names.Order(StringComparer.Ordinal).ToArray()); // de-duplicated by scoring form
        Assert.Equal(new[] { a.Id, b.Id }.Order(), set.LinkedNodeIds.Order());
        Assert.Equal(2, set.Stamp.Links);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task LinkStamp_ChangesWhenALinkIsAddedChangedOrRemoved_OrALinkedRecordIsRefetched()
    {
        var record = await RecordAsync("201", """[{"name":"Given Family","role":"author"}]""");
        var folder = await _db.AddFolderAsync(null, "Linked Saga");
        async Task<LibraryTreeSnapshot.LinkStamp> Stamp() => await LibraryTreeSnapshot.LinkStampAsync(_db.Db, _db.LibraryId, CancellationToken.None);

        var none = await Stamp();
        await _db.AddLinkAsync(folder, record);
        var linked = await Stamp();
        var link = await _db.Db.NodeSeriesLinks.SingleAsync();
        link.UpdatedAt = link.UpdatedAt.AddMinutes(1);
        await _db.Db.SaveChangesAsync();
        var changed = await Stamp();
        record.FetchedAt = record.FetchedAt.AddMinutes(1);
        await _db.Db.SaveChangesAsync();
        var refetched = await Stamp();
        _db.Db.NodeSeriesLinks.Remove(link);
        await _db.Db.SaveChangesAsync();

        Assert.Equal(default, none);
        Assert.Equal(1, linked.Links);
        Assert.NotEqual(linked, changed);
        Assert.NotEqual(changed, refetched);
        Assert.Equal(default, await Stamp());
    }

    [Fact]
    public async Task Detection_FollowsLinkChanges_WithTheCachedSnapshot_AndTheSwitchRestoresTheOldRule()
    {
        var authorFolder = await AuthorFolderAsync();
        var linked = await _db.AddFolderAsync(null, "Linked Saga");
        var record = await RecordAsync("301", """[{"name":"Family Given","role":"author"}]"""); // the other name order
        var detector = new WorkDetector();

        // No link yet: the shape alone cannot tell (review only, one work at folder level).
        var (before, _, _) = await _h.Service(detector).DetectLibraryAsync(_db.LibraryId, false, CancellationToken.None);
        Assert.Equal([(authorFolder.Id, MatchLevel.ReviewOnly)], before.Select(w => (w.AnchorNodeId, w.Level)).ToArray());

        // A link in the same library (the catalog revision is unchanged): the cached snapshot re-reads the authors.
        await _db.AddLinkAsync(linked, record);
        var (after, _, _) = await _h.Service(detector).DetectLibraryAsync(_db.LibraryId, false, CancellationToken.None);
        Assert.Equal(3, after.Count);
        Assert.All(after, w => Assert.Equal((MatchLevel.Archive, WorkClass.ArtistCollection, authorFolder.Id), (w.Level, w.Class, w.FolderId)));

        // The before / after switch: off = the 1.27.0 detector.
        var (off, _, _) = await _h.Service(detector, new MetadataAutoMatchOptions { ProviderAuthorFolders = false })
            .DetectLibraryAsync(_db.LibraryId, false, CancellationToken.None);
        Assert.Equal([MatchLevel.ReviewOnly], off.Select(w => w.Level).ToArray());

        // The link goes: back to review only.
        _db.Db.NodeSeriesLinks.RemoveRange(_db.Db.NodeSeriesLinks);
        await _db.Db.SaveChangesAsync();
        var (removed, _, _) = await _h.Service(detector).DetectLibraryAsync(_db.LibraryId, false, CancellationToken.None);
        Assert.Equal([MatchLevel.ReviewOnly], removed.Select(w => w.Level).ToArray());
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task PostScan_UsesTheAuthorsToo_ASeriesNamedLikeAnAuthorStaysASeries()
    {
        var record = await RecordAsync("401", """[{"name":"Given Family","role":"author"},{"name":"Alpha Saga","role":"artist"}]""");
        var linked = await _db.AddFolderAsync(null, "Linked Saga");
        await _db.AddLinkAsync(linked, record);
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var authorFolder = await AuthorFolderAsync();
        var series = await _db.AddFolderAsync(null, "Alpha Saga");
        for (var i = 1; i <= 4; i++)
            await _db.AddArchiveAsync(series, $"Alpha Saga v{i:D2}");

        Assert.Equal(4, await _h.Service(new WorkDetector()).EnqueueNewFoldersAsync(_db.LibraryId, since));
        var rows = await _db.Db.MetadataMatchQueue.AsNoTracking().ToListAsync();
        Assert.Equal((int)MatchLevel.Folder, rows.Single(r => r.NodeId == series.Id).Level);
        var archiveIds = await _db.Db.CatalogNodes.AsNoTracking().Where(n => n.ParentId == authorFolder.Id).Select(n => n.Id).ToListAsync();
        Assert.Equal(archiveIds.Order(), rows.Where(r => r.NodeId != series.Id).Select(r => r.NodeId).Order());
    }
}
