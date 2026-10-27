namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Xunit;

/// <summary>
/// 1.49.0 lane O (owner decision 7): quick results while typing, <c>GET /api/v1/search/suggest</c>, through the public HTTP surface with TWO
/// users - the admin (every library) and a reader granted A and P only, with P marked Private for them (so Incognito hides it) and B never
/// granted. Each library carries a canary word that appears nowhere else; no group may ever name another library's canary. Grouped answers
/// (series with the matched alternative title, folders, archives, creators, tags), the ready tag-view filter of a value, the typed-field
/// values (<c>creator:ura</c>, <c>tag:</c>, <c>library:</c>, <c>is:</c>), series information hidden, a merged creator listed once.
/// Synthetic names only.
///
/// Seed - A "Quick Lib A" (reader), B "Quick Lib B" (nobody), P "Quick Lib P" (reader, Private):
///   A: folders "Berserk Deluxe" (7 archives "Berserk Deluxe 01..07.cbz"), "Berserk Prototype", "Monster Perfect Edition" (linked to the
///      record "Monster", alternative title "Kaibutsu Kanzenban"); creator Naoki Urasawa (+ a merged "Urasawa N" creator), tags "Time Travel"
///      (2 works), "Time Loop" (1 work), genre "Time Mystery"
///   B: folder "Berserk Zqsecret"; creator "Naoki Zqsecret"; tag "Time Zqsecret"; "Time Loop" too (2 works in all)
///   P: folder "Berserk Xqprivate"; tag "Time Xqprivate"
/// </summary>
[Trait("Category", "Http")]
public sealed class SearchSuggestHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private readonly FacetQueryTestSupport _support;
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static readonly Dictionary<MangaPixerWebApplicationFactory, Seed> Seeds = [];

    public SearchSuggestHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
        _support = new FacetQueryTestSupport(factory);
    }

    private sealed record Seed(HttpClient Admin, AccessReader Reader, long LibraryA, string UrasawaFilter);

    private async Task<Seed> SeedAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (Seeds.TryGetValue(_factory, out var existing))
                return existing;

            var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
            var s = _support;
            var libA = await s.LibraryAsync("qrA", "Quick Lib A");
            var libB = await s.LibraryAsync("qrB", "Quick Lib B");
            var libP = await s.LibraryAsync("qrP", "Quick Lib P");

            var deluxe = await s.NodeAsync(libA, null, "qra1", "Berserk Deluxe");
            for (var i = 1; i <= 7; i++)
                await s.NodeAsync(libA, deluxe, "qra1v" + i, $"Berserk Deluxe {i:00}.cbz", folder: false);
            var prototype = await s.NodeAsync(libA, null, "qra2", "Berserk Prototype");
            var monster = await s.NodeAsync(libA, null, "qra3", "Monster Perfect Edition");
            var b1 = await s.NodeAsync(libB, null, "qrb1", "Berserk Zqsecret");
            var p1 = await s.NodeAsync(libP, null, "qrp1", "Berserk Xqprivate");

            var record = await s.RecordAsync("78001", "Monster", "[\"Kaibutsu Kanzenban\"]");
            await s.WithDbAsync(async db =>
            {
                db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
                {
                    NodeId = monster,
                    LibraryId = libA,
                    State = 0,
                    RecordId = record,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            });

            var w1 = await s.WorkAsync("node:9201", "Monster Perfect Edition");
            var w2 = await s.WorkAsync("node:9202", "Berserk Prototype");
            var w3 = await s.WorkAsync("node:9203", "Berserk Zqsecret");
            var w4 = await s.WorkAsync("node:9204", "Berserk Xqprivate");

            var urasawa = await s.CreatorAsync("Naoki Urasawa", ["Urasawa Naoki"]);
            var urasawaValue = await s.ValueAsync(FacetBucket.Creators, "c:" + urasawa, "Naoki Urasawa", urasawa);
            // A creator an admin merged into the first: its own value stays in the index, but only the live creator is ever listed.
            var merged = await s.CreatorAsync("Urasawa N", [], mergedInto: urasawa);
            var mergedValue = await s.ValueAsync(FacetBucket.Creators, "c:" + merged, "Urasawa N", merged);
            var secretCreator = await s.CreatorAsync("Naoki Zqsecret", []);
            var secretCreatorValue = await s.ValueAsync(FacetBucket.Creators, "c:" + secretCreator, "Naoki Zqsecret", secretCreator);

            var travel = await s.ValueAsync(FacetBucket.Tags, "tg:time travel", "Time Travel");
            var loop = await s.ValueAsync(FacetBucket.Tags, "tg:time loop", "Time Loop");
            var secretTag = await s.ValueAsync(FacetBucket.Tags, "tg:time zqsecret", "Time Zqsecret");
            var privateTag = await s.ValueAsync(FacetBucket.Tags, "tg:time xqprivate", "Time Xqprivate");
            var mystery = await s.ValueAsync(FacetBucket.Genres, "g:time mystery", "Time Mystery");

            foreach (var (work, node, lib, value) in new[]
                     {
                         (w1, monster, libA, urasawaValue.Id), (w1, monster, libA, travel.Id), (w1, monster, libA, mystery.Id),
                         (w2, prototype, libA, travel.Id), (w2, prototype, libA, loop.Id), (w2, prototype, libA, mergedValue.Id),
                         (w3, b1, libB, secretCreatorValue.Id), (w3, b1, libB, secretTag.Id), (w3, b1, libB, loop.Id),
                         (w4, p1, libP, privateTag.Id),
                     })
                await s.EntryAsync(work, node, lib, value);

            var reader = await AccessTestUsers.ReaderAsync(_factory, admin, "qrreader", [libA, libP], [libP]);
            var seed = new Seed(admin, reader, libA, "creator:" + urasawaValue.PublicId);
            Seeds[_factory] = seed;
            return seed;
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private static Task<SearchSuggestDto> SuggestAsync(HttpClient client, string q, bool incognito = false) =>
        FacetQueryTestSupport.GetAsync<SearchSuggestDto>(client, "/api/v1/search/suggest?q=" + Uri.EscapeDataString(q), incognito);

    private static string[] Labels(IEnumerable<SuggestItemDto> items) => items.Select(i => i.Label).ToArray();

    private static string[] AllLabels(SearchSuggestDto dto) =>
        dto.Series.Concat(dto.Folders).Concat(dto.Archives).Concat(dto.Creators).Concat(dto.Tags)
            .SelectMany(i => new[] { i.Label, i.Detail ?? "" }).Where(l => l.Length > 0).ToArray();

    [Fact]
    public async Task Groups_ListFoldersAndArchives_FiveEach_InRankOrder()
    {
        var seed = await SeedAsync();

        var admin = await SuggestAsync(seed.Admin, "berserk");
        Assert.Equal(["Berserk Deluxe", "Berserk Prototype", "Berserk Xqprivate", "Berserk Zqsecret"], Labels(admin.Folders));
        Assert.Equal(5, admin.Archives.Count);
        Assert.Equal("Berserk Deluxe 01.cbz", admin.Archives[0].Label);
        // An archive names the folder it sits in; a node carries what the client needs to open it.
        Assert.Equal("Berserk Deluxe", admin.Archives[0].Detail);
        Assert.All(admin.Archives, a => Assert.Equal(("archive", "Archive"), (a.Kind, a.NodeKind)));
        var deluxe = admin.Folders[0];
        Assert.Equal(("folder", "Folder", "qra1", "qrA"), (deluxe.Kind, deluxe.NodeKind, deluxe.Id, deluxe.LibraryId));

        // Exact name before "contains": the folder named exactly like the text comes first.
        Assert.Equal("Berserk Deluxe", Labels((await SuggestAsync(seed.Admin, "berserk deluxe")).Folders)[0]);
        // Any order, accents and case ignored (the plain search's rules).
        Assert.Contains("Berserk Deluxe", Labels((await SuggestAsync(seed.Admin, "DELUXE berserk")).Folders));
    }

    [Fact]
    public async Task EmptyAndShortText_AnswerEmptyLists()
    {
        var seed = await SeedAsync();
        foreach (var q in new[] { "", "   ", "be" })
        {
            var dto = await SuggestAsync(seed.Admin, q);
            Assert.Empty(AllLabels(dto));
            Assert.Null(dto.Field);
        }
        var bare = await FacetQueryTestSupport.GetAsync<SearchSuggestDto>(seed.Admin, "/api/v1/search/suggest");
        Assert.Empty(bare.Folders);
    }

    [Fact]
    public async Task Series_AreFoundByAlternativeTitle_WithTheMatchedTitle()
    {
        var seed = await SeedAsync();
        var dto = await SuggestAsync(seed.Admin, "kaibutsu");
        var series = Assert.Single(dto.Series);
        Assert.Equal(("series", "Monster Perfect Edition", "aka Kaibutsu Kanzenban", "qra3", "qrA"),
            (series.Kind, series.Label, series.Detail, series.Id, series.LibraryId));
        // A linked series is listed as a series only, not again as a folder (as the search page does).
        Assert.DoesNotContain("Monster Perfect Edition", Labels((await SuggestAsync(seed.Admin, "monster")).Folders));
    }

    [Fact]
    public async Task Creators_AreListedOnce_WithTheReadyTagViewFilter_AndOnlyTheViewersCounts()
    {
        var seed = await SeedAsync();
        var dto = await SuggestAsync(seed.Admin, "urasawa");
        var creator = Assert.Single(dto.Creators);
        Assert.Equal(("creator", "Naoki Urasawa", seed.UrasawaFilter), (creator.Kind, creator.Label, creator.Filter));
        Assert.Equal(1, creator.Count);

        // The filter opens the tag view with that creator's work.
        var view = await FacetQueryTestSupport.GetAsync<SearchResultsDto>(seed.Admin, "/api/v1/search?f=" + Uri.EscapeDataString(creator.Filter!));
        Assert.Equal(["Monster Perfect Edition"], view.Works!.Select(w => w.Title).ToArray());

        // Another spelling of the same name finds the same creator.
        Assert.Equal(["Naoki Urasawa"], Labels((await SuggestAsync(seed.Admin, "naoki ura")).Creators));
    }

    [Fact]
    public async Task Tags_AndGenres_ShareOneGroup_WithPerViewerCounts()
    {
        var seed = await SeedAsync();
        var admin = await SuggestAsync(seed.Admin, "time");
        // The most used first: "Time Loop" has 2 works for the admin (A + B), "Time Travel" 2, then the singles.
        Assert.Equal(["Time Loop", "Time Travel", "Time Mystery", "Time Xqprivate", "Time Zqsecret"], Labels(admin.Tags));
        Assert.Equal(("tag", 2), (admin.Tags[0].Kind, admin.Tags[0].Count));
        Assert.Equal("genre", admin.Tags.Single(t => t.Label == "Time Mystery").Kind);

        var reader = await SuggestAsync(seed.Reader.Client, "time");
        Assert.Equal(["Time Travel", "Time Loop", "Time Mystery", "Time Xqprivate"], Labels(reader.Tags));
        // "Time Loop" is carried by one work in A and one in B: the reader counts only the one in A.
        Assert.Equal(1, reader.Tags.Single(t => t.Label == "Time Loop").Count);
    }

    [Fact]
    public async Task AnUngrantedLibrary_NeverAppearsInAnyGroup()
    {
        var seed = await SeedAsync();
        // The admin finds B's canary in every group that has one...
        var admin = await SuggestAsync(seed.Admin, "zqsecret");
        Assert.Equal(["Berserk Zqsecret"], Labels(admin.Folders));
        Assert.Equal(["Naoki Zqsecret"], Labels(admin.Creators));
        Assert.Equal(["Time Zqsecret"], Labels(admin.Tags));

        // ...the reader, who was never granted B, finds it in none - nor through a typed field.
        foreach (var client in new[] { seed.Reader.Client, seed.Reader.Incognito })
        {
            var dto = await SuggestAsync(client, "zqsecret");
            Assert.Empty(AllLabels(dto));
            Assert.Empty((await SuggestAsync(client, "creator:zqs")).Field!.Items);
            Assert.Empty((await SuggestAsync(client, "tag:zqs")).Field!.Items);
            Assert.DoesNotContain("Quick Lib B", (await SuggestAsync(client, "library:")).Field!.Items.Select(i => i.Label));
        }
    }

    [Fact]
    public async Task APrivateLibrary_UnderIncognito_NeverAppearsInAnyGroup()
    {
        var seed = await SeedAsync();
        var plain = await SuggestAsync(seed.Reader.Client, "xqprivate");
        Assert.Equal(["Berserk Xqprivate"], Labels(plain.Folders));
        Assert.Equal(["Time Xqprivate"], Labels(plain.Tags));
        Assert.Contains("Quick Lib P", (await SuggestAsync(seed.Reader.Client, "library:quick")).Field!.Items.Select(i => i.Label));

        var incognito = await SuggestAsync(seed.Reader.Incognito, "xqprivate");
        Assert.Empty(AllLabels(incognito));
        var libraries = (await SuggestAsync(seed.Reader.Incognito, "library:quick")).Field!.Items.Select(i => i.Label).ToArray();
        Assert.Equal(["Quick Lib A"], libraries);
    }

    [Fact]
    public async Task SeriesInformationHidden_GivesNoSeriesCreatorsOrTags_ButStillFindsNames()
    {
        var seed = await SeedAsync();
        await _support.SetSeriesInfoHiddenAsync(seed.LibraryA, true);
        try
        {
            var series = await SuggestAsync(seed.Reader.Client, "kaibutsu");
            Assert.Empty(series.Series);
            Assert.Empty((await SuggestAsync(seed.Reader.Client, "urasawa")).Creators);
            Assert.Empty((await SuggestAsync(seed.Reader.Client, "time travel")).Tags);
            Assert.Empty((await SuggestAsync(seed.Reader.Client, "creator:ura")).Field!.Items);
            // Folder names are not series information.
            Assert.Contains("Berserk Deluxe", Labels((await SuggestAsync(seed.Reader.Client, "berserk")).Folders));
            // Hidden in A only: the reader's other library still lists its tag.
            Assert.Equal(["Time Xqprivate"], Labels((await SuggestAsync(seed.Reader.Client, "time")).Tags));
        }
        finally
        {
            await _support.SetSeriesInfoHiddenAsync(seed.LibraryA, false);
        }

        await _support.SetSeriesInfoHiddenEverywhereAsync(true);
        try
        {
            var dto = await SuggestAsync(seed.Admin, "time");
            Assert.Empty(dto.Tags);
            Assert.Empty((await SuggestAsync(seed.Admin, "kaibutsu")).Series);
        }
        finally
        {
            await _support.SetSeriesInfoHiddenEverywhereAsync(false);
        }
    }

    [Fact]
    public async Task TypedField_ListsTheValuesOfThatField_AndCompletesTheToken()
    {
        var seed = await SeedAsync();

        var creator = (await SuggestAsync(seed.Admin, "creator:ura")).Field!;
        Assert.Equal(("creator", "ura", 0), (creator.Name, creator.Prefix, creator.Start));
        var item = Assert.Single(creator.Items);
        Assert.Equal(("Naoki Urasawa", "creator:\"Naoki Urasawa\"", 1), (item.Label, item.Insert, item.Count));

        // The token starts where the field starts; words before it stay.
        var tag = (await SuggestAsync(seed.Reader.Client, "berserk tag:tim")).Field!;
        Assert.Equal(("tag", "tim", 8), (tag.Name, tag.Prefix, tag.Start));
        Assert.Equal(["Time Travel", "Time Loop", "Time Mystery", "Time Xqprivate"], tag.Items.Select(i => i.Label).ToArray());
        Assert.Equal("tag:\"Time Travel\"", tag.Items[0].Insert);

        // An empty value lists the field's most used values; a negated field keeps its minus; an unfinished quote is a prefix.
        Assert.Equal("Time Mystery", (await SuggestAsync(seed.Reader.Client, "genre:")).Field!.Items.Single().Label);
        Assert.Equal("-tag:\"Time Travel\"", (await SuggestAsync(seed.Reader.Client, "-tag:\"time tra")).Field!.Items[0].Insert);

        // series: lists linked series titles (and alternative titles); is: its three values.
        Assert.Equal("series:\"Kaibutsu Kanzenban\"", (await SuggestAsync(seed.Admin, "series:kaibutsu")).Field!.Items.Single().Insert);
        Assert.Equal(["read", "reading"], (await SuggestAsync(seed.Admin, "is:rea")).Field!.Items.Select(i => i.Label).ToArray());
        Assert.Equal("library:\"Quick Lib A\"", (await SuggestAsync(seed.Reader.Incognito, "library:quick")).Field!.Items.Single().Insert);
    }

    [Fact]
    public async Task TextThatEndsInNoTypedField_AnswersTheGroups()
    {
        var seed = await SeedAsync();
        // A finished value (a space after it), a closed quote, an unknown prefix and a field in the middle are not "being typed".
        foreach (var q in new[] { "berserk creator:urasawa ", "creator:\"Naoki Urasawa\"", "Re:Zero", "creator:urasawa berserk" })
            Assert.Null((await SuggestAsync(seed.Admin, q)).Field);
        // A field typed earlier narrows the search page, not the names: the free text still finds folders.
        Assert.Contains("Berserk Deluxe", Labels((await SuggestAsync(seed.Admin, "berserk creator:urasawa ")).Folders));
        Assert.Contains("Berserk Deluxe", Labels((await SuggestAsync(seed.Admin, "creator:urasawa berserk")).Folders));
    }

    [Fact]
    public async Task Unauthenticated_Is401()
    {
        await SeedAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await FacetQueryTestSupport.StatusAsync(_factory.CreateClient(), "/api/v1/search/suggest?q=berserk"));
    }
}

