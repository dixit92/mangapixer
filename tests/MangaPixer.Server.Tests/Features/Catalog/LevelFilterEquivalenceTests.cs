namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Operations.Perf;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.49.0 lane J - the level-filter pass was moved out of <c>BrowseAsync</c> into <see cref="CatalogBrowseService.ApplyLevelFiltersAsync"/>
/// so the jump index lists the rows browse lists. Equivalence: for every combination of the read state, hide empty, favorites only and
/// each Completion answer (and none), the rows the shared pass keeps are exactly the rows browse pages through (same order, same total,
/// same linked-series count, same Completion counts). Synthetic names.
/// </summary>
public sealed class LevelFilterEquivalenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public LevelFilterEquivalenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-levelfilter-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "levelfilter.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>
    /// A root level with every state a filter can tell apart: folders that roll up Read / Reading / Unread, an empty folder, loose archives
    /// read / in progress / untouched, starred folder and archive, and linked series with different Completion answers.
    /// </summary>
    internal static async Task<(MangaPixerDbContext Db, long UserId, long LibraryId)> SeedLevelAsync(DbContextOptions<MangaPixerDbContext> options)
    {
        var db = new MangaPixerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var library = await VolumeTestData.AddLibraryAsync(db);
        var user = await VolumeTestData.AddUserAsync(db);
        var lib = library.Id;

        void Mark(CatalogNodeEntity archive) =>
            db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = archive.Id, MarkedAt = DateTimeOffset.UtcNow });
        void Progress(CatalogNodeEntity archive) =>
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = user.Id,
                ItemId = archive.Id,
                ContentVersion = 1,
                Ordinal = 1,
                State = (int)ReadingState.InProgress,
                Revision = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

        var read = await VolumeTestData.AddFolderAsync(db, lib, null, "Alpha Read Folder");
        Mark(await VolumeTestData.AddArchiveAsync(db, lib, read.Id, "Alpha v01"));
        Mark(await VolumeTestData.AddArchiveAsync(db, lib, read.Id, "Alpha v02"));
        var reading = await VolumeTestData.AddFolderAsync(db, lib, null, "Bravo Reading Folder");
        Mark(await VolumeTestData.AddArchiveAsync(db, lib, reading.Id, "Bravo v01"));
        Progress(await VolumeTestData.AddArchiveAsync(db, lib, reading.Id, "Bravo v02"));
        var unread = await VolumeTestData.AddFolderAsync(db, lib, null, "Charlie Unread Folder");
        await VolumeTestData.AddArchiveAsync(db, lib, unread.Id, "Charlie v01");
        await VolumeTestData.AddFolderAsync(db, lib, null, "Delta Empty Folder");

        var starred = await VolumeTestData.AddFolderAsync(db, lib, null, "Echo Starred Folder");
        await VolumeTestData.AddArchiveAsync(db, lib, starred.Id, "Echo v01");
        db.Favorites.Add(new FavoriteEntity { UserId = user.Id, CatalogNodeId = starred.Id, CreatedAt = DateTimeOffset.UtcNow });

        // Linked series: a finished one holding every volume, an ongoing one missing volumes, a finished one missing some.
        async Task LinkedAsync(string name, MetadataOriginStatus status, int volumes, int held)
        {
            var folder = await VolumeTestData.AddFolderAsync(db, lib, null, name);
            for (var v = 1; v <= held; v++)
                await VolumeTestData.AddArchiveAsync(db, lib, folder.Id, $"{name} v{v:00}");
            var record = await VolumeTestData.AddRecordAsync(db, name + " Record", volumes, status, volumes);
            await VolumeTestData.LinkAsync(db, folder, record.Id);
        }
        await LinkedAsync("Foxtrot Whole", MetadataOriginStatus.Complete, 3, 3);
        await LinkedAsync("Golf Running", MetadataOriginStatus.Ongoing, 8, 2);
        await LinkedAsync("Hotel Partial", MetadataOriginStatus.Complete, 4, 1);

        Mark(await VolumeTestData.AddArchiveAsync(db, lib, null, "India Read File"));
        Progress(await VolumeTestData.AddArchiveAsync(db, lib, null, "Juliet Reading File"));
        await VolumeTestData.AddArchiveAsync(db, lib, null, "Kilo Unread File");
        var starredFile = await VolumeTestData.AddArchiveAsync(db, lib, null, "Lima Starred File");
        db.Favorites.Add(new FavoriteEntity { UserId = user.Id, CatalogNodeId = starredFile.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return (db, user.Id, lib);
    }

    [Fact]
    public async Task SharedLevelFilter_KeepsExactlyTheRowsBrowseLists_ForEveryFilterCombination()
    {
        var (db, userId, libraryId) = await SeedLevelAsync(_options);
        await using var dbScope = db;
        var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

        var completions = new SeriesAnswer?[]
        {
            null, SeriesAnswer.HaveItAll, SeriesAnswer.FinishedMissing, SeriesAnswer.UpToDate, SeriesAnswer.MissingSome, SeriesAnswer.CantTell,
        };
        var narrowedByCompletion = false;
        var checkedCombos = 0;
        foreach (var readState in Enum.GetValues<BrowseReadStateFilter>())
        foreach (var hideEmpty in new[] { false, true })
        foreach (var favoritesOnly in new[] { false, true })
        foreach (var completion in completions)
        {
            // Browse, page by page (3 rows a page, so cursors and the total are exercised).
            var browsed = new List<string>();
            string? cursor = null;
            while (true)
            {
                var page = await browse.BrowseAsync(userId, libraryId, null, cursor, pageSize: 3, readState: readState, hideEmpty: hideEmpty,
                    favoritesOnly: favoritesOnly, completion: completion);
                browsed.AddRange(page.Items.Select(i => i.Id));
                cursor = page.NextCursor;
                if (!page.HasMore)
                    break;
            }
            var first = await browse.BrowseAsync(userId, libraryId, null, null, pageSize: 3, readState: readState, hideEmpty: hideEmpty,
                favoritesOnly: favoritesOnly, completion: completion);

            // The shared pass.
            var level = db.CatalogNodes.Where(n => n.LibraryId == libraryId && n.ParentId == null
                && n.Availability != (int)CatalogNodeAvailability.Tombstoned);
            var result = await browse.ApplyLevelFiltersAsync(level, userId, libraryId, null,
                new LevelFilter(readState, hideEmpty, favoritesOnly, completion), continuation: false, default(PerfPhase), CancellationToken.None);
            var kept = await result.Query.OrderBy(n => n.SortKey).Select(n => n.PublicId).ToListAsync();

            var label = $"{readState}/hideEmpty={hideEmpty}/fav={favoritesOnly}/completion={completion}";
            Assert.True(kept.SequenceEqual(browsed), "rows differ for " + label);
            Assert.Equal(first.TotalCount, kept.Count);
            Assert.Equal(first.CompletionCounts, result.CompletionCounts);
            Assert.Equal(first.LinkedSeries, result.LinkedSeries.Count);
            if (completion is not null && kept.Count < 12)
                narrowedByCompletion = true;
            checkedCombos++;
        }

        Assert.Equal(4 * 2 * 2 * 6, checkedCombos);
        Assert.True(narrowedByCompletion, "a Completion answer should keep fewer rows than the whole level");
    }
}
