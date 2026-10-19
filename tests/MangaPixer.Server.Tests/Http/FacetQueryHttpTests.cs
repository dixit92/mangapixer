namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.45.0 lane Q, D1 2.5 / 2.6: per-viewer facet counts WITHOUT LEAKS, the Info panel (<c>GET nodes/{id}/info</c>,
/// <c>GET libraries/{id}/info</c>) and the tag view (search with <c>f=</c> / typed fields), through the public HTTP surface. The tag
/// index is seeded directly (lane F builds it). Red first: against a naive global count (no <c>@shown</c> filter) the leak tests fail.
///
/// Seed - library A ("fqA") and library B ("fqB"); a reader granted A only:
///   creator "Naoki Urasawa" (also spelled "Urasawa Naoki")  W1 Pluto @a1, W2 Monster @a2, W3 20th Century Boys @a3 AND @b3,
///                                                         W4 Billy Bat @b1, W5 Yawara @b2, W6 Master Keaton @aTomb (TOMBSTONED),
///                                                         W7 Oneshot A @aOne1                -> admin 6 works, reader 4, naive 7
///   genre Mystery            W1 @a1, W4 @b1                 -> admin 2, reader 1 (quiet chip)
///   genre Psychological      W3 @b3 only (B's ComicInfo of a work also in A) -> invisible to the reader
///   publisher Shogakukan     W1 @a1 (declared, conflict), W2 @a2
///   creator "N. Urasawa"     merged into "Naoki Urasawa"; its value has no entry (the redirect)
///   genres G01..G14          W7 @aOne1 and W8 Oneshot B @aOne2 (the "One Shots" container: top 12 + More 2)
/// </summary>
[Trait("Category", "Http")]
public sealed class FacetQueryHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private readonly FacetQueryTestSupport _support;
    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    public FacetQueryHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
        _support = new FacetQueryTestSupport(factory);
    }

    private sealed record Seed(
        HttpClient Admin, HttpClient Reader, long AdminId, long LibA, long LibB,
        string Creator, string Mystery, string Psychological, string Publisher, string MergedCreator, IReadOnlyList<string> ManyGenres);

    private static readonly Dictionary<MangaPixerWebApplicationFactory, Seed> Seeds = [];

    private async Task<Seed> SeedAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (Seeds.TryGetValue(_factory, out var existing))
                return existing;

            var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
            var s = _support;
            var libA = await s.LibraryAsync("fqA", "Facet Lib A");
            var libB = await s.LibraryAsync("fqB", "Facet Lib B");
            var a1 = await s.NodeAsync(libA, null, "fqa1", "Pluto Saga");
            await s.NodeAsync(libA, a1, "fqa1x", "Pluto 01.cbz", folder: false);
            var a2 = await s.NodeAsync(libA, null, "fqa2", "Monster Saga");
            var a3 = await s.NodeAsync(libA, null, "fqa3", "Twenty Saga");
            var aTomb = await s.NodeAsync(libA, null, "fqaTomb", "Keaton Saga", tombstoned: true);
            var aOne = await s.NodeAsync(libA, null, "fqaOne", "One Shots");
            var aOne1 = await s.NodeAsync(libA, aOne, "fqaOne1", "Oneshot A.cbz", folder: false);
            var aOne2 = await s.NodeAsync(libA, aOne, "fqaOne2", "Oneshot B.cbz", folder: false);
            var b1 = await s.NodeAsync(libB, null, "fqb1", "Billy Saga");
            var b2 = await s.NodeAsync(libB, null, "fqb2", "Yawara Saga");
            var b3 = await s.NodeAsync(libB, null, "fqb3", "Twenty Saga B");

            var w1 = await s.WorkAsync("node:9001", "Pluto");
            var w2 = await s.WorkAsync("node:9002", "Monster");
            var w3 = await s.WorkAsync("node:9003", "20th Century Boys");
            var w4 = await s.WorkAsync("node:9004", "Billy Bat");
            var w5 = await s.WorkAsync("node:9005", "Yawara");
            var w6 = await s.WorkAsync("node:9006", "Master Keaton");
            var w7 = await s.WorkAsync("node:9007", "Oneshot A");
            var w8 = await s.WorkAsync("node:9008", "Oneshot B");

            var urasawa = await s.CreatorAsync("Naoki Urasawa", ["Urasawa Naoki"]);
            var merged = await s.CreatorAsync("N. Urasawa", [], mergedInto: urasawa);
            var creator = await s.ValueAsync(FacetBucket.Creators, "c:" + urasawa, "Naoki Urasawa", urasawa);
            var mergedValue = await s.ValueAsync(FacetBucket.Creators, "c:" + merged, "N. Urasawa", merged);
            var mystery = await s.ValueAsync(FacetBucket.Genres, "mystery", "Mystery");
            var psychological = await s.ValueAsync(FacetBucket.Genres, "psychological", "Psychological");
            var publisher = await s.ValueAsync(FacetBucket.Publisher, "shogakukan", "Shogakukan");

            const FacetRoles both = FacetRoles.Writer | FacetRoles.Artist;
            foreach (var (work, node, lib) in new[] { (w1, a1, libA), (w2, a2, libA), (w3, a3, libA), (w3, b3, libB), (w4, b1, libB), (w5, b2, libB), (w6, aTomb, libA), (w7, aOne1, libA) })
                await s.EntryAsync(work, node, lib, creator.Id, roles: both);
            await s.EntryAsync(w1, a1, libA, mystery.Id);
            await s.EntryAsync(w4, b1, libB, mystery.Id);
            await s.EntryAsync(w3, b3, libB, psychological.Id, FacetSources.ComicInfo);
            await s.EntryAsync(w1, a1, libA, publisher.Id, FacetSources.Declared, conflict: true);
            await s.EntryAsync(w2, a2, libA, publisher.Id);

            var many = new List<string>();
            for (var i = 1; i <= 14; i++)
            {
                var genre = await s.ValueAsync(FacetBucket.Genres, $"g{i:00}", $"G{i:00}");
                many.Add(genre.PublicId);
                await s.EntryAsync(w7, aOne1, libA, genre.Id, FacetSources.ComicInfo);
                if (i <= 3)
                    await s.EntryAsync(w8, aOne2, libA, genre.Id, FacetSources.ComicInfo); // G01-G03 carried by both works below
            }

            var (reader, _) = await s.ReaderAsync(admin, "fqreader", libA);
            var adminId = await s.UserIdAsync("admin");
            var seed = new Seed(admin, reader, adminId, libA, libB, creator.PublicId, mystery.PublicId, psychological.PublicId,
                publisher.PublicId, mergedValue.PublicId, many);
            Seeds[_factory] = seed;
            return seed;
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private static Task<InfoPanelDto> InfoAsync(HttpClient client, string nodeId, bool incognito = false) =>
        FacetQueryTestSupport.GetAsync<InfoPanelDto>(client, $"/api/v1/nodes/{nodeId}/info", incognito);

    private static Task<SearchResultsDto> TagViewAsync(HttpClient client, string query, bool incognito = false) =>
        FacetQueryTestSupport.GetAsync<SearchResultsDto>(client, "/api/v1/search?" + query, incognito);

    private static string F(string bucket, string id) => "f=" + Uri.EscapeDataString(bucket + ":" + id);

    // ---- counts without leaks (red first against a naive global count) ----

    [Fact]
    public async Task Counts_LeaveOutLibrariesTheReaderCannotSee_AndTombstonedLocations()
    {
        var seed = await SeedAsync();

        var reader = await InfoAsync(seed.Reader, "fqa1");
        var admin = await InfoAsync(seed.Admin, "fqa1");

        Assert.Equal(InfoPanelMode.Work, reader.Mode);
        Assert.Equal(4, FacetQueryTestSupport.Chip(reader, seed.Creator)!.Count); // W1 W2 W3 W7 - not B's W4 W5, not the tombstoned W6
        Assert.Equal(6, FacetQueryTestSupport.Chip(admin, seed.Creator)!.Count);
        // Count-of-1 rule: the reader sees one Mystery work -> a quiet chip (still there, still clickable).
        var quiet = FacetQueryTestSupport.Chip(reader, seed.Mystery)!;
        Assert.Null(quiet.Count);
        Assert.Equal("genre:" + seed.Mystery, quiet.Filter);
        Assert.Equal(2, FacetQueryTestSupport.Chip(admin, seed.Mystery)!.Count);
        // Roles and the declared conflict travel with the chip.
        Assert.Equal(FacetRoles.Writer | FacetRoles.Artist, FacetQueryTestSupport.Chip(reader, seed.Creator)!.Roles);
        Assert.True(FacetQueryTestSupport.Chip(reader, seed.Publisher)!.Conflict);
        Assert.Equal("Pluto", reader.Work!.Title);
    }

    [Fact]
    public async Task TagView_ListsOnlyWorksAndLocationsTheViewerMaySee()
    {
        var seed = await SeedAsync();

        var reader = await TagViewAsync(seed.Reader, F("creator", seed.Creator));
        var admin = await TagViewAsync(seed.Admin, F("creator", seed.Creator));

        Assert.Equal(["20th Century Boys", "Monster", "Oneshot A", "Pluto"], reader.Works!.Select(w => w.Title).Order(StringComparer.Ordinal));
        Assert.Equal(4, reader.TotalCount);
        Assert.Empty(reader.Items);
        Assert.All(reader.Works!.SelectMany(w => w.Locations), l => Assert.Equal("fqA", l.LibraryId));
        var twenty = reader.Works!.Single(w => w.Title == "20th Century Boys");
        Assert.Equal(1, twenty.LocationCount);
        Assert.Equal("fqa3", twenty.Node.Id);
        Assert.Equal(["Twenty Saga"], twenty.Locations[0].Trail.Select(t => t.DisplayName));

        Assert.Equal(6, admin.TotalCount);
        Assert.DoesNotContain(admin.Works!, w => w.Title == "Master Keaton"); // tombstoned location
        Assert.Equal(2, admin.Works!.Single(w => w.Title == "20th Century Boys").LocationCount);
        var filter = Assert.Single(reader.Filters!);
        Assert.True(filter.Matched);
        Assert.Equal("Naoki Urasawa", filter.Display);
    }

    [Fact]
    public async Task ValueOnlyWhereTheReaderCannotLook_AnswersExactlyLikeAnUnknownValue()
    {
        var seed = await SeedAsync();

        // The work W3 is also in A, but Psychological is only on its B location: never on the reader's panel.
        var panel = await InfoAsync(seed.Reader, "fqa3");
        Assert.Null(FacetQueryTestSupport.Chip(panel, seed.Psychological));

        var hidden = await FacetQueryTestSupport.GetRawAsync(seed.Reader, "/api/v1/search?" + F("genre", seed.Psychological));
        var unknownId = FacetPublicIds.NewValueId();
        var unknown = await FacetQueryTestSupport.GetRawAsync(seed.Reader, "/api/v1/search?" + F("genre", unknownId));
        Assert.Equal(unknown.Replace(unknownId, "ID", StringComparison.Ordinal), hidden.Replace(seed.Psychological, "ID", StringComparison.Ordinal));
        var parsed = JsonSerializer.Deserialize<SearchResultsDto>(hidden, TestJson.Web)!;
        Assert.Equal(0, parsed.TotalCount);
        Assert.Empty(parsed.Works!);
        Assert.False(parsed.Filters![0].Matched);
        Assert.Null(parsed.Filters[0].Display);
        Assert.DoesNotContain("Psychological", hidden, StringComparison.Ordinal);

        // Typed: resolved only among values the viewer can see.
        var typedHidden = await FacetQueryTestSupport.GetRawAsync(seed.Reader, "/api/v1/search?q=" + Uri.EscapeDataString("genre:Psychological"));
        var typedUnknown = await FacetQueryTestSupport.GetRawAsync(seed.Reader, "/api/v1/search?q=" + Uri.EscapeDataString("genre:Psychologicak"));
        Assert.Equal(typedUnknown.Replace("Psychologicak", "X", StringComparison.Ordinal), typedHidden.Replace("Psychological", "X", StringComparison.Ordinal));
        var admin = await TagViewAsync(seed.Admin, "q=" + Uri.EscapeDataString("genre:Psychological"));
        Assert.Equal(["20th Century Boys"], admin.Works!.Select(w => w.Title));
    }

    [Fact]
    public async Task NodeOrLibraryTheViewerCannotOpen_Is404_NeverAnswered()
    {
        var seed = await SeedAsync();

        Assert.Equal(HttpStatusCode.NotFound, await FacetQueryTestSupport.StatusAsync(seed.Reader, "/api/v1/nodes/fqb1/info"));
        Assert.Equal(HttpStatusCode.NotFound, await FacetQueryTestSupport.StatusAsync(seed.Reader, "/api/v1/libraries/fqB/info"));
        Assert.Equal(HttpStatusCode.NotFound, await FacetQueryTestSupport.StatusAsync(seed.Reader, "/api/v1/nodes/nosuchnode/info"));
        Assert.Equal(HttpStatusCode.NotFound, await FacetQueryTestSupport.StatusAsync(seed.Admin, "/api/v1/nodes/fqaTomb/info"));
        Assert.Equal(HttpStatusCode.Unauthorized, await FacetQueryTestSupport.StatusAsync(_factory.CreateClient(), "/api/v1/nodes/fqa1/info"));
        Assert.Equal(HttpStatusCode.OK, await FacetQueryTestSupport.StatusAsync(seed.Admin, "/api/v1/nodes/fqb1/info"));
    }

    [Fact]
    public async Task Incognito_LeavesPrivateLibrariesOutOfCounts_ButTheNodeStillOpens()
    {
        var seed = await SeedAsync();
        await _support.SetPrivateAsync(seed.AdminId, seed.LibB);
        try
        {
            var incognito = await InfoAsync(seed.Admin, "fqa1", incognito: true);
            Assert.Equal(4, FacetQueryTestSupport.Chip(incognito, seed.Creator)!.Count);
            Assert.Null(FacetQueryTestSupport.Chip(incognito, seed.Mystery)!.Count); // B's Mystery work left out -> 1

            // Direct access to B's node works in Incognito; its counts still leave the Private library out.
            var privateNode = await InfoAsync(seed.Admin, "fqb1", incognito: true);
            Assert.Equal(InfoPanelMode.Work, privateNode.Mode);
            Assert.Equal(4, FacetQueryTestSupport.Chip(privateNode, seed.Creator)!.Count);

            var search = await TagViewAsync(seed.Admin, F("creator", seed.Creator), incognito: true);
            Assert.Equal(4, search.TotalCount);

            var plain = await InfoAsync(seed.Admin, "fqa1");
            Assert.Equal(6, FacetQueryTestSupport.Chip(plain, seed.Creator)!.Count);
        }
        finally
        {
            await _support.SetPrivateAsync(seed.AdminId);
        }
    }

    [Fact]
    public async Task SeriesInformationOff_TheLibraryContributesNothing()
    {
        var seed = await SeedAsync();
        await _support.SetSeriesInfoHiddenAsync(seed.LibB, true);
        try
        {
            Assert.Equal(4, FacetQueryTestSupport.Chip(await InfoAsync(seed.Admin, "fqa1"), seed.Creator)!.Count);
            var bNode = await InfoAsync(seed.Admin, "fqb1");
            Assert.Equal(InfoPanelMode.None, bNode.Mode);
            Assert.Empty(bNode.Buckets);
            Assert.Equal(4, (await TagViewAsync(seed.Admin, F("creator", seed.Creator))).TotalCount);
            var typed = await TagViewAsync(seed.Admin, "q=" + Uri.EscapeDataString("genre:Psychological"));
            Assert.False(typed.Filters![0].Matched);
            // A work also located in a shown library stays, with only its shown location.
            var twenty = await TagViewAsync(seed.Admin, F("creator", seed.Creator) + "&q=Century");
            Assert.Equal(1, Assert.Single(twenty.Works!).LocationCount);
        }
        finally
        {
            await _support.SetSeriesInfoHiddenAsync(seed.LibB, false);
        }

        await _support.SetSeriesInfoHiddenEverywhereAsync(true);
        try
        {
            Assert.Equal(InfoPanelMode.None, (await InfoAsync(seed.Admin, "fqa1")).Mode);
            var search = await TagViewAsync(seed.Admin, F("creator", seed.Creator));
            Assert.Equal(0, search.TotalCount);
            Assert.False(search.Filters![0].Matched);
        }
        finally
        {
            await _support.SetSeriesInfoHiddenEverywhereAsync(false);
        }
    }

    [Fact]
    public async Task CacheOrder_AdminFirstOrReaderFirst_EqualsAColdComputation()
    {
        var seed = await SeedAsync();
        string[] nodes = ["fqa1", "fqa2", "fqa3", "fqaOne"];

        async Task<string> CountsAsync(HttpClient client)
        {
            var parts = new List<string>();
            foreach (var node in nodes)
            {
                var panel = await InfoAsync(client, node);
                parts.AddRange(panel.Buckets.SelectMany(b => b.Chips).Select(c => $"{node}/{c.Id}={c.Count}/{c.Here}").Order(StringComparer.Ordinal));
            }
            return string.Join(";", parts);
        }

        _support.Bump();
        var coldAdmin = await CountsAsync(seed.Admin);
        _support.Bump();
        var coldReader = await CountsAsync(seed.Reader);
        Assert.NotEqual(coldAdmin, coldReader);

        _support.Bump();
        var adminFirstAdmin = await CountsAsync(seed.Admin);
        var adminFirstReader = await CountsAsync(seed.Reader);
        _support.Bump();
        var readerFirstReader = await CountsAsync(seed.Reader);
        var readerFirstAdmin = await CountsAsync(seed.Admin);

        Assert.Equal(coldAdmin, adminFirstAdmin);
        Assert.Equal(coldAdmin, readerFirstAdmin);
        Assert.Equal(coldReader, adminFirstReader);
        Assert.Equal(coldReader, readerFirstReader);
        // Warm answers come from the cache (the counts are held, keyed by the shown set - never by user).
        Assert.True(_factory.Services.GetRequiredService<FacetCountCache>().Count > 0);
    }

    // ---- the panel ----

    [Fact]
    public async Task Container_ShowsTheWorksBelow_TopTwelvePerBucket_WithMoreAndHere()
    {
        var seed = await SeedAsync();

        var panel = await InfoAsync(seed.Reader, "fqaOne");

        Assert.Equal(InfoPanelMode.Container, panel.Mode);
        Assert.Equal(2, panel.WorksBelow);
        Assert.Null(panel.Work);
        var genres = panel.Buckets.Single(b => b.Bucket == FacetBucket.Genres);
        Assert.Equal(12, genres.Chips.Count);
        Assert.Equal(2, genres.More);
        // Carried by both works below first ("2 here"), then the rest.
        Assert.Equal(seed.ManyGenres.Take(3), genres.Chips.Take(3).Select(c => c.Id));
        Assert.All(genres.Chips.Take(3), c => Assert.Equal(2, c.Here));
        Assert.All(genres.Chips.Skip(3), c => Assert.Equal(1, c.Here));
        Assert.Equal(2, genres.Chips[0].Count);
        Assert.Null(genres.Chips[5].Count);
        // Buckets in the owner's order: creators before genres.
        Assert.Equal(FacetBucket.Creators, panel.Buckets[0].Bucket);

        var library = await FacetQueryTestSupport.GetAsync<InfoPanelDto>(seed.Reader, "/api/v1/libraries/fqA/info");
        Assert.Equal(InfoPanelMode.Container, library.Mode);
        Assert.Equal(5, library.WorksBelow); // W1 W2 W3 W7 W8 (W6's location is tombstoned)
        Assert.Equal(4, library.Buckets.Single(b => b.Bucket == FacetBucket.Creators).Chips.Single().Here); // W1 W2 W3 W7
    }

    [Fact]
    public async Task ArchiveInsideAWorksFolder_ShowsThatWork_AndAPlainFolderShowsNothing()
    {
        var seed = await SeedAsync();

        var archive = await InfoAsync(seed.Reader, "fqa1x");
        Assert.Equal(InfoPanelMode.Work, archive.Mode);
        Assert.Equal("Pluto", archive.Work!.Title);
        Assert.Equal(1, archive.Work.LocationCount);
        Assert.Equal("fqa1", archive.Work.Locations[0].NodeId);
        Assert.Equal("Facet Lib A", archive.Work.Locations[0].LibraryName);

        var reader = await InfoAsync(seed.Reader, "fqa3");
        Assert.Equal(1, reader.Work!.LocationCount);
        var admin = await InfoAsync(seed.Admin, "fqa3");
        Assert.Equal(2, admin.Work!.LocationCount);
    }

    // ---- the tag view ----

    [Fact]
    public async Task TypedCreator_MatchesEverySpelling_AndAMergedCreatorRedirects()
    {
        var seed = await SeedAsync();

        var typed = await TagViewAsync(seed.Reader, "q=" + Uri.EscapeDataString("creator:\"Urasawa Naoki\""));
        Assert.Equal(4, typed.TotalCount);
        var filter = Assert.Single(typed.Filters!);
        Assert.Equal(SearchFilterSource.Typed, filter.Source);
        Assert.Equal("Naoki Urasawa", filter.Display);
        Assert.Equal([seed.Creator], filter.ValueIds);

        var redirected = await TagViewAsync(seed.Reader, F("creator", seed.MergedCreator));
        Assert.Equal(4, redirected.TotalCount);
        Assert.Equal(seed.MergedCreator, redirected.Filters![0].RedirectedFrom);
        Assert.Equal([seed.Creator], redirected.Filters[0].ValueIds);

        // A merged creator's own name resolves to its target too.
        Assert.Equal(4, (await TagViewAsync(seed.Reader, "q=" + Uri.EscapeDataString("creator:\"N. Urasawa\""))).TotalCount);
    }

    [Fact]
    public async Task Filters_AreAnded_AndFreeTextNarrowsByTitle()
    {
        var seed = await SeedAsync();

        var both = await TagViewAsync(seed.Reader, F("creator", seed.Creator) + "&" + F("publisher", seed.Publisher));
        Assert.Equal(["Monster", "Pluto"], both.Works!.Select(w => w.Title));

        var narrowed = await TagViewAsync(seed.Reader, F("creator", seed.Creator) + "&q=mon");
        Assert.Equal(["Monster"], narrowed.Works!.Select(w => w.Title));

        var typedAndChip = await TagViewAsync(seed.Admin, "q=" + Uri.EscapeDataString("genre:mystery") + "&" + F("creator", seed.Creator));
        Assert.Equal(["Billy Bat", "Pluto"], typedAndChip.Works!.Select(w => w.Title));
    }

    [Fact]
    public async Task WorksPage_IsKeysetPaged_EveryWorkOnce()
    {
        var seed = await SeedAsync();

        var titles = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await TagViewAsync(seed.Admin, F("creator", seed.Creator) + "&pageSize=2"
                + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            titles.AddRange(page.Works!.Select(w => w.Title));
            Assert.Equal(6, page.TotalCount);
            cursor = page.HasMore ? page.NextCursor : null;
            Assert.True(++pages <= 10);
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(["20th Century Boys", "Billy Bat", "Monster", "Oneshot A", "Pluto", "Yawara"], titles);
    }

    [Fact]
    public async Task MalformedFilter_Is400_InvalidFilter()
    {
        var seed = await SeedAsync();

        foreach (var bad in new[] { "creator:nope", "bogus:" + seed.Creator, "creator", ":" + seed.Creator })
        {
            using var response = await seed.Reader.GetAsync("/api/v1/search?f=" + Uri.EscapeDataString(bad));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("invalid_filter", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PlainSearch_IsUnchanged_NoWorksOrFiltersInTheAnswer()
    {
        var seed = await SeedAsync();

        var raw = await FacetQueryTestSupport.GetRawAsync(seed.Reader, "/api/v1/search?q=Pluto");
        using var json = JsonDocument.Parse(raw);
        Assert.False(json.RootElement.TryGetProperty("works", out _));
        Assert.False(json.RootElement.TryGetProperty("filters", out _));
        var ids = json.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Contains("fqa1", ids);
        // An unknown field prefix stays free text: a plain folder search.
        var reZero = await FacetQueryTestSupport.GetRawAsync(seed.Reader, "/api/v1/search?q=" + Uri.EscapeDataString("Re:Zero"));
        Assert.False(JsonDocument.Parse(reZero).RootElement.TryGetProperty("works", out _));
    }
}
