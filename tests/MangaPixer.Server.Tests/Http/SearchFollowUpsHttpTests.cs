namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.50.0 lane S (search follow-ups, owner decisions 4 and 5), over HTTP: <c>-word</c> exclusion in the plain search and in quick results;
/// words spread over folder levels (<c>berserk 12</c> finds <c>Berserk/Vol 12.cbz</c>; at least one word in the node's own name; name
/// hits before cross-level hits); zero-padded numbers; two users and Incognito. Well-known public titles and synthetic file names only.
///
/// Seed - library A ("fua", every user of the test), library B ("fub", the admin only; Private for the reader):
///   A: Berserk/ { Vol 01.cbz, Vol 12.cbz, Volumes/ { Vol 13.cbz }, Berserk Deluxe/ { Deluxe 01.cbz } }
///      Monster/ { Monster 12.cbz, Vol 12.cbz, Ch. 0106.00 Vol. 006.cbz }
///      Spider-Man/ { Spider-Man 001.cbz }
///      Seinen/ Vinland Saga/ Chapters/ { Ch. 0012.cbz }
///      Akira/
///   B: Berserk Zq/ { Vol 12.cbz }
///   Monster is linked (Confirmed) to the record "Monster", alternative title "Kaibutsu Kanzenban".
/// </summary>
[Trait("Category", "Http")]
public sealed class SearchFollowUpsHttpTests
{
    private sealed record Seed(MangaPixerWebApplicationFactory Factory, HttpClient Admin, long LibraryA, long LibraryB, Dictionary<string, long> Ids);

    private static async Task<Seed> SetupAsync()
    {
        var factory = new MangaPixerWebApplicationFactory();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var now = DateTimeOffset.UtcNow;
        var a = new LibraryEntity { PublicId = "fua", DisplayName = "Follow-ups A", RootPath = "/tmp/fu-a", CreatedAt = now };
        var b = new LibraryEntity { PublicId = "fub", DisplayName = "Follow-ups B", RootPath = "/tmp/fu-b", CreatedAt = now };
        db.Libraries.AddRange(a, b);
        await db.SaveChangesAsync();
        var ids = new Dictionary<string, long>();

        async Task<CatalogNodeEntity> Add(string pid, LibraryEntity lib, CatalogNodeEntity? parent, CatalogNodeKind kind, string name)
        {
            var path = parent is null ? name : parent.RelativePath + "/" + name;
            var node = new CatalogNodeEntity
            {
                PublicId = pid,
                LibraryId = lib.Id,
                ParentId = parent?.Id,
                Kind = (int)kind,
                DisplayName = name,
                RelativePath = path,
                PathKey = path,
                SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name.ToLowerInvariant(),
                Availability = 0,
                CreatedAt = now,
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            ids[pid] = node.Id;
            return node;
        }

        var bs = await Add("bs", a, null, CatalogNodeKind.Folder, "Berserk");
        await Add("bs01", a, bs, CatalogNodeKind.Archive, "Vol 01.cbz");
        await Add("bs12", a, bs, CatalogNodeKind.Archive, "Vol 12.cbz");
        var bsv = await Add("bsv", a, bs, CatalogNodeKind.Folder, "Volumes");
        await Add("bs13", a, bsv, CatalogNodeKind.Archive, "Vol 13.cbz");
        var bsd = await Add("bsd", a, bs, CatalogNodeKind.Folder, "Berserk Deluxe");
        await Add("bsd1", a, bsd, CatalogNodeKind.Archive, "Deluxe 01.cbz");
        var mo = await Add("mo", a, null, CatalogNodeKind.Folder, "Monster");
        await Add("mov12", a, mo, CatalogNodeKind.Archive, "Monster 12.cbz");
        await Add("mo12", a, mo, CatalogNodeKind.Archive, "Vol 12.cbz");
        await Add("mo6", a, mo, CatalogNodeKind.Archive, "Ch. 0106.00 Vol. 006.cbz");
        var sm = await Add("sm", a, null, CatalogNodeKind.Folder, "Spider-Man");
        await Add("sm1", a, sm, CatalogNodeKind.Archive, "Spider-Man 001.cbz");
        var se = await Add("se", a, null, CatalogNodeKind.Folder, "Seinen");
        var vs = await Add("vs", a, se, CatalogNodeKind.Folder, "Vinland Saga");
        var vsc = await Add("vsc", a, vs, CatalogNodeKind.Folder, "Chapters");
        await Add("vs12", a, vsc, CatalogNodeKind.Archive, "Ch. 0012.cbz");
        var bz = await Add("bz", b, null, CatalogNodeKind.Folder, "Berserk Zq");
        await Add("bz12", b, bz, CatalogNodeKind.Archive, "Vol 12.cbz");
        await Add("ak", a, null, CatalogNodeKind.Folder, "Akira");

        // Monster is linked to a series record (title + an alternative title) - the Series matches row.
        var record = new MetadataRecordEntity
        {
            PublicId = "furec1",
            Provider = "mangaupdates",
            ExternalId = "515151",
            Title = "Monster",
            AltTitlesJson = "[\"Kaibutsu Kanzenban\"]",
            FetchedAt = now,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = mo.Id,
            LibraryId = a.Id,
            State = 0,
            RecordId = record.Id,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return new Seed(factory, admin, a.Id, b.Id, ids);
    }

    private static async Task<SearchResultsDto> SearchAsync(HttpClient client, string q)
    {
        var response = await client.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString(q));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SearchResultsDto>(TestJson.Web))!;
    }