/// <summary>1.49.0 lane O: the typed field a text ends in (token rules of the search syntax) - pure.</summary>
[Trait("Category", "Unit")]
public sealed class SearchSuggestSyntaxTests
{
    [Theory]
    [InlineData("creator:ura", 0, false, "creator", "ura")]
    [InlineData("berserk creator:ura", 8, false, "creator", "ura")]
    [InlineData("creator:", 0, false, "creator", "")]
    [InlineData("creator: ", 0, false, "creator", "")]
    [InlineData("creator: ura", 0, false, "creator", "ura")]
    [InlineData("creators:ura", 0, false, "creator", "ura")]
    [InlineData("CREATOR:Ura", 0, false, "creator", "Ura")]
    [InlineData("-genre:hor", 0, true, "genre", "hor")]
    [InlineData("x -genre:", 2, true, "genre", "")]
    [InlineData("creator:\"Naoki Ur", 0, false, "creator", "Naoki Ur")]
    [InlineData("tag:\"", 0, false, "tag", "")]
    [InlineData("is:rea", 0, false, "is", "rea")]
    [InlineData("library:man", 0, false, "library", "man")]
    [InlineData("series:mon", 0, false, "series", "mon")]
    [InlineData("genre:horror|my", 0, false, "genre", "horror|my")]
    [InlineData("year:2010..", 0, false, "year", "2010..")]
    public void EndsInAField(string text, int start, bool negated, string name, string prefix)
    {
        var field = SearchSuggestSyntax.Trailing(text);
        Assert.NotNull(field);
        Assert.Equal((start, negated, name, prefix), (field.Start, field.Negated, field.Name, field.Prefix));
    }

    [Theory]
    [InlineData("")]
    [InlineData("berserk")]
    [InlineData("Re:Zero")]
    [InlineData("creator:urasawa ")]
    [InlineData("creator:\"Naoki Urasawa\"")]
    [InlineData("creator:ura berserk")]
    [InlineData("-is:read")]
    [InlineData("-library:man")]
    [InlineData("colour:red")]
    [InlineData(":ura")]
    public void EndsInNoField(string text) =>
        Assert.Null(SearchSuggestSyntax.Trailing(text));

    [Theory]
    [InlineData(false, "creator", "Naoki Urasawa", "creator:\"Naoki Urasawa\"")]
    [InlineData(false, "tag", "Robots", "tag:Robots")]
    [InlineData(true, "genre", "Slice of Life", "-genre:\"Slice of Life\"")]
    [InlineData(false, "tag", "a|b", "tag:\"a|b\"")]
    [InlineData(false, "tag", "Say \"hi\"", "tag:\"Say hi\"")]
    public void TheTokenIsQuotedWhenNeeded(bool negated, string name, string value, string expected) =>
        Assert.Equal(expected, SearchSuggestSyntax.Token(negated, name, value));
}
