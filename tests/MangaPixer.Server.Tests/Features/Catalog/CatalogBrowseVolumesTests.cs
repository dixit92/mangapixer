namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for the Volumes branch of browse (1.29.0, P2.4): keyset paging with <c>before</c> and an exact
/// TotalCount over GROUPED pages, stacks never split, the toggle chain and the paths that stay flat (other sorts, read-state
/// filter, favourites only, the Folders switch), stack cards (cover, rollup, summary), hide-empty and the Continue row.
/// </summary>
public sealed class CatalogBrowseVolumesTests : IDisposable
{
    private const int Volumes = 12;

    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public CatalogBrowseVolumesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-browsevol-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "browsevol.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>
    /// A linked series of 12 volumes (10 chapters each in the map, chapters 1-3 of each on disk), one real volume archive with
    /// no chapters (volume 13), a subfolder and a loose chapter past the last volume: 12 stacks + 1 archive + 1 folder + 1 loose.
    /// </summary>
    private async Task<(MangaPixerDbContext Db, UserEntity User, LibraryEntity Library, CatalogNodeEntity Series, CatalogBrowseService Service)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var library = await VolumeTestData.AddLibraryAsync(db);
        var user = await VolumeTestData.AddUserAsync(db);
        var series = await VolumeTestData.AddFolderAsync(db, library.Id, null, "Big Series");
        for (var v = 1; v <= Volumes; v++)
        {
            var first = (v - 1) * 10 + 1;
            await VolumeTestData.AddChaptersAsync(db, library.Id, series.Id, "Big - Chapter ", first, first + 2);
        }
        await VolumeTestData.AddArchiveAsync(db, library.Id, series.Id, "Big v13");
        await VolumeTestData.AddFolderAsync(db, library.Id, series.Id, "Side Story");
        await VolumeTestData.AddArchiveAsync(db, library.Id, series.Id, "Big - Chapter 999");
        var record = await VolumeTestData.AddRecordAsync(db, status: MetadataOriginStatus.Ongoing);
        await VolumeTestData.LinkAsync(db, series, record.Id);
        await VolumeTestData.AddMapAsync(db, record.Id, 1, null, null, VolumeMapState.Ok, VolumeMapSource.MangaDexAggregate,
            Enumerable.Range(1, Volumes).Select(v => (v, (v - 1) * 10 + 1, v * 10)).ToArray());
        return (db, user, library, series, new CatalogBrowseService(db, new LibraryAuthorizationService(db)));
    }

    private static string[] Labels(PageResponse<CatalogNodeDto> page) => page.Items.Select(n => n.DisplayName).ToArray();

    [Fact]
    public async Task FirstPage_ListsStacksInVolumeOrder_WithTheExactEntryCount()
    {
        var (db, user, lib, series, service) = await SetupAsync();
        using var _ = db;

        var page = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 5);

        Assert.Equal(15, page.TotalCount); // 12 stacks + volume 13 + the subfolder + 1 loose chapter
        Assert.True(page.HasMore);
        Assert.StartsWith("v:", page.NextCursor);
        Assert.Equal(["Vol. 1", "Vol. 2", "Vol. 3", "Vol. 4", "Vol. 5"], Labels(page));
        Assert.All(page.Items, n => Assert.Equal(CatalogNodeKind.VolumeStack, n.Kind));
        Assert.False(page.HasPrevious);
        var first = page.Items[0];
        Assert.Equal($"vs.{series.PublicId}.1", first.Id);
        Assert.Equal(series.PublicId, first.ParentId);
        Assert.Equal(lib.PublicId, first.LibraryId);
        var summary = first.VolumeStack!;
        Assert.Equal(("1", "Vol. 1", 3, 10, 7, false, VolumeStackConfidence.Exact), (summary.Key, summary.Label, summary.PresentCount, summary.ChapterCount, summary.MissingCount, summary.HasVolumeArchive, summary.Confidence));
        Assert.Equal(("1", "3"), (summary.FirstChapter, summary.LastChapter));
        // The stack cover is its first chapter's file cover (never a path).
        var chapter1 = await db.CatalogNodes.SingleAsync(n => n.DisplayName == "Big - Chapter 001");
        Assert.Equal($"/api/v1/items/{chapter1.PublicId}/cover", first.CoverUrl);
    }

    [Fact]
    public async Task ForwardPages_CoverEveryEntryOnce_AndTheTotalNeverChanges()
    {
        var (db, user, lib, series, service) = await SetupAsync();
        using var _ = db;

        var labels = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await service.BrowseAsync(user.Id, lib.Id, series.Id, cursor, pageSize: 4);
            Assert.Equal(15, page.TotalCount);
            labels.AddRange(Labels(page));
            if (!page.HasMore)
                break;
            cursor = page.NextCursor;
        }

        Assert.Equal(15, labels.Count);
        Assert.Equal(15, labels.Distinct().Count());
        Assert.Equal(["Big v13", "Side Story", "Big - Chapter 999"], labels.TakeLast(3));
        Assert.Equal(Enumerable.Range(1, Volumes).Select(v => $"Vol. {v}"), labels.Take(Volumes));
    }

    [Fact]
    public async Task APageOfOne_NeverSplitsAStack()
    {
        var (db, user, lib, series, service) = await SetupAsync();
        using var _ = db;

        var page = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 1);

        var stack = Assert.Single(page.Items);
        Assert.Equal(3, stack.VolumeStack!.PresentCount);
        Assert.Equal(15, page.TotalCount);
    }

    [Fact]
    public async Task Before_ReturnsThePagePrecedingACursor_AndAMidListCursorReportsIt()
    {
        var (db, user, lib, series, service) = await SetupAsync();
        using var _ = db;

        var first = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 5);
        var second = await service.BrowseAsync(user.Id, lib.Id, series.Id, first.NextCursor, pageSize: 5);
        Assert.Equal(["Vol. 6", "Vol. 7", "Vol. 8", "Vol. 9", "Vol. 10"], Labels(second));
        Assert.True(second.HasPrevious);
        Assert.NotNull(second.PrevCursor);
        Assert.Equal(15, second.TotalCount);

        var back = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 5, before: second.PrevCursor);
        Assert.Equal(Labels(first), Labels(back));
        Assert.False(back.HasPrevious);
        Assert.True(back.HasMore);
        Assert.Equal(15, back.TotalCount);
    }

    [Fact]
    public async Task NameDescending_ReversesTheEntries_ButNotTheMembersOfAStack()
    {
        var (db, user, lib, series, service) = await SetupAsync();
        using var _ = db;

        var page = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 4, direction: SortDirection.Descending);

        Assert.Equal(["Big - Chapter 999", "Side Story", "Big v13", "Vol. 12"], Labels(page));
        Assert.Equal(CatalogNodeKind.Archive, page.Items[0].Kind);
        Assert.Equal(CatalogNodeKind.Folder, page.Items[1].Kind);
        var next = await service.BrowseAsync(user.Id, lib.Id, series.Id, page.NextCursor, pageSize: 4, direction: SortDirection.Descending);
        Assert.Equal(["Vol. 11", "Vol. 10", "Vol. 9", "Vol. 8"], Labels(next));
    }

    [Fact]
    public async Task PlainEntries_KeepTheirNormalEnrichment()
    {
        var (db, user, lib, series, service) = await SetupAsync();
        using var _ = db;
        var volume13 = await db.CatalogNodes.SingleAsync(n => n.DisplayName == "Big v13");
        db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = volume13.Id, MarkedAt = DateTimeOffset.UtcNow });
        db.Favorites.Add(new FavoriteEntity { UserId = user.Id, CatalogNodeId = volume13.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var page = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 50);

        var card = page.Items.Single(n => n.DisplayName == "Big v13");
        Assert.True(card.IsRead);
        Assert.True(card.IsFavorite);
        Assert.Equal(12, card.PageCount);
        Assert.Equal($"/api/v1/items/{volume13.PublicId}/cover", card.CoverUrl);
        var folder = page.Items.Single(n => n.Kind == CatalogNodeKind.Folder);
        Assert.Equal("Side Story", folder.DisplayName);
        Assert.NotNull(page.NextUnread); // the Continue row still follows the real folder
    }

    [Fact]
    public async Task AStacksReadRollup_FollowsItsMembers()
    {
        var (db, user, lib, series, service) = await SetupAsync();
        using var _ = db;
        var members = await db.CatalogNodes.Where(n => n.DisplayName.StartsWith("Big - Chapter 00")).OrderBy(n => n.DisplayName).ToListAsync();
        Assert.Equal(3, members.Count); // volume 1 = chapters 1-3

        var page = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 3);
        Assert.Equal(FolderReadRollup.Unread, page.Items[0].ReadRollup);

        db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = members[0].Id, MarkedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        page = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 3);
        Assert.Equal(FolderReadRollup.Reading, page.Items[0].ReadRollup);
        Assert.Equal(FolderReadRollup.Unread, page.Items[1].ReadRollup);

        db.ReadMarks.AddRange(members.Skip(1).Select(m => new ReadMarkEntity { UserId = user.Id, ItemId = m.Id, MarkedAt = DateTimeOffset.UtcNow }));
        await db.SaveChangesAsync();
        page = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 3);
        Assert.Equal(FolderReadRollup.Read, page.Items[0].ReadRollup);
    }

    [Fact]
    public async Task TheFoldersSwitch_OtherSorts_ReadStateAndFavouritesFilters_StayFlat()
    {
        var (db, user, lib, series, service) = await SetupAsync();
        using var _ = db;

        // The explicit flat request, and the user's own Folders switch.
        var flat = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 200, group: "flat");
        Assert.Equal(39, flat.TotalCount); // 36 chapters + volume 13 + loose chapter + the subfolder
        Assert.DoesNotContain(flat.Items, n => n.Kind == CatalogNodeKind.VolumeStack);

        db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = user.Id, SeriesViewMode = (int)SeriesViewMode.Folders });
        await db.SaveChangesAsync();
        Assert.Equal(flat.TotalCount, (await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 200)).TotalCount);
        // ... and an explicit request for the Volumes view beats the stored switch.
        Assert.Equal(15, (await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 200, group: "volumes")).TotalCount);

        // Other sorts, the read-state filter and Favourites only are about single items.
        db.ReaderPreferences.Single().SeriesViewMode = (int)SeriesViewMode.Volumes;
        await db.SaveChangesAsync();
        Assert.Equal(15, (await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 200)).TotalCount);
        Assert.Equal(39, (await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 200, sort: "recentlyAdded")).TotalCount);
        Assert.Equal(39, (await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 200, readState: BrowseReadStateFilter.Unread)).TotalCount);
        Assert.Equal(0, (await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 200, favoritesOnly: true)).TotalCount);
    }

    [Fact]
    public async Task AVolumesCursor_OnTheFlatPath_IsIgnored_AndANameCursorOnTheGroupedPathToo()
    {
        var (db, user, lib, series, service) = await SetupAsync();
        using var _ = db;
        var grouped = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 3);

        var flatWithVolumesCursor = await service.BrowseAsync(user.Id, lib.Id, series.Id, grouped.NextCursor, pageSize: 3, group: "flat");
        var flatFirst = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 3, group: "flat");
        Assert.Equal(Labels(flatFirst), Labels(flatWithVolumesCursor));

        var groupedWithNameCursor = await service.BrowseAsync(user.Id, lib.Id, series.Id, "1big - chapter 010", pageSize: 3);
        Assert.Equal(Labels(grouped), Labels(groupedWithNameCursor));
    }

    [Fact]
    public async Task HideEmpty_DropsAnEmptySubfolder_FromTheGroupedList()
    {
        var (db, user, lib, series, service) = await SetupAsync();
        using var _ = db;

        var page = await service.BrowseAsync(user.Id, lib.Id, series.Id, null, pageSize: 50, hideEmpty: true);

        Assert.Equal(14, page.TotalCount); // "Side Story" holds no archive
        Assert.DoesNotContain(page.Items, n => n.Kind == CatalogNodeKind.Folder);
    }

    [Fact]
    public async Task ARootOrUngroupableFolder_BrowsesExactlyAsBefore()
    {
        var (db, user, lib, _, service) = await SetupAsync();
        using var _ = db;
        var plain = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Plain Folder");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, plain.Id, "Alpha");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, plain.Id, "Beta");

        var root = await service.BrowseAsync(user.Id, lib.Id, null, null, pageSize: 50);
        var inside = await service.BrowseAsync(user.Id, lib.Id, plain.Id, null, pageSize: 50);

        Assert.Equal(2, root.TotalCount);
        Assert.Equal(["Alpha", "Beta"], Labels(inside));
        Assert.DoesNotContain(inside.Items, n => n.Kind == CatalogNodeKind.VolumeStack);
    }
}
