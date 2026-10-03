namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Review;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of the "Same author" and "Same folder" groups of Needs review (1.33.0) on a synthetic doujin library:
/// balanced and unbalanced leading tags in two folders, a circle and its artist joining one group, the artist-folder and
/// ComicInfo fallbacks (never a translator), the release-tag guard, the row hints, the author / folder filters with the Later
/// filter and the cursor, and the Authors list. Nothing is sent to a provider.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class ReviewSameAuthorTests : IAsyncLifetime
{
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;
    private readonly Dictionary<string, CatalogNodeEntity> _n = [];
    private CatalogNodeEntity _folderA = null!;
    private CatalogNodeEntity _folderB = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
        _folderA = await _db.AddFolderAsync(null, "Doujins A");
        _folderB = await _db.AddFolderAsync(null, "Doujins B");
        var artistZ = await _db.AddFolderAsync(null, "Artist Z");
        await WaitingArchiveAsync("a1", _folderA, "[Circle One (Artist A)] Story One.cbz");
        await WaitingArchiveAsync("a2", _folderA, "Circle One] Story Two.cbz");
        await WaitingArchiveAsync("a3", _folderA, "[English] Story Three.cbz"); // a release tag: no author
        var a4 = await WaitingArchiveAsync("a4", _folderA, "Plain Story Four.cbz");
        await _db.AddComicInfoAsync(a4, "Plain Story", creators: [new ComicInfoCreator { Name = "Artist A", Role = "writer" },
            new ComicInfoCreator { Name = "Helper T", Role = "translator" }]);
        await WaitingArchiveAsync("b1", _folderB, "[Artist A] Story Five.cbz");
        await WaitingArchiveAsync("b2", _folderB, "Given Family] Story Six.cbz");
        await WaitingArchiveAsync("b3", _folderB, "[Family Given] Story Seven.cbz");
        var b4 = await WaitingArchiveAsync("b4", _folderB, "Lonely Story.cbz");
        await _db.AddComicInfoAsync(b4, "Lonely Story", creators: [new ComicInfoCreator { Name = "Helper T", Role = "translator" }]);
        await WaitingArchiveAsync("z1", artistZ, "Untitled Sketches.cbz", WorkClass.ArtistCollection); // alone: no hint
        var collected = await _db.AddFolderAsync(null, "[Circle One] Collected Works");
        await _db.AddArchiveAsync(collected, "Collected Works v01.cbz");
        await WaitingAsync("f1", collected, WorkClass.Series);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private async Task<CatalogNodeEntity> WaitingArchiveAsync(string tag, CatalogNodeEntity folder, string name, WorkClass cls = WorkClass.CollectionLeaf)
    {
        var archive = await _db.AddArchiveAsync(folder, name);
        await WaitingAsync(tag, archive, cls);
        return archive;
    }

    private async Task WaitingAsync(string tag, CatalogNodeEntity node, WorkClass cls)
    {
        _n[tag] = node;
        await _db.AddLinkAsync(node, null, SeriesLinkState.NeedsReview);
        _db.Db.MetadataMatchQueue.Add(new MetadataMatchQueueEntity
        {
            NodeId = node.Id,
            LibraryId = node.LibraryId,
            State = QueueState.Done,
            Outcome = (int)MatchBand.NeedsReview,
            WorkClass = (int)cls,
            EnqueuedAt = DateTimeOffset.UtcNow,
        });
        await _db.Db.SaveChangesAsync();
    }

    private MetadataReviewService Review()
    {
        _db.Db.ChangeTracker.Clear();
        return new MetadataReviewService(_db.Db, _h.Net.Links(), _h.Net.Identify(), _h.Service(), _h.CarryOver(), new AuditService(_db.Db),
            NullLogger<MetadataReviewService>.Instance, _h.Time);
    }

    private string TagOf(string publicId) => _n.Single(x => x.Value.PublicId == publicId).Key;

    private async Task<Dictionary<string, MetadataReviewItemDto>> ItemsAsync()
    {
        var (error, page) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, null, 100);
        Assert.Null(error);
        return page!.Items.ToDictionary(i => TagOf(i.NodeId));
    }

    private async Task<string[]> TagsAsync(int limit = 100, bool? later = null, string? author = null, string? folder = null)
    {
        var tags = new List<string>();
        string? cursor = null;
        for (var guard = 0; guard < 20; guard++)
        {
            var (error, page) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, cursor, limit, later: later, author: author, folder: folder);
            Assert.Null(error);
            tags.AddRange(page!.Items.Select(i => TagOf(i.NodeId)));
            if (!page.HasMore)
                return [.. tags.Order(StringComparer.Ordinal)];
            cursor = page.NextCursor;
        }
        Assert.Fail("The pages did not end.");
        return [];
    }

    [Fact]
    public async Task Hints_AnySharedName_JoinsOneGroup_WithFallbacks_AndFolders()
    {
        var items = await ItemsAsync();

        // a1 [Circle One (Artist A)] joins a2 (Circle One], unbalanced), a4 (ComicInfo writer Artist A), b1 ([Artist A], other
        // folder) and the folder work [Circle One] Collected Works: five works, one group.
        foreach (var tag in new[] { "a1", "a2", "a4", "b1", "f1" })
            Assert.Equal(("artista", "Artist A", 4), (items[tag].SameAuthor!.Key, items[tag].SameAuthor!.Label, items[tag].SameAuthor!.Others));
        // "Given Family]" and "[Family Given]": the same name in the other word order.
        Assert.Equal(("familygiven", 1), (items["b2"].SameAuthor!.Key, items["b2"].SameAuthor!.Others));
        Assert.Equal("familygiven", items["b3"].SameAuthor!.Key);
        // A release tag, a translator only, and an artist folder of one waiting work: no author hint.
        Assert.Null(items["a3"].SameAuthor);
        Assert.Null(items["b4"].SameAuthor);
        Assert.Null(items["z1"].SameAuthor);

        // Same folder: four waiting works in each Doujins folder; none for a top-level work or a folder of one.
        Assert.Equal((_folderA.PublicId, "Doujins A", 3), (items["a3"].SameFolder!.Key, items["a3"].SameFolder!.Label, items["a3"].SameFolder!.Others));
        Assert.Equal((_folderB.PublicId, 3), (items["b4"].SameFolder!.Key, items["b4"].SameFolder!.Others));
        Assert.Null(items["z1"].SameFolder);
        Assert.Null(items["f1"].SameFolder);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task AuthorFilter_ListsTheGroup_ByAnyKeyOfItsNames_WithLaterAndTheCursor()
    {
        string[] group = ["a1", "a2", "a4", "b1", "f1"];
        Assert.Equal(group, await TagsAsync(author: "artista"));
        Assert.Equal(group, await TagsAsync(author: "circleone")); // another name of the same group
        for (var limit = 1; limit <= 5; limit++)
            Assert.Equal(group, await TagsAsync(limit, author: "artista"));
        var (_, page) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, null, 2, author: "artista");
        Assert.Equal((5, true), (page!.Total, page.HasMore));

        Assert.Equal("ok", await Review().SetLaterAsync(_n["b1"].PublicId, later: true, "admin"));
        Assert.Equal(["b1"], await TagsAsync(later: true, author: "artista"));
        Assert.Equal(["a1", "a2", "a4", "f1"], await TagsAsync(later: false, author: "artista"));
        Assert.Equal(group, await TagsAsync(1, author: "artista")); // across the Later boundary too

        Assert.Empty(await TagsAsync(author: "nobodyknown"));
        Assert.Empty(await TagsAsync(author: "englishx"));
    }

    [Fact]
    public async Task FolderFilter_ListsTheFoldersWaitingWorks_NotWithAnAuthor()
    {
        Assert.Equal(["b1", "b2", "b3", "b4"], await TagsAsync(folder: _folderB.PublicId));
        Assert.Equal(["b1", "b2", "b3", "b4"], await TagsAsync(1, folder: _folderB.PublicId));
        Assert.Empty(await TagsAsync(folder: "nope"));
        var (error, _) = await Review().ListAsync(MetadataReviewTab.NeedsReview, null, null, 10, author: "artista", folder: _folderB.PublicId);
        Assert.Equal("invalid_filter", error);
    }

    [Fact]
    public async Task AuthorsList_LargestFirst_TwoOrMore_WithLater_AndTheLibraryFilter()
    {
        await Review().SetLaterAsync(_n["a2"].PublicId, later: true, "admin");
        var authors = (await Review().AuthorsAsync(null))!.Items;
        Assert.Equal([("artista", "Artist A", 5, 1), ("familygiven", "Family Given", 2, 0)],
            authors.Select(a => (a.Key, a.Label, a.Count, a.Later)));

        // A work by the same circle in another library counts only without the library filter.
        var other = await _db.AddLibraryAsync("otherlib", "Other Lib");
        var elsewhere = await _db.AddFolderAsync(null, "[Circle One] Elsewhere", other.Id);
        await WaitingAsync("x1", elsewhere, WorkClass.Series);
        Assert.Equal(6, (await Review().AuthorsAsync(null))!.Items[0].Count);
        Assert.Equal(5, (await Review().AuthorsAsync(_db.LibraryPublicId))!.Items[0].Count);
        Assert.Null(await Review().AuthorsAsync("nolib"));
    }
}