    private static Task<SearchSuggestDto> SuggestAsync(HttpClient client, string q) =>
        FacetQueryTestSupport.GetAsync<SearchSuggestDto>(client, "/api/v1/search/suggest?q=" + Uri.EscapeDataString(q));

    private static List<string> Ids(SearchResultsDto page) => page.Items.Select(n => n.Id).ToList();

    // --- S-a: -word exclusion -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MinusWord_LeavesOutNamesHoldingIt_AndTheirArchivesFromTheFolderCount()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        var all = await SearchAsync(seed.Admin, "berserk");
        Assert.Contains("bsd", Ids(all));
        Assert.Equal(3, all.InsideCounts!["bs"]); // Vol 01, Vol 12, Volumes/Vol 13 (Berserk Deluxe is its own card)

        var without = await SearchAsync(seed.Admin, "berserk -deluxe");
        Assert.DoesNotContain("bsd", Ids(without));
        Assert.DoesNotContain("bsd1", Ids(without));
        Assert.Contains("bs", Ids(without));
        Assert.Contains("bz", Ids(without));
        // Berserk Deluxe is left out, and so is what is inside it: the Berserk card still counts its own three.
        Assert.Equal(3, without.InsideCounts!["bs"]);
        Assert.Equal(without.Items.Count, without.TotalCount);

        // A -word applies to the archives counted inside a card too ("vol" leaves out every "Vol NN").
        var noVolumes = await SearchAsync(seed.Admin, "berserk -vol");
        Assert.False(noVolumes.InsideCounts?.ContainsKey("bs") ?? false); // Vol 01 / 12 / 13 left out; Deluxe 01 is Berserk Deluxe's
        Assert.Equal(1, noVolumes.InsideCounts!["bsd"]);

