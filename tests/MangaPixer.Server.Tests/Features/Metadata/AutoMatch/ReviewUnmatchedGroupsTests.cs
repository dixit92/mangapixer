namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.Review;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of the "Same author" and "Same folder" groups on the Unmatched tab (1.34.0): the same index as Needs review,
/// built from the Unmatched queue rows - hints on the rows, the author / folder filters with the cursor, the Authors list
/// (<c>tab=Unmatched</c>), and the two tabs staying apart (a waiting work never joins an Unmatched group). Nothing is sent.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class ReviewUnmatchedGroupsTests : IAsyncLifetime
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
        await UnmatchedArchiveAsync("a1", _folderA, "[Circle One (Artist A)] Story One.cbz");
        await UnmatchedArchiveAsync("a2", _folderA, "Circle One] Story Two.cbz");
        await UnmatchedArchiveAsync("a3", _folderA, "[English] Story Three.cbz"); // a release tag: no author
        await UnmatchedArchiveAsync("b1", _folderB, "[Artist A] Story Five.cbz");
        await UnmatchedArchiveAsync("b2", _folderB, "Plain Story Six.cbz");
        // A failed work is listed in Unmatched too.
        await UnmatchedArchiveAsync("b3", _folderB, "[Artist A] Story Seven.cbz", QueueState.Failed);
        // A work waiting in Needs review never joins an Unmatched group (and the other way round).
        var waiting = await _db.AddArchiveAsync(_folderB, "[Artist A] Story Eight.cbz");
        _n["w1"] = waiting;
        await _db.AddLinkAsync(waiting, null, SeriesLinkState.NeedsReview);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private async Task UnmatchedArchiveAsync(string tag, CatalogNodeEntity folder, string name, QueueState state = QueueState.Done)
    {
        var archive = await _db.AddArchiveAsync(folder, name);
        _n[tag] = archive;
        _db.Db.MetadataMatchQueue.Add(new MetadataMatchQueueEntity
        {
            NodeId = archive.Id,
            LibraryId = archive.LibraryId,
            State = state,
            Outcome = (int)MatchBand.Unmatched,
            WorkClass = (int)WorkClass.CollectionLeaf,
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

    private async Task<string[]> TagsAsync(int limit = 100, string? author = null, string? folder = null)
    {
        var tags = new List<string>();
        string? cursor = null;
        for (var guard = 0; guard < 20; guard++)
        {
            var (error, page) = await Review().ListAsync(MetadataReviewTab.Unmatched, null, cursor, limit, author: author, folder: folder);
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
    public async Task Rows_CarryTheSameAuthorAndSameFolderHints_FromTheUnmatchedWorksOnly()
    {
        var (error, page) = await Review().ListAsync(MetadataReviewTab.Unmatched, null, null, 100);
        Assert.Null(error);
        var items = page!.Items.ToDictionary(i => TagOf(i.NodeId));
        Assert.Equal(6, items.Count);

        // a1 [Circle One (Artist A)] + a2 (Circle One]) + b1 ([Artist A]) + b3 (failed, [Artist A]): four works, one group; the waiting
        // work w1 by the same artist is not counted.
        foreach (var tag in new[] { "a1", "a2", "b1", "b3" })
            Assert.Equal(("artista", "Artist A", 3), (items[tag].SameAuthor!.Key, items[tag].SameAuthor!.Label, items[tag].SameAuthor!.Others));
        Assert.Null(items["a3"].SameAuthor);
        Assert.Null(items["b2"].SameAuthor);

        // Folder B holds three Unmatched works (and one waiting work, not counted); folder A three.
        Assert.Equal((_folderB.PublicId, "Doujins B", 2), (items["b1"].SameFolder!.Key, items["b1"].SameFolder!.Label, items["b1"].SameFolder!.Others));
        Assert.Equal((_folderA.PublicId, 2), (items["a3"].SameFolder!.Key, items["a3"].SameFolder!.Others));
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Filters_ListTheGroup_WithTheCursor_AndNotBoth()
    {
        string[] group = ["a1", "a2", "b1", "b3"];
        Assert.Equal(group, await TagsAsync(author: "artista"));
        Assert.Equal(group, await TagsAsync(author: "circleone")); // another name of the same group
        for (var limit = 1; limit <= 4; limit++)
            Assert.Equal(group, await TagsAsync(limit, author: "artista"));
        var (_, page) = await Review().ListAsync(MetadataReviewTab.Unmatched, null, null, 2, author: "artista");
        Assert.Equal((4, true), (page!.Total, page.HasMore));

        Assert.Equal(["b1", "b2", "b3"], await TagsAsync(folder: _folderB.PublicId));
        Assert.Equal(["b1", "b2", "b3"], await TagsAsync(1, folder: _folderB.PublicId));
        Assert.Empty(await TagsAsync(author: "nobodyknown"));
        Assert.Empty(await TagsAsync(folder: "nope"));
        var (error, _) = await Review().ListAsync(MetadataReviewTab.Unmatched, null, null, 10, author: "artista", folder: _folderB.PublicId);
        Assert.Equal("invalid_filter", error);
    }

    [Fact]
    public async Task AuthorsList_ForUnmatched_CountsTheUnmatchedWorks_AndNeedsReviewKeepsItsOwn()
    {
        var unmatched = (await Review().AuthorsAsync(null, tab: MetadataReviewTab.Unmatched))!.Items;
        Assert.Equal([("artista", "Artist A", 4, 0)], unmatched.Select(a => (a.Key, a.Label, a.Count, a.Later)));
        // Needs review has only the one waiting work: no group of two.
        Assert.Empty((await Review().AuthorsAsync(null))!.Items);
        Assert.Null(await Review().AuthorsAsync("nolib", tab: MetadataReviewTab.Unmatched));
    }
}
