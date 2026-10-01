using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Home;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Features.Home;

/// <summary>
/// Service-with-DB tests for the RecentChaptersService stacking rewrite (1.12.0). Uses real
/// file-backed SQLite. Verifies top-level stacking (deep archives attribute to their top-level
/// ancestor), standalone loose archives, the per-library STACK cap, NewCount, newest-activity
/// ordering, tombstone exclusion, the recency window, home-excluded libraries, the empty state,
/// and Incognito/Private exclusion.
/// </summary>
public sealed class RecentChaptersServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public RecentChaptersServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-recent-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "recent.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // Recent seeds are anchored to "now" so they fall inside the service recency window.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private async Task<(MangaPixerDbContext db, long userId, long libAId, long libBId)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var libA = new LibraryEntity { PublicId = "recLibA", DisplayName = "Alpha Library", RootPath = "/private/alpha", CreatedAt = Now };
        var libB = new LibraryEntity { PublicId = "recLibB", DisplayName = "Beta Library", RootPath = "/private/beta", CreatedAt = Now };
        db.Libraries.AddRange(libA, libB);
        await db.SaveChangesAsync();

        var user = new UserEntity
        {
            PublicId = "recUser",
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = Now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (db, user.Id, libA.Id, libB.Id);
    }

    private static async Task<CatalogNodeEntity> AddArchiveAsync(
        MangaPixerDbContext db, long libraryId, string publicId, string displayName,
        DateTimeOffset createdAt, long? parentId = null,
        int availability = (int)CatalogNodeAvailability.Available)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = publicId,
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = displayName,
            RelativePath = "/private/" + displayName,
            PathKey = "/private/" + displayName.ToLowerInvariant() + "-" + publicId,
            SortKey = "1" + displayName,
            Availability = availability,
            CreatedAt = createdAt,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    private static async Task<CatalogNodeEntity> AddFolderAsync(
        MangaPixerDbContext db, long libraryId, string publicId, string displayName, long? parentId = null)
    {
        var node = new CatalogNodeEntity
        {
            PublicId = publicId,
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = (int)CatalogNodeKind.Folder,
            DisplayName = displayName,
            RelativePath = "/private/" + displayName,
            PathKey = "/private/" + displayName.ToLowerInvariant() + "-" + publicId,
            SortKey = "0" + displayName,
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = Now,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    [Fact]
    public async Task Stacks_ByTopLevelFolder_NewCount_LatestItem_NewestFirst()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // Series A (top level) -> Volume 1 -> two archives; the newest defines the stack.
            var seriesA = await AddFolderAsync(db, libAId, "seriesA", "Series A");
            var vol1 = await AddFolderAsync(db, libAId, "vol1", "Volume 1", parentId: seriesA.Id);
            await AddArchiveAsync(db, libAId, "a_ch1", "Ch1.cbz", Now.AddHours(-5), parentId: vol1.Id);
            var newest = await AddArchiveAsync(db, libAId, "a_ch2", "Ch2.cbz", Now.AddHours(-1), parentId: vol1.Id);

            // Series B (top level) with one recent archive, older than Series A's newest.
            var seriesB = await AddFolderAsync(db, libAId, "seriesB", "Series B");
            await AddArchiveAsync(db, libAId, "b_ch1", "BCh1.cbz", Now.AddHours(-3), parentId: seriesB.Id);

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            // Two stacks (Series A, Series B), Series A first (newest activity).
            Assert.Equal(new[] { "seriesA", "seriesB" }, alpha.Stacks.Select(s => s.Id).ToArray());

            var stackA = alpha.Stacks[0];
            Assert.True(stackA.IsFolder);
            Assert.Equal("Series A", stackA.DisplayName);
            Assert.Equal(2, stackA.NewCount);                 // both chapters attributed to Series A
            Assert.Equal("a_ch2", stackA.LatestItemId);       // newest descendant archive
            Assert.Equal("Ch2.cbz", stackA.LatestItemName);
            // Compare against the DB-stored value (the binary converter is coarser than the
            // in-memory seed's sub-tick precision).
            var newestCreatedAt = await db.CatalogNodes.Where(n => n.Id == newest.Id).Select(n => n.CreatedAt).FirstAsync();
            Assert.Equal(newestCreatedAt, stackA.LatestAddedAt);
            Assert.NotNull(stackA.CoverUrl);                  // folder cover resolved

            var stackB = alpha.Stacks[1];
            Assert.Equal("seriesB", stackB.Id);
            Assert.Equal(1, stackB.NewCount);
        }
        finally { await db.DisposeAsync(); }
    }

    private static async Task<long> AddRecordAsync(MangaPixerDbContext db, string externalId)
    {
        var record = new MetadataRecordEntity
        {
            PublicId = "rec" + externalId,
            Provider = "mangaupdates",
            ExternalId = externalId,
            Title = "Synthetic " + externalId,
            FetchedAt = Now,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        return record.Id;
    }

    private static async Task LinkAsync(MangaPixerDbContext db, CatalogNodeEntity folder, long? recordId, SeriesLinkState state)
    {
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = folder.Id,
            LibraryId = folder.LibraryId,
            State = (int)state,
            RecordId = recordId,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CategoryLibrary_StacksBySeriesFolder_NotByCategory()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // Manga (a category at the top level) holds two series folders; Manhwa holds one. 1.30.0: a card per SERIES.
            var manga = await AddFolderAsync(db, libAId, "catManga", "Manga");
            var manhwa = await AddFolderAsync(db, libAId, "catManhwa", "Manhwa");
            var alpha = await AddFolderAsync(db, libAId, "serAlpha", "Alpha Series", parentId: manga.Id);
            var beta = await AddFolderAsync(db, libAId, "serBeta", "Beta Series", parentId: manga.Id);
            var gamma = await AddFolderAsync(db, libAId, "serGamma", "Gamma Series", parentId: manhwa.Id);
            await AddArchiveAsync(db, libAId, "al1", "Alpha 1.cbz", Now.AddHours(-1), parentId: alpha.Id);
            await AddArchiveAsync(db, libAId, "al2", "Alpha 2.cbz", Now.AddHours(-2), parentId: alpha.Id);
            await AddArchiveAsync(db, libAId, "be1", "Beta 1.cbz", Now.AddHours(-3), parentId: beta.Id);
            await AddArchiveAsync(db, libAId, "ga1", "Gamma 1.cbz", Now.AddHours(-4), parentId: gamma.Id);

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var group = result.Libraries.First(g => g.LibraryId == "recLibA");
            Assert.Equal(new[] { "serAlpha", "serBeta", "serGamma" }, group.Stacks.Select(s => s.Id).ToArray());
            Assert.All(group.Stacks, s => Assert.True(s.IsFolder));
            Assert.Equal(2, group.Stacks[0].NewCount);
            Assert.Equal("al1", group.Stacks[0].LatestItemId);
            Assert.Equal("Alpha Series", group.Stacks[0].DisplayName);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task LinkedFolder_IsTheStack_HoweverDeepTheArchiveIs()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // Manga / Linked Series (own link) / Season 1 (not a generic unit name) / chapters: the link names the series.
            var manga = await AddFolderAsync(db, libAId, "lkManga", "Manga");
            var series = await AddFolderAsync(db, libAId, "lkSeries", "Linked Series", parentId: manga.Id);
            var season = await AddFolderAsync(db, libAId, "lkSeason", "Season 1", parentId: series.Id);
            await LinkAsync(db, series, await AddRecordAsync(db, "9001"), SeriesLinkState.Confirmed);
            await AddArchiveAsync(db, libAId, "lk1", "Ch 1.cbz", Now.AddHours(-1), parentId: season.Id);
            await AddArchiveAsync(db, libAId, "lk2", "Ch 2.cbz", Now.AddHours(-2), parentId: series.Id);

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var stack = Assert.Single(result.Libraries.First(g => g.LibraryId == "recLibA").Stacks);
            Assert.Equal("lkSeries", stack.Id);
            Assert.Equal(2, stack.NewCount);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task UnlinkedFolder_HoldingTheArchives_IsTheStack_AndClimbsOutOfUnitFolders()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            var manga = await AddFolderAsync(db, libAId, "ulManga", "Manga");
            // No link anywhere: the folder that holds the archives is the stack ...
            var plain = await AddFolderAsync(db, libAId, "ulPlain", "Plain Series", parentId: manga.Id);
            await AddArchiveAsync(db, libAId, "ul1", "Ch 1.cbz", Now.AddHours(-1), parentId: plain.Id);
            // ... except generic unit folders (Vol N / Chapters), which are part of the series above them.
            var vols = await AddFolderAsync(db, libAId, "ulVols", "Volumed Series", parentId: manga.Id);
            var vol1 = await AddFolderAsync(db, libAId, "ulVol1", "Vol 1", parentId: vols.Id);
            var vol2 = await AddFolderAsync(db, libAId, "ulVol2", "Volume 2", parentId: vols.Id);
            await AddArchiveAsync(db, libAId, "ul2", "V1 Ch 1.cbz", Now.AddHours(-2), parentId: vol1.Id);
            await AddArchiveAsync(db, libAId, "ul3", "V2 Ch 1.cbz", Now.AddHours(-3), parentId: vol2.Id);

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var stacks = result.Libraries.First(g => g.LibraryId == "recLibA").Stacks;
            Assert.Equal(new[] { "ulPlain", "ulVols" }, stacks.Select(s => s.Id).ToArray());
            Assert.Equal(2, stacks[1].NewCount);              // Vol 1 and Volume 2 stay ONE stack
        }
        finally { await db.DisposeAsync(); }
    }

    [Theory]
    [InlineData(SeriesLinkState.NeedsReview)]
    [InlineData(SeriesLinkState.DontMatch)]
    public async Task OnlyAConfirmedOrAutoLinkWithARecord_MakesASeriesFolder(SeriesLinkState state)
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // A review-pending / "Don't match" link on the ancestor is not a series: the holding folder is the stack.
            var shelf = await AddFolderAsync(db, libAId, "lnShelf", "Shelf");
            var series = await AddFolderAsync(db, libAId, "lnSeries", "Series", parentId: shelf.Id);
            var extras = await AddFolderAsync(db, libAId, "lnExtras", "Extras", parentId: series.Id);
            await LinkAsync(db, shelf, state == SeriesLinkState.DontMatch ? null : await AddRecordAsync(db, "9002"), state);
            await AddArchiveAsync(db, libAId, "ln1", "Extra 1.cbz", Now.AddHours(-1), parentId: extras.Id);

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var stack = Assert.Single(result.Libraries.First(g => g.LibraryId == "recLibA").Stacks);
            Assert.Equal("lnExtras", stack.Id);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task SeriesFolderStack_ReadState_IsThatOfTheSeriesNotTheCategory()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // A read series next to an unread one in the same category: each card carries its OWN rollup.
            var manga = await AddFolderAsync(db, libAId, "rsManga", "Manga");
            var doneSeries = await AddFolderAsync(db, libAId, "rsDone", "Done Series", parentId: manga.Id);
            var freshSeries = await AddFolderAsync(db, libAId, "rsFresh", "Fresh Series", parentId: manga.Id);
            var done = await AddArchiveAsync(db, libAId, "rs1", "Done 1.cbz", Now.AddHours(-1), parentId: doneSeries.Id);
            await AddArchiveAsync(db, libAId, "rs2", "Fresh 1.cbz", Now.AddHours(-2), parentId: freshSeries.Id);
            db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = done.Id, MarkedAt = Now });
            await db.SaveChangesAsync();

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var all = await service.GetRecentChaptersAsync(userId);
            var stacks = all.Libraries.First(g => g.LibraryId == "recLibA").Stacks;
            Assert.Equal("read", stacks.Single(s => s.Id == "rsDone").ReadState);
            Assert.Equal("unread", stacks.Single(s => s.Id == "rsFresh").ReadState);

            var unreadOnly = await service.GetRecentChaptersAsync(userId, readState: HomeReadStateFilter.Unread);
            Assert.Equal(new[] { "rsFresh" }, unreadOnly.Libraries.First(g => g.LibraryId == "recLibA").Stacks.Select(s => s.Id).ToArray());
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task StandaloneLooseArchive_IsOwnStack_NotAFolder()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            var loose = await AddArchiveAsync(db, libAId, "loose1", "Loose.cbz", Now.AddHours(-2));

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            var stack = Assert.Single(alpha.Stacks);
            Assert.False(stack.IsFolder);
            Assert.Equal("loose1", stack.Id);
            Assert.Equal("loose1", stack.LatestItemId);       // Id == LatestItemId for a loose archive
            Assert.Equal("Loose.cbz", stack.DisplayName);
            Assert.Equal(1, stack.NewCount);
            Assert.Equal($"/api/v1/items/{loose.PublicId}/cover", stack.CoverUrl);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task PerLibrary_Caps_Stacks_NotArchives()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // 5 top-level folders, each with 3 recent archives (15 archives, 5 stacks).
            for (var s = 0; s < 5; s++)
            {
                var folder = await AddFolderAsync(db, libAId, "s" + s, "Series " + s);
                for (var c = 0; c < 3; c++)
                    await AddArchiveAsync(db, libAId, $"s{s}c{c}", $"Ch{c}.cbz", Now.AddHours(-(s * 10 + c)), parentId: folder.Id);
            }

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId, perLibrary: 2);

            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            // Cap is on STACKS: 2 stacks, each still reports its full NewCount of 3.
            Assert.Equal(2, alpha.Stacks.Count);
            Assert.All(alpha.Stacks, st => Assert.Equal(3, st.NewCount));
            // Newest-activity ordering: Series 0 then Series 1 (lower index = more recent seed).
            Assert.Equal(new[] { "s0", "s1" }, alpha.Stacks.Select(st => st.Id).ToArray());
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ExcludesTombstoned_And_OutsideWindow()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            var series = await AddFolderAsync(db, libAId, "seriesA", "Series A");
            await AddArchiveAsync(db, libAId, "live", "Live.cbz", Now.AddHours(-1), parentId: series.Id);
            await AddArchiveAsync(db, libAId, "tomb", "Tomb.cbz", Now.AddHours(-2), parentId: series.Id,
                availability: (int)CatalogNodeAvailability.Tombstoned);
            // An archive older than the recency window must not count.
            await AddArchiveAsync(db, libAId, "old", "Old.cbz", Now - RecentChaptersService.RecentWindow - TimeSpan.FromDays(1), parentId: series.Id);

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            var stack = Assert.Single(alpha.Stacks);
            Assert.Equal(1, stack.NewCount);                 // only the live, in-window archive
            Assert.Equal("live", stack.LatestItemId);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task EmptyState_WhenNoRecentArchives()
    {
        var (db, userId, _, _) = await SetupAsync();
        try
        {
            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            Assert.Equal(2, result.Libraries.Count);
            Assert.All(result.Libraries, g => Assert.Empty(g.Stacks));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task HomeExcludedLibraries_AreDropped()
    {
        var (db, userId, libAId, libBId) = await SetupAsync();
        try
        {
            await AddArchiveAsync(db, libAId, "a1", "A.cbz", Now.AddHours(-1));
            await AddArchiveAsync(db, libBId, "b1", "B.cbz", Now.AddHours(-1));

            db.HomeExcludedLibraries.Add(new HomeExcludedLibraryEntity { UserId = userId, LibraryId = libBId, MarkedAt = Now });
            await db.SaveChangesAsync();

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            // Beta is hidden from home entirely (no group at all).
            Assert.Single(result.Libraries);
            Assert.Equal("recLibA", result.Libraries[0].LibraryId);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task PerUserWindow_NarrowsToStoredDays()
    {
        // A 10-day-old archive is inside the 30-day default but outside a 7-day per-user window.
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            var series = await AddFolderAsync(db, libAId, "seriesA", "Series A");
            await AddArchiveAsync(db, libAId, "recentArch", "Recent.cbz", Now.AddDays(-2), parentId: series.Id);
            await AddArchiveAsync(db, libAId, "tenDayArch", "TenDay.cbz", Now.AddDays(-10), parentId: series.Id);

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

            // Default (unset) window: 30 days — both archives count.
            var defaultResult = await service.GetRecentChaptersAsync(userId);
            var defaultStack = Assert.Single(defaultResult.Libraries.First(g => g.LibraryId == "recLibA").Stacks);
            Assert.Equal(2, defaultStack.NewCount);

            // Per-user 7-day window — only the 2-day-old archive counts.
            db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = userId, HomeRecentWindowDays = 7 });
            await db.SaveChangesAsync();

            var narrowResult = await service.GetRecentChaptersAsync(userId);
            var narrowStack = Assert.Single(narrowResult.Libraries.First(g => g.LibraryId == "recLibA").Stacks);
            Assert.Equal(1, narrowStack.NewCount);
            Assert.Equal("recentArch", narrowStack.LatestItemId);
        }
        finally { await db.DisposeAsync(); }
    }

    [Theory]
    [InlineData(0, 30)]     // unset -> default
    [InlineData(-5, 1)]     // a stray negative (should not occur via the UI) clamps to the floor
    [InlineData(1, 1)]      // already at the range floor
    [InlineData(500, 365)]  // above range clamps to the ceiling
    public async Task PerUserWindow_IsClampedToSaneRange(int stored, int expectedEffectiveDays)
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // One archive just inside the expected effective window, one just outside it.
            await AddArchiveAsync(db, libAId, "inside", "Inside.cbz", Now.AddDays(-(expectedEffectiveDays - 0.5)));
            await AddArchiveAsync(db, libAId, "outside", "Outside.cbz", Now.AddDays(-(expectedEffectiveDays + 0.5)));

            db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = userId, HomeRecentWindowDays = stored });
            await db.SaveChangesAsync();

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));
            var result = await service.GetRecentChaptersAsync(userId);

            var alpha = result.Libraries.First(g => g.LibraryId == "recLibA");
            var stack = Assert.Single(alpha.Stacks);
            Assert.Equal("inside", stack.LatestItemId);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ReadStateFilter_RestrictsStacksToMatchingTopLevelRollup()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // Series A: single recent archive, sticky read-mark -> folder rollup = Read.
            var seriesA = await AddFolderAsync(db, libAId, "seriesA", "Series A");
            var a1 = await AddArchiveAsync(db, libAId, "a1", "A1.cbz", Now.AddHours(-1), parentId: seriesA.Id);
            db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = a1.Id, MarkedAt = Now });

            // Series B: single recent archive, in-progress (no read-mark) -> rollup = Reading.
            var seriesB = await AddFolderAsync(db, libAId, "seriesB", "Series B");
            var b1 = await AddArchiveAsync(db, libAId, "b1", "B1.cbz", Now.AddHours(-2), parentId: seriesB.Id);
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = userId,
                ItemId = b1.Id,
                State = (int)ReadingState.InProgress,
                EntryKey = "e1",
                LastMutationId = "m1",
                UpdatedAt = Now,
            });

            // Loose top-level archive, untouched -> standalone rollup = Unread.
            await AddArchiveAsync(db, libAId, "loose1", "Loose.cbz", Now.AddHours(-3));

            await db.SaveChangesAsync();

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

            var read = await service.GetRecentChaptersAsync(userId, readState: HomeReadStateFilter.Read);
            var readStacks = read.Libraries.First(g => g.LibraryId == "recLibA").Stacks;
            Assert.Equal(new[] { "seriesA" }, readStacks.Select(s => s.Id).ToArray());

            var reading = await service.GetRecentChaptersAsync(userId, readState: HomeReadStateFilter.Reading);
            var readingStacks = reading.Libraries.First(g => g.LibraryId == "recLibA").Stacks;
            Assert.Equal(new[] { "seriesB" }, readingStacks.Select(s => s.Id).ToArray());

            var unread = await service.GetRecentChaptersAsync(userId, readState: HomeReadStateFilter.Unread);
            var unreadStacks = unread.Libraries.First(g => g.LibraryId == "recLibA").Stacks;
            Assert.Equal(new[] { "loose1" }, unreadStacks.Select(s => s.Id).ToArray());

            // All (default): every stack, unfiltered, NewCount unaffected by the read state.
            var all = await service.GetRecentChaptersAsync(userId);
            var allStacks = all.Libraries.First(g => g.LibraryId == "recLibA").Stacks;
            Assert.Equal(3, allStacks.Count);
            Assert.All(allStacks, st => Assert.Equal(1, st.NewCount));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ReadState_TaggedPerStack_ReadReadingUnread_LooseArchiveIncluded()
    {
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            // Series A: single recent archive, sticky read-mark -> ReadState = "read".
            var seriesA = await AddFolderAsync(db, libAId, "seriesA", "Series A");
            var a1 = await AddArchiveAsync(db, libAId, "a1", "A1.cbz", Now.AddHours(-1), parentId: seriesA.Id);
            db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = a1.Id, MarkedAt = Now });

            // Series B: single recent archive, in-progress (no read-mark) -> ReadState = "reading".
            var seriesB = await AddFolderAsync(db, libAId, "seriesB", "Series B");
            var b1 = await AddArchiveAsync(db, libAId, "b1", "B1.cbz", Now.AddHours(-2), parentId: seriesB.Id);
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = userId,
                ItemId = b1.Id,
                State = (int)ReadingState.InProgress,
                EntryKey = "e1",
                LastMutationId = "m1",
                UpdatedAt = Now,
            });

            // Loose top-level archive, untouched -> a "loose archive" stack (IsFolder = false)
            // whose own progress/read-mark (none here) rolls up to ReadState = "unread".
            await AddArchiveAsync(db, libAId, "loose1", "Loose.cbz", Now.AddHours(-3));

            await db.SaveChangesAsync();

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

            // Unfiltered (All): every stack still appears, each tagged with its own rollup.
            var all = await service.GetRecentChaptersAsync(userId);
            var stacks = all.Libraries.First(g => g.LibraryId == "recLibA").Stacks;
            Assert.Equal(3, stacks.Count);

            var stackA = stacks.Single(s => s.Id == "seriesA");
            Assert.Equal("read", stackA.ReadState);

            var stackB = stacks.Single(s => s.Id == "seriesB");
            Assert.Equal("reading", stackB.ReadState);

            var looseStack = stacks.Single(s => s.Id == "loose1");
            Assert.False(looseStack.IsFolder);
            Assert.Equal("unread", looseStack.ReadState);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ReadState_AgreesWithReadStateFilter_ForEveryReturnedStack()
    {
        // The per-stack ReadState tag and the readState filter both derive from the same
        // rollup resolution; a stack returned under a given filter must carry the matching
        // ReadState string, and vice versa (no stack of another state leaks through).
        var (db, userId, libAId, _) = await SetupAsync();
        try
        {
            var seriesA = await AddFolderAsync(db, libAId, "seriesA", "Series A");
            var a1 = await AddArchiveAsync(db, libAId, "a1", "A1.cbz", Now.AddHours(-1), parentId: seriesA.Id);
            db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = a1.Id, MarkedAt = Now });

            var seriesB = await AddFolderAsync(db, libAId, "seriesB", "Series B");
            var b1 = await AddArchiveAsync(db, libAId, "b1", "B1.cbz", Now.AddHours(-2), parentId: seriesB.Id);
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = userId,
                ItemId = b1.Id,
                State = (int)ReadingState.InProgress,
                EntryKey = "e1",
                LastMutationId = "m1",
                UpdatedAt = Now,
            });

            await AddArchiveAsync(db, libAId, "loose1", "Loose.cbz", Now.AddHours(-3));
            await db.SaveChangesAsync();

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

            foreach (var (filter, expectedReadState) in new[]
                     {
                         (HomeReadStateFilter.Read, "read"),
                         (HomeReadStateFilter.Reading, "reading"),
                         (HomeReadStateFilter.Unread, "unread"),
                     })
            {
                var result = await service.GetRecentChaptersAsync(userId, readState: filter);
                var libStacks = result.Libraries.First(g => g.LibraryId == "recLibA").Stacks;
                Assert.NotEmpty(libStacks);
                Assert.All(libStacks, s => Assert.Equal(expectedReadState, s.ReadState));
            }
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Incognito_ExcludesPrivateLibrary()
    {
        var (db, userId, libAId, libBId) = await SetupAsync();
        try
        {
            await AddArchiveAsync(db, libAId, "a1", "A.cbz", Now.AddHours(-1));
            await AddArchiveAsync(db, libBId, "b1", "B.cbz", Now.AddHours(-1));

            db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = userId, LibraryId = libBId, MarkedAt = Now });
            await db.SaveChangesAsync();

            var service = new RecentChaptersService(db, new LibraryAuthorizationService(db));

            var normal = await service.GetRecentChaptersAsync(userId, incognito: false);
            Assert.Equal(new[] { "recLibA", "recLibB" }, normal.Libraries.Select(g => g.LibraryId).ToArray());

            var incog = await service.GetRecentChaptersAsync(userId, incognito: true);
            Assert.Single(incog.Libraries);
            Assert.Equal("recLibA", incog.Libraries[0].LibraryId);
        }
        finally { await db.DisposeAsync(); }
    }
}