        // A phrase after the minus: only names holding the words in that order.
        Assert.DoesNotContain("bsd", Ids(await SearchAsync(seed.Admin, "berserk -\"berserk deluxe\"")));
        Assert.Contains("bsd", Ids(await SearchAsync(seed.Admin, "berserk -\"deluxe berserk\"")));
    }

    [Fact]
    public async Task AHyphenInsideAWord_StillSeparatesWords_AndOnlyMinusTermsFindNothing()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        // 1.49.0 read "-berserk" as a search FOR berserk; a hyphen inside a name still separates words.
        Assert.Contains("sm", Ids(await SearchAsync(seed.Admin, "spider-man")));
        Assert.Contains("sm1", Ids(await SearchAsync(seed.Admin, "Spider-Man 001")));
        Assert.DoesNotContain("sm", Ids(await SearchAsync(seed.Admin, "spider -man")));

        var only = await SearchAsync(seed.Admin, "-berserk");
        Assert.Empty(only.Items);
        Assert.Equal(0, only.TotalCount);
        Assert.Empty((await SearchAsync(seed.Admin, "-berserk -monster")).Items);
    }

    [Fact]
    public async Task QuickResults_InheritTheMinusWord()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        var all = await SuggestAsync(seed.Admin, "berserk");
        Assert.Contains("Berserk Deluxe", all.Folders.Select(f => f.Label));

        var without = await SuggestAsync(seed.Admin, "berserk -deluxe");
        Assert.DoesNotContain("Berserk Deluxe", without.Folders.Select(f => f.Label));
        Assert.Contains("Berserk", without.Folders.Select(f => f.Label));
        Assert.Empty((await SuggestAsync(seed.Admin, "-berserk")).Folders);
    }

    // --- S-b: words across folder levels ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task WordsAcrossFolderLevels_FindTheTitleLessVolume_ThroughItsSeriesFolder()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        // The series title is only in the folder's name; the volume's own name holds "12".
        var berserk12 = await SearchAsync(seed.Admin, "berserk 12");
        Assert.Equal(["bs12", "bz12"], Ids(berserk12));
        Assert.Equal(2, berserk12.ArchiveCount);
        Assert.Equal(["bs12", "bz12"], Ids(await SearchAsync(seed.Admin, "12 BERSERK"))); // any order
        Assert.Equal(["bs13"], Ids(await SearchAsync(seed.Admin, "berserk 13")));       // a kind folder in between
        Assert.Equal(["vs12"], Ids(await SearchAsync(seed.Admin, "seinen vinland 12"))); // three levels, a zero-padded number
        // MangaList's title-less chapter name: a short number after its zeros, or a longer one inside the name.
        Assert.Equal(["mo6"], Ids(await SearchAsync(seed.Admin, "monster 6")));
        Assert.Equal(["mo6"], Ids(await SearchAsync(seed.Admin, "monster 106")));
    }

    [Fact]
    public async Task WordsAcrossFolderLevels_NameHitsFirst_AndOneWordStillListsTheFolderOnly()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        // "Monster 12.cbz" holds both words itself; "Monster/Vol 12.cbz" needs its folder: after it.
        Assert.Equal(["mov12", "mo12"], Ids(await SearchAsync(seed.Admin, "monster 12")));

        // At least one word in the node's own name: "berserk" alone lists folders, the volumes stay "+N inside".
        var berserk = await SearchAsync(seed.Admin, "berserk");
        Assert.Equal(["bs", "bsd", "bz"], Ids(berserk));
        Assert.Equal(3, berserk.InsideCounts!["bs"]);
        Assert.Equal(1, berserk.InsideCounts["bz"]);

        // A folder is a cross-level hit too: "Seinen/Vinland Saga" (one word each); "Seinen" alone lacks "vinland" above it.
        Assert.Equal(["vs"], Ids(await SearchAsync(seed.Admin, "seinen vinland")));
    }

    [Fact]
    public async Task MinusWord_NamingAnAncestor_LeavesOutEverythingInsideIt()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        Assert.Equal(["bs12", "bz12", "mo12"], Ids(await SearchAsync(seed.Admin, "vol 12")).OrderBy(i => i).ToList());
        Assert.Equal(["mo12"], Ids(await SearchAsync(seed.Admin, "vol 12 -berserk")));
        Assert.Equal(["bs12", "bz12"], Ids(await SearchAsync(seed.Admin, "vol 12 -monster")).OrderBy(i => i).ToList());
    }

    [Fact]
    public async Task WordsAcrossFolderLevels_TwoUsersAndIncognito_AnInvisibleLibraryNeverContributesAWord()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;
        var reader = await AccessTestUsers.ReaderAsync(seed.Factory, seed.Admin, "fureader", [seed.LibraryA], [seed.LibraryA]);

        Assert.Equal(["bz12"], Ids(await SearchAsync(seed.Admin, "zq 12")));
        // B is never granted: its folder's words find nothing for the reader.
        Assert.Empty((await SearchAsync(reader.Client, "zq 12")).Items);
        Assert.Equal(["bs12"], Ids(await SearchAsync(reader.Client, "berserk 12")));
        // A is Private for the reader: under Incognito nothing of it - folder or volume - is listed or contributes a word.
        Assert.Empty((await SearchAsync(reader.Incognito, "berserk 12")).Items);
        Assert.Empty((await SearchAsync(reader.Incognito, "berserk")).Items);
        Assert.Empty((await SuggestAsync(reader.Incognito, "berserk 12")).Archives);
        Assert.DoesNotContain("bz12", (await SuggestAsync(reader.Client, "zq 12")).Archives.Select(a => a.Id));
    }

    [Fact]
    public async Task QuickResults_ListTheTitleLessVolume_AfterTheNameHits()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        Assert.Equal(["bs12", "bz12"], (await SuggestAsync(seed.Admin, "berserk 12")).Archives.Select(a => a.Id));
        Assert.Equal(["mov12", "mo12"], (await SuggestAsync(seed.Admin, "monster 12")).Archives.Select(a => a.Id));
    }

    // --- S-c: the Series matches row with typos and 1-2 letters; quick-results typos (decision 5) -------------------------------------

    private static List<(string Id, string Title)> Series(SearchResultsDto page) =>
        (page.SeriesMatches ?? []).Select(m => (m.Node.Id, m.MatchedTitle)).ToList();

    [Fact]
    public async Task SeriesMatches_FindATitleWithATypo_AfterTheExactOnes()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        Assert.Equal([("mo", "Kaibutsu Kanzenban")], Series(await SearchAsync(seed.Admin, "kaibutsu")));    // exact (1.49.0 already)
        Assert.Equal([("mo", "Kaibutsu Kanzenban")], Series(await SearchAsync(seed.Admin, "kaibutzu")));    // one wrong letter
        Assert.Equal([("mo", "Monster")], Series(await SearchAsync(seed.Admin, "monstre")));                // two swapped
        Assert.Empty(Series(await SearchAsync(seed.Admin, "kaibutzu -kanzenban")));                         // a -word on the title
        Assert.Empty(Series(await SearchAsync(seed.Admin, "kaibu kanzenbaan monsterr")));                   // every word must be close
    }

    [Fact]
    public async Task SeriesMatches_FindATitleFromOneOrTwoLetters_ByAWordStart()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        Assert.Equal([("mo", "Monster")], Series(await SearchAsync(seed.Admin, "mo")));
        Assert.Equal([("mo", "Kaibutsu Kanzenban")], Series(await SearchAsync(seed.Admin, "ka")));
        Assert.Equal([("mo", "Kaibutsu Kanzenban")], Series(await SearchAsync(seed.Admin, "k k")));
        Assert.Empty(Series(await SearchAsync(seed.Admin, "on")));    // "on" starts no word of the titles
        // Only the libraries the viewer may see: under Incognito with A Private, nothing.
        var reader = await AccessTestUsers.ReaderAsync(seed.Factory, seed.Admin, "fuseries", [seed.LibraryA], [seed.LibraryA]);
        Assert.Equal([("mo", "Monster")], Series(await SearchAsync(reader.Client, "mo")));
        Assert.Empty(Series(await SearchAsync(reader.Incognito, "mo")));
        Assert.Empty(Series(await SearchAsync(reader.Incognito, "kaibutzu")));
    }

    [Fact]
    public async Task QuickResults_LookForTyposInOneWordOfFiveLetters_SeriesAndFolders()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        Assert.Equal(["mo"], (await SuggestAsync(seed.Admin, "kaibutzu")).Series.Select(i => i.Id));
        Assert.Equal(["mo"], (await SuggestAsync(seed.Admin, "monstre")).Series.Select(i => i.Id));
        Assert.Equal(["ak"], (await SuggestAsync(seed.Admin, "akria")).Folders.Select(i => i.Id));
        // Four letters, or two words: no typo look-up in quick results (the full search still has them).
        Assert.Empty((await SuggestAsync(seed.Admin, "akra")).Folders);
        Assert.Empty((await SuggestAsync(seed.Admin, "akria saga")).Folders);
        // A typo never lists an archive in quick results (folder names only - the budget).
        Assert.Empty((await SuggestAsync(seed.Admin, "deluxx")).Archives);
    }

    // --- After lane W: the archive context line on search and quick-results archive rows ------------------------------------------

    [Fact]
    public async Task ContextLine_SaysWhichSeriesATitleLessVolumeBelongsTo_InSearchAndQuickResults()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        var berserk12 = await SearchAsync(seed.Admin, "berserk 12");
        Assert.Equal([("bs12", "Berserk"), ("bz12", "Berserk Zq")], berserk12.Items.Select(n => (n.Id, n.ContextName)).ToList());
        // Through a kind folder: the series folder above "Volumes".
        Assert.Equal("Berserk", Assert.Single((await SearchAsync(seed.Admin, "berserk 13")).Items).ContextName);
        // Folders carry none.
        Assert.All((await SearchAsync(seed.Admin, "berserk")).Items, n => Assert.Null(n.ContextName));

        var quick = await SuggestAsync(seed.Admin, "berserk 12");
        Assert.Equal([("bs12", "Berserk"), ("bz12", "Berserk Zq")], quick.Archives.Select(a => (a.Id, a.ContextName)).ToList());
        Assert.All((await SuggestAsync(seed.Admin, "berserk")).Folders, f => Assert.Null(f.ContextName));

        // The reader sees only library A's volume, with its context; under Incognito (A Private) nothing.
        var reader = await AccessTestUsers.ReaderAsync(seed.Factory, seed.Admin, "fucontext", [seed.LibraryA], [seed.LibraryA]);
        Assert.Equal([("bs12", "Berserk")], (await SearchAsync(reader.Client, "berserk 12")).Items.Select(n => (n.Id, n.ContextName)).ToList());
        Assert.Empty((await SuggestAsync(reader.Incognito, "berserk 12")).Archives);
    }
}
