namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.50.0 lane D: backward (upward) paging under the date sorts. A synthetic level with ties on the date bins (the id, and under
/// recentlyUpdated the SortKey, break them - two rows even share a SortKey), folders whose newest descendant ties an archive's
/// CreatedAt, and four folders with no descendant (recentlyUpdated's trailing nulls-last bucket). From EVERY row a forward landing
/// reports hasPrevious / prevCursor, and the page before it is exactly the rows above it - including pages that straddle the
/// kind boundary (recentlyAdded) and the dated / null boundary (recentlyUpdated). Paging back to the start lists every row once.
/// </summary>
public sealed class DatePagingBackwardTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public DatePagingBackwardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-datepaging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "datepaging.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext Db, long UserId, long LibraryId)> SeedAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var library = await VolumeTestData.AddLibraryAsync(db);
        var user = await VolumeTestData.AddUserAsync(db);

        // Archives: ages in hours with ties (three at 0 h, two at 5 h, three at 24 h, two at 100 h).
        var ages = new[] { 0, 0, 0, 5, 5, 24, 24, 24, 100, 100 };
        var archives = new List<CatalogNodeEntity>();
        for (var i = 0; i < ages.Length; i++)
        {
            var node = await VolumeTestData.AddArchiveAsync(db, library.Id, null, $"Story {(char)('A' + i)}");
            node.CreatedAt = s_now.AddHours(-ages[i]);
            archives.Add(node);
        }
        // Two archives at the same time share a SortKey too: only the id orders them under recentlyUpdated.
        archives[2].SortKey = archives[1].SortKey;

        // Folders: CreatedAt ties (recentlyAdded); newest descendant ties an archive's time (recentlyUpdated interleaves them by
        // SortKey), two folders tie each other, and four have none (the null bucket, two of them with one SortKey).
        var folderAges = new[] { 10, 10, 10, 20, 20, 30, 30, 30 };
        int?[] latest = [0, 5, 5, 50, null, null, null, null];
        var folders = new List<CatalogNodeEntity>();
        for (var i = 0; i < folderAges.Length; i++)
        {
            var folder = await VolumeTestData.AddFolderAsync(db, library.Id, null, $"Series {(char)('A' + i)}");
            folder.CreatedAt = s_now.AddHours(-folderAges[i]);
            folder.LatestDescendantAddedAt = latest[i] is { } h ? s_now.AddHours(-h) : null;
            folders.Add(folder);
        }
        folders[7].SortKey = folders[6].SortKey;

        // Three archives are read: the Unread filter drops them in both directions.
        foreach (var read in new[] { archives[0], archives[4], archives[8] })
            db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = read.Id, MarkedAt = s_now });
        await db.SaveChangesAsync();
        return (db, user.Id, library.Id);
    }

    /// <summary>
    /// The whole listing one row per page: the ids in display order and, per row, the forward cursor that continues after it (null for
    /// the last row).
    /// </summary>
    private static async Task<(List<string> Ids, List<string?> After)> WalkAsync(
        CatalogBrowseService browse, long userId, long libraryId, string sort, BrowseReadStateFilter readState)
    {
        var ids = new List<string>();
        var after = new List<string?>();
        string? cursor = null;
        while (true)
        {
            var page = await browse.BrowseAsync(userId, libraryId, null, cursor, pageSize: 1, sort: sort, readState: readState);
            Assert.Single(page.Items);
            ids.Add(page.Items[0].Id);
            after.Add(page.HasMore ? page.NextCursor : null);
            if (!page.HasMore)
                return (ids, after);
            cursor = page.NextCursor;
        }
    }

    [Theory]
    [InlineData("recentlyAdded", BrowseReadStateFilter.All)]
    [InlineData("recentlyUpdated", BrowseReadStateFilter.All)]
    [InlineData("recentlyAdded", BrowseReadStateFilter.Unread)]
    [InlineData("recentlyUpdated", BrowseReadStateFilter.Unread)]
    public async Task FromEveryRow_ThePageBefore_IsExactlyTheRowsAboveIt(string sort, BrowseReadStateFilter readState)
    {
        var (db, userId, libraryId) = await SeedAsync();
        await using var dbScope = db;
        var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

        var (ids, after) = await WalkAsync(browse, userId, libraryId, sort, readState);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(readState == BrowseReadStateFilter.All ? 18 : 15, ids.Count);

        // A window loaded from the start has nothing above it.
        var first = await browse.BrowseAsync(userId, libraryId, null, null, pageSize: 3, sort: sort, readState: readState);
        Assert.False(first.HasPrevious);
        Assert.Null(first.PrevCursor);

        foreach (var pageSize in new[] { 1, 2, 3, 5 })
        {
            for (var i = 1; i < ids.Count; i++)
            {
                var landing = await browse.BrowseAsync(userId, libraryId, null, after[i - 1], pageSize: pageSize, sort: sort,
                    readState: readState);
                Assert.Equal(ids[i], landing.Items[0].Id);
                Assert.True(landing.HasPrevious, $"{sort} row {i}: a mid-list landing has rows above it");
                Assert.NotNull(landing.PrevCursor);

                var back = await browse.BrowseAsync(userId, libraryId, null, null, pageSize: pageSize, sort: sort,
                    readState: readState, before: landing.PrevCursor);
                var from = Math.Max(0, i - pageSize);
                Assert.Equal(ids.Skip(from).Take(i - from), back.Items.Select(n => n.Id));
                Assert.Equal(from > 0, back.HasPrevious);
                Assert.Equal(from > 0, back.PrevCursor is not null);
                Assert.True(back.HasMore); // the client's window sits below
                Assert.Equal(ids.Count, back.TotalCount);
            }
        }
    }

    [Theory]
    [InlineData("recentlyAdded", 1)]
    [InlineData("recentlyAdded", 4)]
    [InlineData("recentlyUpdated", 1)]
    [InlineData("recentlyUpdated", 4)]
    public async Task PagingBackFromTheLastRow_ListsEveryRowExactlyOnce(string sort, int pageSize)
    {
        var (db, userId, libraryId) = await SeedAsync();
        await using var dbScope = db;
        var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
        var (ids, after) = await WalkAsync(browse, userId, libraryId, sort, BrowseReadStateFilter.All);

        // Land on the last row, then scroll up page by page until nothing is above.
        var landing = await browse.BrowseAsync(userId, libraryId, null, after[^2], pageSize: pageSize, sort: sort);
        var collected = landing.Items.Select(n => n.Id).ToList();
        var before = landing.PrevCursor;
        var pages = 0;
        while (before is not null)
        {
            var back = await browse.BrowseAsync(userId, libraryId, null, null, pageSize: pageSize, sort: sort, before: before);
            Assert.NotEmpty(back.Items);
            collected.InsertRange(0, back.Items.Select(n => n.Id));
            before = back.HasPrevious ? back.PrevCursor : null;
            Assert.True(++pages <= ids.Count, "backward paging must end");
        }
        Assert.Equal(ids, collected);
    }

    [Theory]
    [InlineData(SortDirection.Descending)]
    [InlineData(SortDirection.Ascending)]
    public async Task RecentlyAddedBackward_HonoursBothDirections(SortDirection direction)
    {
        // Browse pins the date sorts to Descending (the label bakes the direction in); the recentlyAdded helpers honour either, like
        // the forward query, so the backward query is checked against the full-reversal order directly.
        var (db, _, libraryId) = await SeedAsync();
        await using var dbScope = db;
        var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
        IQueryable<CatalogNodeEntity> Level() => db.CatalogNodes.Where(n => n.LibraryId == libraryId && n.ParentId == null);

        var expected = await (direction == SortDirection.Descending
                ? Level().OrderBy(n => n.Kind).ThenByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
                : Level().OrderByDescending(n => n.Kind).ThenBy(n => n.CreatedAt).ThenBy(n => n.Id))
            .Select(n => new { n.PublicId, n.Kind, n.CreatedAt, n.Id })
            .ToListAsync();
        static string CursorOf(int kind, DateTimeOffset createdAt, long id) =>
            string.Create(CultureInfo.InvariantCulture, $"a:{kind}:{createdAt.UtcDateTime.ToBinary()}:{id}");

        var last = expected[^1];
        var collected = new List<string>();
        var before = CursorOf(last.Kind, last.CreatedAt, last.Id);
        while (true)
        {
            var (rows, hasPrevious) = await browse.QueryDateBackwardAsync(Level(), "recentlyAdded", before, 3, direction, default);
            collected.InsertRange(0, rows.Select(r => r.Id));
            if (!hasPrevious)
                break;
            before = CursorOf(rows[0].Kind, rows[0].CreatedAt, rows[0].InternalId);
        }
        Assert.Equal(expected.Take(expected.Count - 1).Select(e => e.PublicId), collected);
    }

    [Fact]
    public async Task AnotherSortsOrAMalformedCursor_IsIgnored_AndRecentlyReadStaysOffsetBased()
    {
        var (db, userId, libraryId) = await SeedAsync();
        await using var dbScope = db;
        var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

        foreach (var sort in new[] { "recentlyAdded", "recentlyUpdated" })
        {
            var start = await browse.BrowseAsync(userId, libraryId, null, null, pageSize: 4, sort: sort);
            var junk = sort == "recentlyAdded"
                ? new[] { "a:x:y:z", "a:1:2", "u:1:0:5:k", "1Story" }
                : ["u:1:x:5:k", "u:1:0", "a:1:0:5", "1Story"];
            foreach (var before in junk)
            {
                // Not a cursor of this sort: browse answers its first page, as before 1.50.0.
                var page = await browse.BrowseAsync(userId, libraryId, null, null, pageSize: 4, sort: sort, before: before);
                Assert.Equal(start.Items.Select(n => n.Id), page.Items.Select(n => n.Id));
                Assert.False(page.HasPrevious);
            }
            // A forward page from another sort's cursor starts at the top: nothing above.
            var other = await browse.BrowseAsync(userId, libraryId, null, sort == "recentlyAdded" ? "u:0:0:1:x" : "a:0:0:1", pageSize: 4,
                sort: sort);
            Assert.False(other.HasPrevious);
        }

        // recentlyRead pages by offset and has no rail: a later page reports nothing above, a `before` is ignored.
        var read = await browse.BrowseAsync(userId, libraryId, null, "r:3", pageSize: 4, sort: "recentlyRead");
        Assert.False(read.HasPrevious);
        Assert.Null(read.PrevCursor);
        var readBack = await browse.BrowseAsync(userId, libraryId, null, null, pageSize: 4, sort: "recentlyRead", before: "r:3");
        Assert.False(readBack.HasPrevious);
    }
}
