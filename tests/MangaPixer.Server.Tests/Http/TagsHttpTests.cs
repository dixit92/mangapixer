namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using Xunit;

/// <summary>
/// 1.49.0 lane T (owner decisions 8 a + a, 9 a): the Tags bucket and tags that combine, through the public HTTP surface - exclusion and
/// any-of in <c>f</c>, the typed syntax (<c>-genre:</c>, <c>tag:</c>, <c>year:a..b</c>, unquoted word match, <c>series:</c>, scope words),
/// tag-view free text over titles / alternative titles / folder names (S5), the Refine list with PER-VIEWER counts (two users; the count
/// cache is keyed by the viewer scope), and the Info panel's Tags row (strongest first, "+N more"). Seeded directly; synthetic names only.
///
/// Seed - library A ("tgA", "Tags Lib A") and B ("tgB", "Tags Lib B"); a reader granted A only:
///   W1 "Pluto" @a1 "Pluto Saga" (record: alt title "Kanzenban Edition", 10 categories) - Mystery, Robots, Year 2003, creator Naoki Urasawa,
///      tags T01..T10 (votes 10..1) + "Comic Only" (ComicInfo, no votes)
///   W2 "Monster" @a2 "Monster Perfect Edition"       - Mystery, Horror, Year 1994
///   W3 "Billy Bat" @b1 (library B)                    - Mystery, Robots, Secret Tag, Year 2008
///   W4 "Yawara" @a3                                   - Sports, Year 1986
/// </summary>
[Trait("Category", "Http")]
public sealed class TagsHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private readonly FacetQueryTestSupport _support;
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static readonly Dictionary<MangaPixerWebApplicationFactory, Seed> Seeds = [];

    public TagsHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
        _support = new FacetQueryTestSupport(factory);
    }

    private sealed record Seed(
        HttpClient Admin, HttpClient Reader, string Mystery, string Horror, string Sports, string Robots, string Secret,
        IReadOnlyList<string> Ranked, string ComicOnly);

    private async Task<Seed> SeedAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (Seeds.TryGetValue(_factory, out var existing))
                return existing;

            var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
            var s = _support;
            var libA = await s.LibraryAsync("tgA", "Tags Lib A");
            var libB = await s.LibraryAsync("tgB", "Tags Lib B");
            var a1 = await s.NodeAsync(libA, null, "tga1", "Pluto Saga");
            var a2 = await s.NodeAsync(libA, null, "tga2", "Monster Perfect Edition");
            var a3 = await s.NodeAsync(libA, null, "tga3", "Yawara Saga");
            var b1 = await s.NodeAsync(libB, null, "tgb1", "Billy Saga");

            var categories = "[" + string.Join(",", Enumerable.Range(1, 10).Select(i => $"{{\"name\":\"T{i:00}\",\"votes\":{11 - i}}}")) + "]";
            var record = await s.RecordAsync("77001", "Pluto", "[\"Kanzenban Edition\"]", categories);
            var w1 = await s.WorkAsync("rec:" + record, "Pluto", record);
            var w2 = await s.WorkAsync("node:9102", "Monster");
            var w3 = await s.WorkAsync("node:9103", "Billy Bat");
            var w4 = await s.WorkAsync("node:9104", "Yawara");

            var mystery = await s.ValueAsync(FacetBucket.Genres, "g:mystery", "Mystery");
            var horror = await s.ValueAsync(FacetBucket.Genres, "g:horror", "Horror");
            var sports = await s.ValueAsync(FacetBucket.Genres, "g:sports", "Sports");
            var robots = await s.ValueAsync(FacetBucket.Tags, "tg:robots", "Robots");
            var secret = await s.ValueAsync(FacetBucket.Tags, "tg:secret tag", "Secret Tag");
            var y2003 = await s.ValueAsync(FacetBucket.Year, "y:2003", "2003");
            var y1994 = await s.ValueAsync(FacetBucket.Year, "y:1994", "1994");
            var y2008 = await s.ValueAsync(FacetBucket.Year, "y:2008", "2008");
            var y1986 = await s.ValueAsync(FacetBucket.Year, "y:1986", "1986");
            var urasawa = await s.CreatorAsync("Naoki Urasawa", []);
            var creator = await s.ValueAsync(FacetBucket.Creators, "c:" + urasawa, "Naoki Urasawa", urasawa);

            foreach (var (work, node, lib, value) in new[]
                     {
                         (w1, a1, libA, mystery.Id), (w1, a1, libA, robots.Id), (w1, a1, libA, y2003.Id), (w1, a1, libA, creator.Id),
                         (w2, a2, libA, mystery.Id), (w2, a2, libA, horror.Id), (w2, a2, libA, y1994.Id),
                         (w3, b1, libB, mystery.Id), (w3, b1, libB, robots.Id), (w3, b1, libB, secret.Id), (w3, b1, libB, y2008.Id),
                         (w4, a3, libA, sports.Id), (w4, a3, libA, y1986.Id),
                     })
                await s.EntryAsync(work, node, lib, value);

            // W1's tags: the record's ten categories (shuffled insert order) and one ComicInfo-only tag.
            var ranked = new List<string>();
            var byName = new Dictionary<string, string>();
            foreach (var i in new[] { 7, 2, 10, 1, 5, 9, 3, 8, 4, 6 })
            {
                var tag = await s.ValueAsync(FacetBucket.Tags, $"tg:t{i:00}", $"T{i:00}");
                byName[$"T{i:00}"] = tag.PublicId;
                await s.EntryAsync(w1, a1, libA, tag.Id);
            }
            ranked.AddRange(Enumerable.Range(1, 10).Select(i => byName[$"T{i:00}"]));
            var comicOnly = await s.ValueAsync(FacetBucket.Tags, "tg:comic only", "Comic Only");
            await s.EntryAsync(w1, a1, libA, comicOnly.Id, FacetSources.ComicInfo);

            var (reader, _) = await s.ReaderAsync(admin, "tgreader", libA);
            var seed = new Seed(admin, reader, mystery.PublicId, horror.PublicId, sports.PublicId, robots.PublicId, secret.PublicId, ranked,
                comicOnly.PublicId);
            Seeds[_factory] = seed;
            return seed;
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private static Task<SearchResultsDto> SearchAsync(HttpClient client, string query) =>
        FacetQueryTestSupport.GetAsync<SearchResultsDto>(client, "/api/v1/search?" + query);

    private static string[] Titles(SearchResultsDto result) => result.Works!.Select(w => w.Title).ToArray();

    private static string F(string filter) => "f=" + Uri.EscapeDataString(filter);

    private static string Q(string q) => "q=" + Uri.EscapeDataString(q);

    [Fact]
    public async Task Exclude_ByChipAndTyped_DropsTheWorksCarryingTheValue()
    {
        var seed = await SeedAsync();

        var chip = await SearchAsync(seed.Admin, $"{F("genre:" + seed.Mystery)}&{F("-genre:" + seed.Horror)}");
        Assert.Equal(["Billy Bat", "Pluto"], Titles(chip));
        var echo = Assert.Single(chip.Filters!, f => f.Negated);
        Assert.Equal(("Horror", true), (echo.Display, echo.Matched));

        var reader = await SearchAsync(seed.Reader, $"{F("genre:" + seed.Mystery)}&{F("-genre:" + seed.Horror)}");
        Assert.Equal(["Pluto"], Titles(reader));

        // Only an exclusion: every work the viewer may see, without the excluded value.
        Assert.Equal(["Billy Bat", "Pluto", "Yawara"], Titles(await SearchAsync(seed.Admin, Q("-genre:horror"))));
        Assert.Equal(["Pluto", "Yawara"], Titles(await SearchAsync(seed.Reader, Q("-genre:horror"))));
        // An exclusion of a value nobody can see excludes nothing.
        Assert.Equal(["Pluto", "Yawara"], Titles(await SearchAsync(seed.Reader, $"{F("-tag:" + seed.Secret)}&{F("-genre:" + seed.Horror)}")));
    }

    [Fact]
    public async Task AnyOf_InOneFilter_MatchesEither_AndTheOldGrammarStillWorks()
    {
        var seed = await SeedAsync();

        var any = await SearchAsync(seed.Admin, F($"genre:{seed.Horror}|{seed.Sports}"));
        Assert.Equal(["Monster", "Yawara"], Titles(any));
        var echo = Assert.Single(any.Filters!);
        Assert.Equal(($"{seed.Horror}|{seed.Sports}", "Horror or Sports"), (echo.Id, echo.Display));
        Assert.Equal(2, echo.ValueIds.Count);

        Assert.Equal(["Monster", "Yawara"], Titles(await SearchAsync(seed.Admin, Q("genre:horror|sports"))));
        Assert.Equal(["Billy Bat", "Monster", "Pluto"], Titles(await SearchAsync(seed.Admin, F("genre:" + seed.Mystery))));
    }

    [Fact]
    public async Task TypedFields_TagYearRangeAndWordMatch()
    {
        var seed = await SeedAsync();

        Assert.Equal(["Billy Bat", "Pluto"], Titles(await SearchAsync(seed.Admin, Q("tag:robots"))));
        Assert.Equal(["Pluto"], Titles(await SearchAsync(seed.Reader, Q("tag:Robots"))));
        Assert.Equal(["Monster", "Pluto"], Titles(await SearchAsync(seed.Admin, Q("year:1990..2005"))));
        Assert.Equal(["Yawara"], Titles(await SearchAsync(seed.Admin, Q("year:..1990"))));
        // Unquoted: a value that has all the typed words; quoted: only the exact value.
        Assert.Equal(["Pluto"], Titles(await SearchAsync(seed.Admin, Q("creator:urasawa"))));
        Assert.Empty(Titles(await SearchAsync(seed.Admin, Q("creator:\"urasawa\""))));
        // A typed tag only the other library carries: unmatched for the reader, never named.
        var hidden = await SearchAsync(seed.Reader, Q("tag:\"secret tag\""));
        Assert.Empty(hidden.Works!);
        Assert.False(Assert.Single(hidden.Filters!).Matched);
        Assert.Null(hidden.Filters![0].Display);
    }

    [Fact]
    public async Task FreeText_MatchesTitleAltTitleAndFolderName_AndANegatedWordExcludes()
    {
        var seed = await SeedAsync();
        var mystery = F("genre:" + seed.Mystery);

        Assert.Equal(["Pluto"], Titles(await SearchAsync(seed.Admin, $"{mystery}&{Q("kanzenban")}"))); // alternative title
        Assert.Equal(["Monster"], Titles(await SearchAsync(seed.Admin, $"{mystery}&{Q("perfect edition")}"))); // folder name, both words
        Assert.Equal(["Billy Bat", "Pluto"], Titles(await SearchAsync(seed.Admin, $"{mystery}&{Q("-perfect")}")));
        Assert.Equal(["Pluto"], Titles(await SearchAsync(seed.Admin, Q("series:kanzenban"))));
        // A folder name the reader cannot see does not match for them.
        Assert.Empty(Titles(await SearchAsync(seed.Reader, $"{mystery}&{Q("billy saga")}")));
    }

    [Fact]
    public async Task Refine_CountsTheResultsTags_PerViewer_AndLeavesTheFiltersOut()
    {
        var seed = await SeedAsync();
        var query = F("genre:" + seed.Mystery);

        // Admin first (fills the cache), then the reader with the SAME query: the reader's scope gives its own numbers.
        var admin = await SearchAsync(seed.Admin, query);
        var reader = await SearchAsync(seed.Reader, query);

        int? Count(SearchResultsDto r, FacetBucket bucket, string id) =>
            r.Refine!.FirstOrDefault(b => b.Bucket == bucket)?.Chips.FirstOrDefault(c => c.Id == id)?.Count;

        Assert.Equal(2, Count(admin, FacetBucket.Tags, seed.Robots));
        Assert.Equal(1, Count(admin, FacetBucket.Tags, seed.Secret));
        Assert.Equal(1, Count(admin, FacetBucket.Genres, seed.Horror));
        Assert.Null(Count(admin, FacetBucket.Genres, seed.Mystery)); // the filter itself is not offered again
        Assert.Equal(1, Count(reader, FacetBucket.Tags, seed.Robots));
        Assert.Null(Count(reader, FacetBucket.Tags, seed.Secret)); // only in library B: never named, never counted
        Assert.DoesNotContain(reader.Refine!.SelectMany(b => b.Chips), c => c.Display == "Secret Tag" || c.Display == "2008");

        // Refine comes with the first page only.
        var paged = await SearchAsync(seed.Admin, query + "&pageSize=1");
        Assert.NotNull(paged.Refine);
        var next = await SearchAsync(seed.Admin, query + "&pageSize=1&cursor=" + Uri.EscapeDataString(paged.NextCursor!));
        Assert.Null(next.Refine);
        // A plain search carries no Refine field at all.
        var plain = await FacetQueryTestSupport.GetRawAsync(seed.Admin, "/api/v1/search?q=Saga");
        Assert.DoesNotContain("\"refine\"", plain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InfoPanel_TagsRow_StrongestFirst_ComicInfoAfter_PlusMore_AndCountsPerViewer()
    {
        var seed = await SeedAsync();

        var panel = await FacetQueryTestSupport.GetAsync<InfoPanelDto>(seed.Admin, "/api/v1/nodes/tga1/info");
        var tags = Assert.Single(panel.Buckets, b => b.Bucket == FacetBucket.Tags);
        // The record's categories by net votes, then the tags the record does not rank (Robots, Comic Only) by name.
        Assert.Equal(seed.Ranked.Take(8).ToArray(), tags.Chips.Select(c => c.Id).ToArray());
        Assert.Equal(4, tags.More);
        var rest = await FacetQueryTestSupport.GetAsync<FacetBucketPageDto>(seed.Admin, "/api/v1/nodes/tga1/info/buckets/tag?offset=8");
        Assert.Equal(seed.Ranked.Skip(8).Append(seed.ComicOnly).Append(seed.Robots).ToArray(), rest.Chips.Select(c => c.Id).ToArray());
        Assert.Equal("tag:" + seed.Robots, rest.Chips[^1].Filter);

        // Robots: 2 works for the admin, 1 visible to the reader (quiet - no count).
        Assert.Equal(2, rest.Chips[^1].Count);
        var readerRest = await FacetQueryTestSupport.GetAsync<FacetBucketPageDto>(seed.Reader, "/api/v1/nodes/tga1/info/buckets/tags?offset=8");
        Assert.Null(readerRest.Chips.Single(c => c.Id == seed.Robots).Count);
        // The reader cannot open library B's node at all.
        Assert.Equal(HttpStatusCode.NotFound, await FacetQueryTestSupport.StatusAsync(seed.Reader, "/api/v1/nodes/tgb1/info"));
    }

    [Fact]
    public async Task ScopeWords_LibraryAndIs_ApplyToTheSearch()
    {
        var seed = await SeedAsync();
        var mystery = F("genre:" + seed.Mystery);

        Assert.Equal(["Billy Bat"], Titles(await SearchAsync(seed.Admin, $"{mystery}&{Q("library:\"tags lib b\"")}")));
        Assert.Equal(["Monster", "Pluto"], Titles(await SearchAsync(seed.Admin, $"{mystery}&{Q("library:\"tags lib a\"")}")));
        // A library the viewer may not list answers like an unknown one: empty.
        var hidden = await SearchAsync(seed.Reader, $"{mystery}&{Q("library:\"Tags Lib B\"")}");
        Assert.Empty(hidden.Works!);
        Assert.Equal(0, hidden.TotalCount);
        // is:read - nothing is read in this seed.
        Assert.Empty((await SearchAsync(seed.Admin, $"{mystery}&{Q("is:read")}")).Works!);
        Assert.Equal(3, (await SearchAsync(seed.Admin, $"{mystery}&{Q("is:unread")}")).TotalCount);
        // A plain search made only of scope words has nothing to search for (200, empty - no 400 for a typo).
        var plain = await SearchAsync(seed.Admin, Q("is:read"));
        Assert.Equal(0, plain.TotalCount);
        Assert.Null(plain.Works);
    }

    [Fact]
    public async Task MalformedFilters_Answer400()
    {
        var seed = await SeedAsync();
        foreach (var bad in new[] { "--genre:" + seed.Mystery, $"genre:{seed.Mystery}|", "genre:" + seed.Mystery + "|nope" })
        {
            using var response = await seed.Admin.GetAsync("/api/v1/search?" + F(bad));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
