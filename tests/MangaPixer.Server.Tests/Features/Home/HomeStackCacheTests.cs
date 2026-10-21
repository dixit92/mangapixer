using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Home;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Features.Home;

/// <summary>
/// Service-with-DB tests for the 1.46.0 lane HC home stack cache (<see cref="HomeStackCache"/>): the home "New chapters" row served from
/// a kept, user-independent stack source per library is the same answer as the row built from the database every time.
/// <list type="bullet">
/// <item><description>Equivalence (real scans on temp folders) of the old service (<see cref="RecentChaptersReference"/>) and the new one
/// without and with the kept sources: three viewers (admin; a reader with one grant; a reader with two grants, a
/// Private library and a 7-day window), Incognito off / on, every read-state filter and two caps, after each of: the first scan, a new
/// archive, a folder rename, a move between series folders, a move to another library, a series-link change, a cover change.</description></item>
/// <item><description>Invalidation, one test per input of the stamp (catalog revision, library creation time, latest scan run, a scan in
/// progress, the linked folders, the window cut-off going back, the age bound): each first shows the kept source is really reused while
/// nothing moved (the answer is stale on purpose - the stamp is the contract), then that moving that input alone gives the fresh answer
/// again. A stack ageing out of the window leaves on time from the kept source, without a rebuild; the viewer's read marks and progress
/// are read per request from the kept source (the stack's archives are kept, which of them the viewer read is not).</description></item>
/// </list>
/// </summary>
public sealed class HomeStackCacheTests : IDisposable
{
    private readonly MoveTestHarness _h = new();
    private readonly HomeStackCache _cache = new();
    private readonly Clock _clock = new(DateTimeOffset.UtcNow);
    private int _serial;

    public void Dispose() => _h.Dispose();

    // ---------------------------------------------------------------- equivalence through real scans

