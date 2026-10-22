namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.47.0 lane P - the Info panel follow-ups through the public HTTP surface, each with its visibility rule (grants, Private + Incognito,
/// "Show series information" off):
/// (1) the tag view's read filter (<c>readState</c> on a search with <c>f=</c>) - a WORK is read by the browse folder rule over its
///     locations the viewer may see;
/// (2) the "+N more" bucket page (<c>GET nodes/{id}/info/buckets/{bucket}</c>, <c>GET libraries/{id}/info/buckets/{bucket}</c>);
/// (3) <c>creatorId</c> on creator chips.
/// The new shapes are read as raw JSON / a local record, so this file also compiles on the code before them (the red proof).
///
/// Seed - libraries A ("fpA"), B ("fpB"), P ("fpP"); a reader granted A and P (not B), who marks P Private:
///   W1 Akira       folder @a1 (2 archives, both read by the reader) AND folder @b1 (1 archive, unread)  -> reader Read (B not counted)
///   W2 Berserk     folder @a2 (2 archives, both read by the reader) AND archive @p2 (unread)            -> reader Reading; Incognito Read
///   W3 Claymore    archive @a3 (in progress for the reader)                                              -> Reading
///   W4 Dorohedoro  folder @a4 with no archive                                                            -> Unread (nothing readable)
///   W5 Eden        folder @a5 (1 archive, unread)                                                        -> Unread
///   W6 Fire Punch  folder @b6 only (B)                                                                   -> never the reader's
///   all six carry genre "Seinen"; the "One Shots" folder @aOne holds W7 / W8 with genres G01..G14 (G01-G03 on both; G01 also on W6 in B);
///   creator "Kentaro Miura" (live) on W1 / W2 and "K. Miura" merged into it on W5 (the chip shows the target's value);
///   creator "Hiro Mashima" on W4, merged into "Mashima Hiro" which has no value of its own (the chip keeps its value, names the target);
///   creator "Tatsuki Fujimoto" only on W6 (B);
///   genre "Horror" on W3 @a3, W5 @a5, W2 @p2 and W9 Gantz @p9 (P only) - the Incognito bucket check.
/// </summary>
[Trait("Category", "Http")]
public sealed class InfoPanelFollowUpsHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private readonly FacetQueryTestSupport _support;
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static readonly Dictionary<MangaPixerWebApplicationFactory, Seed> Seeds = [];

    public InfoPanelFollowUpsHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
        _support = new FacetQueryTestSupport(factory);
    }

    private sealed record Seed(
        HttpClient Admin, HttpClient Reader, long ReaderId, long LibA, long LibB, long LibP,
        string Seinen, string Horror, IReadOnlyList<string> ManyGenres, string Miura, string MiuraCreator, string MashimaValue,
        string MashimaCreator, string FujimotoCreator);

    /// <summary>The bucket page as JSON (a local shape: the DTO is new in 1.47.0).</summary>
    private sealed record BucketPage(FacetBucket Bucket, List<FacetChipDto> Chips, int Offset, int Total, int? NextOffset);

    private async Task<Seed> SeedAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (Seeds.TryGetValue(_factory, out var existing))
                return existing;

            var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
            var s = _support;
            var libA = await s.LibraryAsync("fpA", "Follow Lib A");
            var libB = await s.LibraryAsync("fpB", "Follow Lib B");
            var libP = await s.LibraryAsync("fpP", "Follow Lib P");

            var a1 = await s.NodeAsync(libA, null, "fpa1", "Akira Saga");
            var a1x = await s.NodeAsync(libA, a1, "fpa1x", "Akira 01.cbz", folder: false);
            var a1y = await s.NodeAsync(libA, a1, "fpa1y", "Akira 02.cbz", folder: false);
            var b1 = await s.NodeAsync(libB, null, "fpb1", "Akira Saga B");
            await s.NodeAsync(libB, b1, "fpb1x", "Akira 03.cbz", folder: false);
            var a2 = await s.NodeAsync(libA, null, "fpa2", "Berserk Saga");
            var a2x = await s.NodeAsync(libA, a2, "fpa2x", "Berserk 01.cbz", folder: false);
            var a2y = await s.NodeAsync(libA, a2, "fpa2y", "Berserk 02.cbz", folder: false);
            var p2 = await s.NodeAsync(libP, null, "fpp2", "Berserk 03.cbz", folder: false);
            var p9 = await s.NodeAsync(libP, null, "fpp9", "Gantz 01.cbz", folder: false);
            var a3 = await s.NodeAsync(libA, null, "fpa3", "Claymore.cbz", folder: false);
            var a4 = await s.NodeAsync(libA, null, "fpa4", "Dorohedoro Saga");
            var a5 = await s.NodeAsync(libA, null, "fpa5", "Eden Saga");
            await s.NodeAsync(libA, a5, "fpa5x", "Eden 01.cbz", folder: false);
            var b6 = await s.NodeAsync(libB, null, "fpb6", "Fire Punch Saga");
            await s.NodeAsync(libB, b6, "fpb6x", "Fire Punch 01.cbz", folder: false);
            var aOne = await s.NodeAsync(libA, null, "fpaOne", "One Shots");
            var aOne1 = await s.NodeAsync(libA, aOne, "fpaOne1", "Oneshot A.cbz", folder: false);
            var aOne2 = await s.NodeAsync(libA, aOne, "fpaOne2", "Oneshot B.cbz", folder: false);

            var w1 = await s.WorkAsync("node:9101", "Akira");
            var w2 = await s.WorkAsync("node:9102", "Berserk");
            var w3 = await s.WorkAsync("node:9103", "Claymore");
            var w4 = await s.WorkAsync("node:9104", "Dorohedoro");
            var w5 = await s.WorkAsync("node:9105", "Eden");
            var w6 = await s.WorkAsync("node:9106", "Fire Punch");
            var w7 = await s.WorkAsync("node:9107", "Oneshot A");
            var w8 = await s.WorkAsync("node:9108", "Oneshot B");
            var w9 = await s.WorkAsync("node:9109", "Gantz");

            var seinen = await s.ValueAsync(FacetBucket.Genres, "seinen", "Seinen");
            foreach (var (work, node, lib) in new[] { (w1, a1, libA), (w1, b1, libB), (w2, a2, libA), (w2, p2, libP), (w3, a3, libA), (w4, a4, libA), (w5, a5, libA), (w6, b6, libB) })
                await s.EntryAsync(work, node, lib, seinen.Id);

            var horror = await s.ValueAsync(FacetBucket.Genres, "horror", "Horror");
            foreach (var (work, node, lib) in new[] { (w3, a3, libA), (w5, a5, libA), (w2, p2, libP), (w9, p9, libP) })
                await s.EntryAsync(work, node, lib, horror.Id);

            var many = new List<string>();
            for (var i = 1; i <= 14; i++)
            {
                var genre = await s.ValueAsync(FacetBucket.Genres, $"fg{i:00}", $"FG{i:00}");
                many.Add(genre.PublicId);
                await s.EntryAsync(w7, aOne1, libA, genre.Id, FacetSources.ComicInfo);
                if (i <= 3)
                    await s.EntryAsync(w8, aOne2, libA, genre.Id, FacetSources.ComicInfo);
                if (i == 1)
                    await s.EntryAsync(w6, b6, libB, genre.Id, FacetSources.ComicInfo); // B: never in the reader's counts
            }

            var miura = await s.CreatorAsync("Kentaro Miura", []);
            var kMiura = await s.CreatorAsync("K. Miura", [], mergedInto: miura);
            var fujimoto = await s.CreatorAsync("Tatsuki Fujimoto", []);
            var mashimaLive = await s.CreatorAsync("Mashima Hiro", []);
            var mashima = await s.CreatorAsync("Hiro Mashima", [], mergedInto: mashimaLive);
            var mashimaValue = await s.ValueAsync(FacetBucket.Creators, "c:" + mashima, "Hiro Mashima", mashima);
            await s.EntryAsync(w4, a4, libA, mashimaValue.Id, roles: FacetRoles.Writer);
            var miuraValue = await s.ValueAsync(FacetBucket.Creators, "c:" + miura, "Kentaro Miura", miura);
            var kMiuraValue = await s.ValueAsync(FacetBucket.Creators, "c:" + kMiura, "K. Miura", kMiura);
            var fujimotoValue = await s.ValueAsync(FacetBucket.Creators, "c:" + fujimoto, "Tatsuki Fujimoto", fujimoto);
            await s.EntryAsync(w1, a1, libA, miuraValue.Id, roles: FacetRoles.Writer | FacetRoles.Artist);
            await s.EntryAsync(w2, a2, libA, miuraValue.Id, roles: FacetRoles.Writer | FacetRoles.Artist);
            await s.EntryAsync(w5, a5, libA, kMiuraValue.Id, roles: FacetRoles.Writer);
            await s.EntryAsync(w6, b6, libB, fujimotoValue.Id, roles: FacetRoles.Writer | FacetRoles.Artist);

            var (reader, readerId) = await s.ReaderAsync(admin, "fpreader", libA, libP);
            await s.WithDbAsync(async db =>
            {
                foreach (var item in new[] { a1x, a1y, a2x, a2y })
                    db.ReadMarks.Add(new ReadMarkEntity { UserId = readerId, ItemId = item, MarkedAt = DateTimeOffset.UtcNow, Source = "manual" });
                db.ReadingProgress.Add(new ReadingProgressEntity
                {
                    UserId = readerId,
                    ItemId = a3,
                    EntryKey = "e1",
                    Ordinal = 3,
                    State = 1, // in progress
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            });

            var publicIds = await s.WithDbAsync(db => db.Creators.Where(c => c.Id == miura || c.Id == fujimoto || c.Id == mashimaLive)
                .ToDictionaryAsync(c => c.Id, c => c.PublicId));
            var seed = new Seed(admin, reader, readerId, libA, libB, libP, seinen.PublicId, horror.PublicId, many, miuraValue.PublicId,
                publicIds[miura], mashimaValue.PublicId, publicIds[mashimaLive], publicIds[fujimoto]);
            Seeds[_factory] = seed;
            return seed;
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private static string F(string bucket, string id) => "f=" + Uri.EscapeDataString(bucket + ":" + id);

    private static async Task<List<string>> TitlesAsync(HttpClient client, string query, bool incognito = false)
    {
        var page = await FacetQueryTestSupport.GetAsync<SearchResultsDto>(client, "/api/v1/search?" + query, incognito);
        Assert.Equal(page.Works!.Count, page.TotalCount); // one page here: the count is the works' count
        return page.Works!.Select(w => w.Title).ToList();
    }

    // ---- (1) the tag view's read filter ----

    [Fact]
    public async Task ReadFilter_AppliesToWorks_ByTheBrowseRule_OverVisibleLocationsOnly()
    {
        var seed = await SeedAsync();
        var q = F("genre", seed.Seinen);

        Assert.Equal(["Akira", "Berserk", "Claymore", "Dorohedoro", "Eden"], await TitlesAsync(seed.Reader, q));
        // Akira: both archives in A read; its B location (not granted, unread) does not count -> Read, not Reading.
        Assert.Equal(["Akira"], await TitlesAsync(seed.Reader, q + "&readState=read"));
        // Berserk: A read, its P archive unread -> Reading; Claymore: in progress.
        Assert.Equal(["Berserk", "Claymore"], await TitlesAsync(seed.Reader, q + "&readState=reading"));
        // Dorohedoro has nothing readable (Unread, as an empty folder in browse); Fire Punch (B only) is never the reader's.
        Assert.Equal(["Dorohedoro", "Eden"], await TitlesAsync(seed.Reader, q + "&readState=unread"));
        // Anything else is no filter (browse's tolerant rule).
        Assert.Equal(5, (await TitlesAsync(seed.Reader, q + "&readState=bogus")).Count);

        // Read marks are the viewer's own: the admin has none, and sees B.
        Assert.Empty(await TitlesAsync(seed.Admin, q + "&readState=read"));
        Assert.Equal(["Akira", "Berserk", "Claymore", "Dorohedoro", "Eden", "Fire Punch"], await TitlesAsync(seed.Admin, q + "&readState=unread"));
    }

    [Fact]
    public async Task ReadFilter_Incognito_LeavesThePrivateLocationOut()
    {
        var seed = await SeedAsync();
        var q = F("genre", seed.Seinen);
        await _support.SetPrivateAsync(seed.ReaderId, seed.LibP);
        try
        {
            // Without Incognito the Private library still counts: Berserk is Reading.
            Assert.Equal(["Berserk", "Claymore"], await TitlesAsync(seed.Reader, q + "&readState=reading"));
            // Incognito: P's unread archive is left out -> Berserk is Read.
            Assert.Equal(["Akira", "Berserk"], await TitlesAsync(seed.Reader, q + "&readState=read", incognito: true));
            Assert.Equal(["Claymore"], await TitlesAsync(seed.Reader, q + "&readState=reading", incognito: true));
        }
        finally
        {
            await _support.SetPrivateAsync(seed.ReaderId);
        }
    }

    [Fact]
    public async Task ReadFilter_SeriesInformationOff_TheLibraryCountsNowhere()
    {
        var seed = await SeedAsync();
        var q = F("genre", seed.Seinen);
        await _support.SetSeriesInfoHiddenAsync(seed.LibP, true);
        try
        {
            // P's location is out of the tag view's scope, so it is out of the read state too: Berserk is Read.
            Assert.Equal(["Akira", "Berserk"], await TitlesAsync(seed.Reader, q + "&readState=read"));
        }
        finally
        {
            await _support.SetSeriesInfoHiddenAsync(seed.LibP, false);
        }
    }

    [Fact]
    public async Task ReadFilter_PagesAndCountAgree()
    {
        var seed = await SeedAsync();

        // The filter is applied before the page is cut: every page counts the same 2 works, the cursor walks them once.
        var titles = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await FacetQueryTestSupport.GetAsync<SearchResultsDto>(seed.Reader, "/api/v1/search?" + F("genre", seed.Seinen)
                + "&readState=unread&pageSize=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            titles.AddRange(page.Works!.Select(w => w.Title));
            Assert.Equal(2, page.TotalCount);
            cursor = page.HasMore ? page.NextCursor : null;
            Assert.True(++pages <= 5);
        }
        while (cursor is not null);

        Assert.Equal(2, pages);
        Assert.Equal(["Dorohedoro", "Eden"], titles);

        // The admin (no read marks; sees B too): all six are unread, over pages of 4.
        var admin = await FacetQueryTestSupport.GetAsync<SearchResultsDto>(seed.Admin, "/api/v1/search?" + F("genre", seed.Seinen)
            + "&readState=unread&pageSize=4");
        Assert.Equal(6, admin.TotalCount);
        Assert.True(admin.HasMore);
    }

    // ---- (2) the "+N more" bucket page ----

    private static Task<BucketPage> BucketAsync(HttpClient client, string url, bool incognito = false) =>
        FacetQueryTestSupport.GetAsync<BucketPage>(client, url, incognito);

    [Fact]
    public async Task BucketPage_ServesTheRestOfTheBucket_InThePanelsOrder()
    {
        var seed = await SeedAsync();

        var panel = await FacetQueryTestSupport.GetAsync<InfoPanelDto>(seed.Reader, "/api/v1/nodes/fpaOne/info");
        var genres = panel.Buckets.Single(b => b.Bucket == FacetBucket.Genres);
        Assert.Equal(12, genres.Chips.Count);
        Assert.Equal(2, genres.More);

        var whole = await BucketAsync(seed.Reader, "/api/v1/nodes/fpaOne/info/buckets/genre?limit=200");
        Assert.Equal(14, whole.Total);
        Assert.Equal(0, whole.Offset);
        Assert.Null(whole.NextOffset);
        Assert.Equal(seed.ManyGenres.Order(StringComparer.Ordinal), whole.Chips.Select(c => c.Id).Order(StringComparer.Ordinal));
        // The panel's chips are the bucket's first page, chip for chip (same order, counts and "here").
        Assert.Equal(genres.Chips.Select(Describe), whole.Chips.Take(12).Select(Describe));

        var rest = await BucketAsync(seed.Reader, "/api/v1/nodes/fpaOne/info/buckets/genre?offset=12");
        Assert.Equal(whole.Chips.Skip(12).Select(Describe), rest.Chips.Select(Describe));
        Assert.Equal(12, rest.Offset);
        Assert.Null(rest.NextOffset);

        var first = await BucketAsync(seed.Reader, "/api/v1/nodes/fpaOne/info/buckets/Genres?limit=5");
        Assert.Equal(whole.Chips.Take(5).Select(Describe), first.Chips.Select(Describe));
        Assert.Equal(5, first.NextOffset);
        var second = await BucketAsync(seed.Reader, "/api/v1/nodes/fpaOne/info/buckets/genres?offset=5&limit=5");
        Assert.Equal(whole.Chips.Skip(5).Take(5).Select(Describe), second.Chips.Select(Describe));
        Assert.Equal(10, second.NextOffset);

        var past = await BucketAsync(seed.Reader, "/api/v1/nodes/fpaOne/info/buckets/genre?offset=40");
        Assert.Empty(past.Chips);
        Assert.Equal(14, past.Total);

        // The library root: the same rule over the whole library.
        var libraryPanel = await FacetQueryTestSupport.GetAsync<InfoPanelDto>(seed.Reader, "/api/v1/libraries/fpA/info");
        var libraryGenres = libraryPanel.Buckets.Single(b => b.Bucket == FacetBucket.Genres);
        var libraryRest = await BucketAsync(seed.Reader, "/api/v1/libraries/fpA/info/buckets/genre?offset=12");
        Assert.Equal(libraryGenres.More, libraryRest.Chips.Count);
        Assert.Equal(libraryGenres.Chips.Count + libraryGenres.More, libraryRest.Total);
    }

    private static string Describe(FacetChipDto c) => $"{c.Id}/{c.Count}/{c.Here}/{c.Roles}/{c.Conflict}/{c.Filter}";

    [Fact]
    public async Task BucketPage_CountsAreTheViewers_AndNeverReachALibraryTheViewerCannotSee()
    {
        var seed = await SeedAsync();
        var g01 = seed.ManyGenres[0];

        // FG01 is on W7 / W8 in A and on W6 in B: the admin counts 3, the reader 2.
        var reader = await BucketAsync(seed.Reader, "/api/v1/nodes/fpaOne/info/buckets/genre?limit=200");
        var admin = await BucketAsync(seed.Admin, "/api/v1/nodes/fpaOne/info/buckets/genre?limit=200");
        Assert.Equal(2, reader.Chips.Single(c => c.Id == g01).Count);
        Assert.Equal(3, admin.Chips.Single(c => c.Id == g01).Count);

        // A node or library the reader may not open is 404, exactly like the panel; never an answer.
        Assert.Equal(HttpStatusCode.NotFound, await FacetQueryTestSupport.StatusAsync(seed.Reader, "/api/v1/nodes/fpb6/info/buckets/genre"));
        Assert.Equal(HttpStatusCode.NotFound, await FacetQueryTestSupport.StatusAsync(seed.Reader, "/api/v1/libraries/fpB/info/buckets/genre"));
        Assert.Equal(HttpStatusCode.NotFound, await FacetQueryTestSupport.StatusAsync(seed.Reader, "/api/v1/nodes/nosuchnode/info/buckets/genre"));
        Assert.Equal(HttpStatusCode.NotFound, await FacetQueryTestSupport.StatusAsync(seed.Reader, "/api/v1/libraries/nosuchlib/info/buckets/genre"));
        Assert.Equal(HttpStatusCode.Unauthorized,
            await FacetQueryTestSupport.StatusAsync(_factory.CreateClient(), "/api/v1/nodes/fpaOne/info/buckets/genre"));
        using (var bad = await seed.Reader.GetAsync("/api/v1/nodes/fpaOne/info/buckets/bogus"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("invalid_bucket", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // B's creator never shows in the reader's library-root bucket (by name or id); the admin sees it.
        var readerCreators = await FacetQueryTestSupport.GetRawAsync(seed.Reader, "/api/v1/libraries/fpA/info/buckets/creator?limit=200");
        Assert.DoesNotContain("Fujimoto", readerCreators, StringComparison.Ordinal);
        Assert.DoesNotContain(seed.FujimotoCreator, readerCreators, StringComparison.Ordinal);
        var adminB = await BucketAsync(seed.Admin, "/api/v1/libraries/fpB/info/buckets/creator?limit=200");
        Assert.Contains(adminB.Chips, c => c.Display == "Tatsuki Fujimoto");
    }

    [Fact]
    public async Task BucketPage_Incognito_AndSeriesInformationOff()
    {
        var seed = await SeedAsync();
        // Horror: W3 / W5 in A, W2 / W9 in P -> the reader counts 4.
        var plain = await BucketAsync(seed.Reader, "/api/v1/libraries/fpP/info/buckets/genre");
        Assert.Equal(4, plain.Chips.Single(c => c.Id == seed.Horror).Count);
        await _support.SetPrivateAsync(seed.ReaderId, seed.LibP);
        try
        {
            // The Private library's own root still opens in Incognito (direct access, as the panel), but its works leave the counts:
            // Horror counts W3 / W5 only; "here" is the root's own works (W2, W9).
            var direct = await BucketAsync(seed.Reader, "/api/v1/libraries/fpP/info/buckets/genre", incognito: true);
            var horror = direct.Chips.Single(c => c.Id == seed.Horror);
            Assert.Equal(2, horror.Count);
            Assert.Equal(2, horror.Here);
            // Not Incognito's business elsewhere: A's One Shots page answers as before.
            Assert.Equal(14, (await BucketAsync(seed.Reader, "/api/v1/nodes/fpaOne/info/buckets/genre", incognito: true)).Total);
        }
        finally
        {
            await _support.SetPrivateAsync(seed.ReaderId);
        }

        await _support.SetSeriesInfoHiddenAsync(seed.LibA, true);
        try
        {
            var hidden = await BucketAsync(seed.Reader, "/api/v1/nodes/fpaOne/info/buckets/genre");
            Assert.Empty(hidden.Chips);
            Assert.Equal(0, hidden.Total);
            Assert.Null(hidden.NextOffset);
            var hiddenLibrary = await BucketAsync(seed.Admin, "/api/v1/libraries/fpA/info/buckets/genre");
            Assert.Equal(0, hiddenLibrary.Total);
        }
        finally
        {
            await _support.SetSeriesInfoHiddenAsync(seed.LibA, false);
        }
    }

    // ---- (3) creatorId on creator chips ----

    [Fact]
    public async Task CreatorChips_CarryTheLiveCreatorsId_OtherChipsNone()
    {
        var seed = await SeedAsync();

        var raw = await FacetQueryTestSupport.GetRawAsync(seed.Reader, "/api/v1/libraries/fpA/info");
        using var json = JsonDocument.Parse(raw);
        var buckets = json.RootElement.GetProperty("buckets").EnumerateArray().ToList();
        var creators = buckets.Single(b => b.GetProperty("bucket").GetString() == "Creators").GetProperty("chips").EnumerateArray().ToList();
        // "Kentaro Miura" (W1, W2, and W5 through the merged "K. Miura") and "Hiro Mashima" (merged into a creator with no value of its
        // own: the chip keeps its value and names the live creator).
        Assert.Equal(2, creators.Count);
        var miura = creators.Single(c => c.GetProperty("id").GetString() == seed.Miura);
        Assert.Equal(seed.MiuraCreator, miura.GetProperty("creatorId").GetString());
        Assert.Equal(3, miura.GetProperty("here").GetInt32());
        var mashima = creators.Single(c => c.GetProperty("id").GetString() == seed.MashimaValue);
        Assert.Equal(seed.MashimaCreator, mashima.GetProperty("creatorId").GetString());
        // Every other bucket: no creatorId key at all.
        foreach (var bucket in buckets.Where(b => b.GetProperty("bucket").GetString() != "Creators"))
        {
            foreach (var chip in bucket.GetProperty("chips").EnumerateArray())
                Assert.False(chip.TryGetProperty("creatorId", out _));
        }
        // A creator only in a library the reader cannot see is never named, by id either.
        Assert.DoesNotContain(seed.FujimotoCreator, raw, StringComparison.Ordinal);

        // The node panel and the bucket page carry it too.
        var node = await FacetQueryTestSupport.GetRawAsync(seed.Reader, "/api/v1/nodes/fpa1/info");
        Assert.Contains(seed.MiuraCreator, node, StringComparison.Ordinal);
        var page = await FacetQueryTestSupport.GetRawAsync(seed.Reader, "/api/v1/nodes/fpa5/info/buckets/creator");
        Assert.Contains(seed.MiuraCreator, page, StringComparison.Ordinal);
        var admin = await FacetQueryTestSupport.GetRawAsync(seed.Admin, "/api/v1/libraries/fpB/info");
        Assert.Contains(seed.FujimotoCreator, admin, StringComparison.Ordinal);
    }
}