    [Fact]
    public async Task EveryViewer_EveryChange_TheKeptSourceGivesTheSameRows()
    {
        await _h.InitAsync();
        var alpha = await _h.AddLibraryAsync("Alpha");
        var beta = await _h.AddLibraryAsync("Beta");
        var admin = await _h.AddUserAsync("admin", admin: true);
        var oneGrant = await _h.AddUserAsync("reader1");
        var twoGrants = await _h.AddUserAsync("reader2");
        await using (var db = _h.NewContext())
        {
            var now = DateTimeOffset.UtcNow;
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = oneGrant, LibraryId = alpha, GrantedAt = now });
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = twoGrants, LibraryId = alpha, GrantedAt = now });
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = twoGrants, LibraryId = beta, GrantedAt = now });
            db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = twoGrants, LibraryId = beta, MarkedAt = now });
            db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = twoGrants, HomeRecentWindowDays = 7 });
            await db.SaveChangesAsync();
        }
        long[] viewers = [admin, oneGrant, twoGrants];

        _h.WriteArchive(alpha, "Manga/Series One/Vol 1/c001.cbz", 1);
        _h.WriteArchive(alpha, "Manga/Series One/Vol 2/c002.cbz", 2);
        _h.WriteArchive(alpha, "Manga/Series Two/c001.cbz", 3);
        _h.WriteArchive(alpha, "Manga/Series Two/c002.cbz", 4);
        _h.WriteArchive(alpha, "Series Three/c001.cbz", 5);
        _h.WriteArchive(alpha, "Loose One.cbz", 6);
        _h.WriteArchive(beta, "Series Four/c001.cbz", 7);
        _h.WriteArchive(beta, "Series Five/c001.cbz", 8);
        await _h.ScanAsync(alpha);
        await _h.ScanAsync(beta);
        _clock.Now = DateTimeOffset.UtcNow;

        // Per-viewer read state, so the rollups differ by viewer.
        await using (var db = _h.NewContext())
        {
            foreach (var id in await db.CatalogNodes.Where(n => n.LibraryId == alpha && n.Kind == 1 && n.RelativePath.Contains("Series Two"))
                         .Select(n => n.Id).ToListAsync())
                db.ReadMarks.Add(new ReadMarkEntity { UserId = oneGrant, ItemId = id, MarkedAt = DateTimeOffset.UtcNow });
            var volume1 = await db.CatalogNodes.SingleAsync(n => n.LibraryId == alpha && n.RelativePath.EndsWith("c001.cbz") && n.RelativePath.Contains("Vol 1"));
            db.ReadingProgress.Add(InProgress(twoGrants, volume1.Id));
            db.ReadMarks.Add(new ReadMarkEntity { UserId = admin, ItemId = volume1.Id, MarkedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await AssertSameForAllAsync(viewers, "first scan");
        var first = await AskDtoAsync(cached: true, admin);
        Assert.Equal(new[] { "Alpha", "Beta" }, first.Libraries.Select(l => l.LibraryName).ToArray());
        Assert.Equal(4, first.Libraries[0].Stacks.Count); // Series One (through its unit folders), Series Two, Series Three, the loose archive
        Assert.Single((await AskDtoAsync(cached: true, oneGrant)).Libraries);
        Assert.Single((await AskDtoAsync(cached: true, twoGrants, incognito: true)).Libraries);
        Assert.True(_cache.Hits > 0, "the kept sources were reused within the step");

        _h.WriteArchive(alpha, "Manga/Series Two/c003.cbz", 9);
        await _h.ScanAsync(alpha);
        await AssertSameForAllAsync(viewers, "a new archive");

        _h.Move(alpha, "Series Three", alpha, "Series Three Renamed");
        await _h.ScanAsync(alpha);
        await AssertSameForAllAsync(viewers, "a folder rename");

        _h.Move(alpha, "Manga/Series Two/c003.cbz", alpha, "Series Three Renamed/c003.cbz");
        await _h.ScanAsync(alpha);
        await AssertSameForAllAsync(viewers, "a move between series folders");

        _h.Move(alpha, "Loose One.cbz", beta, "Series Four/Loose One.cbz");
        await _h.ScanAsync(alpha);
        await _h.ScanAsync(beta);
        await AssertSameForAllAsync(viewers, "a move to another library");

        // A series link on the category folder: everything below it stacks on it; Don't match puts it back.
        var manga = await _h.NodeAsync(alpha, "Manga");
        var record = await AddRecordAsync();
        await using (var db = _h.NewContext())
        {
            db.NodeSeriesLinks.Add(Link(manga, record, SeriesLinkState.Confirmed));
            await db.SaveChangesAsync();
        }
        await AssertSameForAllAsync(viewers, "a series link added");
        Assert.Contains((await AskDtoAsync(cached: true, admin)).Libraries[0].Stacks, s => s.Id == manga.PublicId);
        await using (var db = _h.NewContext())
            await db.NodeSeriesLinks.Where(l => l.NodeId == manga.Id).ExecuteUpdateAsync(u => u.SetProperty(l => l.State, (int)SeriesLinkState.DontMatch));
        await AssertSameForAllAsync(viewers, "a series link set to Don't match");

        // A cover change (the admin picks another archive's cover for a series folder): covers are resolved per request.
        var seriesOne = await _h.NodeAsync(alpha, "Manga/Series One");
        var volume2 = await _h.NodeAsync(alpha, "Manga/Series One/Vol 2/c002.cbz");
        var coverBefore = (await AskDtoAsync(cached: true, admin)).Libraries[0].Stacks.Single(s => s.Id == seriesOne.PublicId).CoverUrl;
        await using (var db = _h.NewContext())
        {
            db.NodeCoverChoices.Add(new NodeCoverChoiceEntity
            {
                NodeId = seriesOne.Id,
                Mode = (int)CoverChoiceMode.Archive,
                ArchiveNodeId = volume2.Id,
                Version = 1,
                SetAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await AssertSameForAllAsync(viewers, "a cover change");
        Assert.NotEqual(coverBefore, (await AskDtoAsync(cached: true, admin)).Libraries[0].Stacks.Single(s => s.Id == seriesOne.PublicId).CoverUrl);
    }

    // ---------------------------------------------------------------- invalidation, one input at a time

    [Fact]
    public async Task WhileNoInputMoves_TheKeptSourceIsReused()
    {
        var (library, admin) = await SeedOneLibraryAsync();
        var kept = await AskAsync(cached: true, admin);
        await AddArchiveRowAsync(library, "Unannounced", _clock.Now.AddMinutes(-1));

        // A catalog row written without raising the revision (no writer does that): the kept source does not see it.
        Assert.NotEqual(kept, await AskAsync(cached: false, admin));
        Assert.Equal(kept, await AskAsync(cached: true, admin));
        Assert.Equal(1, _cache.Builds);
    }

    [Fact]
    public async Task TheCatalogRevision_RebuildsTheSource()
    {
        var (library, admin) = await SeedOneLibraryAsync();
        await AskAsync(cached: true, admin);
        await AddArchiveRowAsync(library, "New chapter", _clock.Now.AddMinutes(-1));
        await using (var db = _h.NewContext())
            await db.Libraries.Where(l => l.Id == library).ExecuteUpdateAsync(u => u.SetProperty(l => l.CatalogRevision, l => l.CatalogRevision + 1));

        await AssertFreshAsync(admin, "New chapter");
    }

    [Fact]
    public async Task TheLibraryCreationTime_RebuildsTheSource()
    {
        var (library, admin) = await SeedOneLibraryAsync();
        await AskAsync(cached: true, admin);
        await AddArchiveRowAsync(library, "Recreated", _clock.Now.AddMinutes(-1));
        await using (var db = _h.NewContext())
            await db.Libraries.Where(l => l.Id == library).ExecuteUpdateAsync(u => u.SetProperty(l => l.CreatedAt, DateTimeOffset.UtcNow.AddSeconds(1)));

        await AssertFreshAsync(admin, "Recreated");
    }

    [Fact]
    public async Task ANewScanRun_RebuildsTheSource()
    {
        var (library, admin) = await SeedOneLibraryAsync();
        await AskAsync(cached: true, admin);
        await AddArchiveRowAsync(library, "Scanned", _clock.Now.AddMinutes(-1));
        await AddScanRunAsync(library, status: 2);

        await AssertFreshAsync(admin, "Scanned");
    }

    [Theory]
    [InlineData(0)] // pending
    [InlineData(1)] // running
    public async Task WhileAScanIsPendingOrRunning_NothingIsReusedOrKept(int status)
    {
        var (library, admin) = await SeedOneLibraryAsync();
        await AskAsync(cached: true, admin);
        var run = await AddScanRunAsync(library, status);

        await AddArchiveRowAsync(library, "Mid-scan one", _clock.Now.AddMinutes(-1));
        await AssertFreshAsync(admin, "Mid-scan one");
        await AddArchiveRowAsync(library, "Mid-scan two", _clock.Now.AddMinutes(-1));
        await AssertFreshAsync(admin, "Mid-scan two");

        // The scan ends (same run, no revision raised): what was answered during it was never kept, so the next answer is fresh too.
        await using (var db = _h.NewContext())
            await db.ScanRuns.Where(r => r.Id == run).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, 2));
        await AddArchiveRowAsync(library, "After the scan", _clock.Now.AddMinutes(-1));
        await AssertFreshAsync(admin, "After the scan");
    }

    [Theory]
    [InlineData(SeriesLinkState.Confirmed, true)]
    [InlineData(SeriesLinkState.Auto, true)]
    [InlineData(SeriesLinkState.CollectionAbout, true)]
    [InlineData(SeriesLinkState.Confirmed, false)] // a row without a record does not stack
    [InlineData(SeriesLinkState.NeedsReview, true)]
    [InlineData(SeriesLinkState.DontMatch, true)]
    public async Task ALinkOnAFolderAbove_IsAnInputExactly(SeriesLinkState state, bool withRecord)
    {
        var (library, admin) = await SeedOneLibraryAsync();
        var category = await _h.NodeAsync(library, "Manga");
        await AskAsync(cached: true, admin);
        var record = withRecord ? await AddRecordAsync() : (long?)null;
        await using (var db = _h.NewContext())
        {
            db.NodeSeriesLinks.Add(Link(category, record, state));
            await db.SaveChangesAsync();
        }

        var fresh = await AskAsync(cached: false, admin);
        Assert.Equal(fresh, await AskAsync(cached: true, admin));
        var stacksOnTheCategory = (await AskDtoAsync(cached: true, admin)).Libraries[0].Stacks.Any(s => s.Id == category.PublicId);
        var counts = withRecord && state is SeriesLinkState.Confirmed or SeriesLinkState.Auto or SeriesLinkState.CollectionAbout;
        Assert.Equal(counts, stacksOnTheCategory);

        // And back: the link removed.
        await using (var db = _h.NewContext())
            await db.NodeSeriesLinks.Where(l => l.NodeId == category.Id).ExecuteDeleteAsync();
        Assert.Equal(await AskAsync(cached: false, admin), await AskAsync(cached: true, admin));
    }

    [Fact]
    public async Task ReadMarksAndProgress_AreReadPerRequest_FromTheKeptSource()
    {
        var (library, admin) = await SeedOneLibraryAsync();
        var series = await _h.NodeAsync(library, "Manga/Series Two");
        var chapter = await _h.NodeAsync(library, "Manga/Series Two/c001.cbz");
        string StateOf(RecentChaptersDto dto) => dto.Libraries[0].Stacks.Single(s => s.Id == series.PublicId).ReadState;
        Assert.Equal("unread", StateOf(await AskDtoAsync(cached: true, admin)));
        var builds = _cache.Builds;

        await using (var db = _h.NewContext())
        {
            db.ReadingProgress.Add(InProgress(admin, chapter.Id));
            await db.SaveChangesAsync();
        }
        Assert.Equal(await AskAsync(cached: false, admin), await AskAsync(cached: true, admin));
        Assert.Equal("reading", StateOf(await AskDtoAsync(cached: true, admin)));

        await using (var db = _h.NewContext())
        {
            db.ReadMarks.Add(new ReadMarkEntity { UserId = admin, ItemId = chapter.Id, MarkedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.Equal(await AskAsync(cached: false, admin), await AskAsync(cached: true, admin));
        Assert.Equal("read", StateOf(await AskDtoAsync(cached: true, admin)));
        Assert.Equal(builds, _cache.Builds); // the same kept source throughout: only the viewer's marks were read again
    }

    [Fact]
    public async Task AnArchiveAddedBelowAShownStack_CountsInItsRollup_AfterTheRevisionMoves()
    {
        var (library, admin) = await SeedOneLibraryAsync();
        var series = await _h.NodeAsync(library, "Manga/Series Two");
        var chapter = await _h.NodeAsync(library, "Manga/Series Two/c001.cbz");
        await using (var db = _h.NewContext())
        {
            db.ReadMarks.Add(new ReadMarkEntity { UserId = admin, ItemId = chapter.Id, MarkedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        string StateOf(RecentChaptersDto dto) => dto.Libraries[0].Stacks.Single(s => s.Id == series.PublicId).ReadState;
        Assert.Equal("read", StateOf(await AskDtoAsync(cached: true, admin)));

        // An old chapter (outside the window) appears in the series: the stack is the same, its rollup is not.
        await AddArchiveRowAsync(library, "Old chapter", _clock.Now.AddDays(-100), series.Id);
        await using (var db = _h.NewContext())
            await db.Libraries.Where(l => l.Id == library).ExecuteUpdateAsync(u => u.SetProperty(l => l.CatalogRevision, l => l.CatalogRevision + 1));

        var fresh = await AskAsync(cached: false, admin);
        Assert.Equal(fresh, await AskAsync(cached: true, admin));
        Assert.NotEqual("read", StateOf(await AskDtoAsync(cached: true, admin)));
    }

    [Fact]
    public async Task AStackAgeingOutOfTheWindow_LeavesOnTime_FromTheKeptSource()
    {
        var (library, admin) = await SeedOneLibraryAsync();
        await AddArchiveRowAsync(library, "Nearly old", _clock.Now - TimeSpan.FromDays(RecentChaptersService.DefaultWindowDays) + TimeSpan.FromMinutes(2));
        var kept = await AskDtoAsync(cached: true, admin);
        Assert.Contains(kept.Libraries[0].Stacks, s => s.DisplayName == "Nearly old");
        var builds = _cache.Builds;
        var hits = _cache.Hits;

        _clock.Now += TimeSpan.FromMinutes(5);

        var fresh = await AskAsync(cached: false, admin);
        Assert.Equal(fresh, await AskAsync(cached: true, admin));
        Assert.DoesNotContain((await AskDtoAsync(cached: true, admin)).Libraries[0].Stacks, s => s.DisplayName == "Nearly old");
        Assert.Equal(builds, _cache.Builds); // no rebuild: the kept source, filtered by the request's own cut-off
        Assert.True(_cache.Hits > hits);
    }

    [Fact]
    public async Task AClockGoingBack_RebuildsTheSource()
    {
        var (library, admin) = await SeedOneLibraryAsync();
        await AddArchiveRowAsync(library, "Just too old", _clock.Now - TimeSpan.FromDays(RecentChaptersService.DefaultWindowDays) - TimeSpan.FromMinutes(3));
        Assert.DoesNotContain((await AskDtoAsync(cached: true, admin)).Libraries[0].Stacks, s => s.DisplayName == "Just too old");

        _clock.Now -= TimeSpan.FromMinutes(5);

        await AssertFreshAsync(admin, "Just too old");
    }

    [Fact]
    public async Task TheAgeBound_RebuildsAfterTenMinutes()
    {
        var (library, admin) = await SeedOneLibraryAsync();
        var kept = await AskAsync(cached: true, admin);
        await AddArchiveRowAsync(library, "Unannounced", _clock.Now.AddMinutes(-1));

        _clock.Now += HomeStackCache.MaxAge - TimeSpan.FromSeconds(1);
        Assert.Equal(kept, await AskAsync(cached: true, admin)); // still within the bound: reused

        _clock.Now += TimeSpan.FromSeconds(1);
        await AssertFreshAsync(admin, "Unannounced");
    }

    [Fact]
    public async Task TwoWindows_KeepTheirOwnSources_AndViewersTheirOwnVisibility()
    {
        var (library, admin) = await SeedOneLibraryAsync();
        var other = await _h.AddLibraryAsync("Other");
        _h.WriteArchive(other, "Elsewhere/c001.cbz", 40);
        await _h.ScanAsync(other);
        var reader = await _h.AddUserAsync("narrow");
        await using (var db = _h.NewContext())
        {
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = reader, LibraryId = library, GrantedAt = DateTimeOffset.UtcNow });
            db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = reader, HomeRecentWindowDays = 3 });
            await db.SaveChangesAsync();
        }
        await AddArchiveRowAsync(library, "Ten days old", _clock.Now.AddDays(-10));

        // Interleaved: each viewer's answer equals its own uncached answer, whoever asked first.
        for (var round = 0; round < 2; round++)
        {
            Assert.Equal(await AskAsync(cached: false, reader), await AskAsync(cached: true, reader));
            Assert.Equal(await AskAsync(cached: false, admin), await AskAsync(cached: true, admin));
        }
        var narrow = await AskDtoAsync(cached: true, reader);
        Assert.Single(narrow.Libraries);
        Assert.DoesNotContain(narrow.Libraries[0].Stacks, s => s.DisplayName == "Ten days old");
        Assert.Contains((await AskDtoAsync(cached: true, admin)).Libraries.Single(l => l.LibraryName == "Alpha").Stacks, s => s.DisplayName == "Ten days old");
        Assert.True(_cache.Hits >= 4);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// For every viewer, Incognito off / on, every filter and two caps: the OLD service's answer (<see cref="RecentChaptersReference"/>, the
    /// code before the cache) equals the new service's, without and with the kept sources.
    /// </summary>
    private async Task AssertSameForAllAsync(long[] viewers, string step)
    {
        _clock.Now = DateTimeOffset.UtcNow;
        foreach (var viewer in viewers)
        {
            foreach (var incognito in new[] { false, true })
            {
                foreach (var filter in Enum.GetValues<HomeReadStateFilter>())
                {
                    foreach (var cap in new int?[] { null, 2 })
                    {
                        var expected = await AskReferenceAsync(viewer, incognito, filter, cap);
                        var uncached = await AskAsync(cached: false, viewer, incognito, filter, cap);
                        var actual = await AskAsync(cached: true, viewer, incognito, filter, cap);
                        Assert.True(expected == uncached, $"{step} (uncached): viewer {viewer}, incognito {incognito}, {filter}, cap {cap}:\n{uncached}\nexpected\n{expected}");
                        Assert.True(expected == actual, $"{step} (kept): viewer {viewer}, incognito {incognito}, {filter}, cap {cap}:\n{actual}\nexpected\n{expected}");
                    }
                }
            }
        }
    }

    /// <summary>After an input moved: the cached answer is the fresh one again and holds the named stack.</summary>
    private async Task AssertFreshAsync(long userId, string stackName)
    {
        var fresh = await AskAsync(cached: false, userId);
        Assert.Contains(stackName, fresh, StringComparison.Ordinal);
        Assert.Equal(fresh, await AskAsync(cached: true, userId));
    }

    private async Task<string> AskAsync(bool cached, long userId, bool incognito = false, HomeReadStateFilter filter = HomeReadStateFilter.All,
        int? cap = null) =>
        JsonSerializer.Serialize(await AskDtoAsync(cached, userId, incognito, filter, cap));

    /// <summary>One request as the host serves it: a fresh context and service; the cached path shares this test's cache (the singleton).</summary>
    private async Task<RecentChaptersDto> AskDtoAsync(bool cached, long userId, bool incognito = false,
        HomeReadStateFilter filter = HomeReadStateFilter.All, int? cap = null)
    {
        await using var db = _h.NewContext();
        var service = new RecentChaptersService(db, new LibraryAuthorizationService(db),
            covers: new LayeredCoverResolver(new CoverResolutionService(db)), stackCache: cached ? _cache : null, time: _clock);
        return await service.GetRecentChaptersAsync(userId, cap, incognito, filter);
    }

    /// <summary>The same request through the pre-1.46.0-HC service (per-library covers, no kept source, the wall clock).</summary>
    private async Task<string> AskReferenceAsync(long userId, bool incognito, HomeReadStateFilter filter, int? cap)
    {
        await using var db = _h.NewContext();
        var service = new RecentChaptersReference(db, new LibraryAuthorizationService(db), covers: new LayeredCoverResolver(new CoverResolutionService(db)));
        return JsonSerializer.Serialize(await service.GetRecentChaptersAsync(userId, cap, incognito, filter));
    }

    /// <summary>One scanned library ("Alpha": a category folder with two series, one through unit folders, a loose archive) and an admin.</summary>
    private async Task<(long Library, long Admin)> SeedOneLibraryAsync()
    {
        await _h.InitAsync();
        var library = await _h.AddLibraryAsync("Alpha");
        var admin = await _h.AddUserAsync("admin", admin: true);
        _h.WriteArchive(library, "Manga/Series One/Vol 1/c001.cbz", 11);
        _h.WriteArchive(library, "Manga/Series Two/c001.cbz", 12);
        _h.WriteArchive(library, "Loose.cbz", 13);
        await _h.ScanAsync(library);
        _clock.Now = DateTimeOffset.UtcNow;
        return (library, admin);
    }

    /// <summary>An archive row written straight into the catalog (no revision raised), added at <paramref name="createdAt"/>; loose by default.</summary>
    private async Task AddArchiveRowAsync(long libraryId, string name, DateTimeOffset createdAt, long? parentId = null)
    {
        var id = "direct" + ++_serial;
        await using var db = _h.NewContext();
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = id,
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = name,
            RelativePath = id + ".cbz",
            PathKey = id + ".cbz",
            SortKey = "1" + id,
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task<long> AddScanRunAsync(long libraryId, int status)
    {
        await using var db = _h.NewContext();
        var run = new ScanRunEntity { LibraryId = libraryId, ScanRevision = 99, Status = status, StartedAt = DateTimeOffset.UtcNow };
        db.ScanRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task<long> AddRecordAsync()
    {
        await using var db = _h.NewContext();
        var record = new MetadataRecordEntity
        {
            PublicId = "rec" + ++_serial,
            Provider = "mangaupdates",
            ExternalId = "ext" + _serial,
            Title = "Synthetic record " + _serial,
            FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        return record.Id;
    }

    private static ReadingProgressEntity InProgress(long userId, long itemId) => new()
    {
        UserId = userId,
        ItemId = itemId,
        State = (int)ReadingState.InProgress,
        EntryKey = "e1",
        LastMutationId = "m" + itemId,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static NodeSeriesLinkEntity Link(CatalogNodeEntity folder, long? recordId, SeriesLinkState state) => new()
    {
        NodeId = folder.Id,
        LibraryId = folder.LibraryId,
        State = (int)state,
        RecordId = recordId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
