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
/// 1.49.0 lane S (owner decisions 4-6), over HTTP: words in any order, accents and punctuation ignored, quotes keep a phrase; names before
/// paths - an archive found only through its folder is counted on that folder's card ("+N inside"), every node is still found by its own
/// name; close matches for a typo only after every exact hit; a 1-2 character search matches a word start; the rank order; favourites
/// lifted on the server across pages; the counts by kind; and a second user who sees only their own libraries and their own favourites.
/// Well-known public titles and synthetic file names only.
/// </summary>
public sealed class SearchByWordsHttpTests
{
    private sealed record Seed(MangaPixerWebApplicationFactory Factory, HttpClient Admin, long LibraryA, long LibraryB, Dictionary<string, long> Ids);

    private static async Task<Seed> SetupAsync()
    {
        var factory = new MangaPixerWebApplicationFactory();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var now = DateTimeOffset.UtcNow;
        var a = new LibraryEntity { PublicId = "wordsa", DisplayName = "Words A", RootPath = "/tmp/words-a", CreatedAt = now };
        var b = new LibraryEntity { PublicId = "wordsb", DisplayName = "Words B", RootPath = "/tmp/words-b", CreatedAt = now };
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

        var op = await Add("op", a, null, CatalogNodeKind.Folder, "One Piece");
        await Add("opc1", a, op, CatalogNodeKind.Archive, "Chapter 001.cbz");
        await Add("opc2", a, op, CatalogNodeKind.Archive, "Chapter 002.cbz");
        await Add("opv1", a, op, CatalogNodeKind.Archive, "One Piece v01.cbz");
        var opcw = await Add("opcw", a, null, CatalogNodeKind.Folder, "One Piece Color Walk");
        await Add("opcw1", a, opcw, CatalogNodeKind.Archive, "Art 01.cbz");
        await Add("pom", a, null, CatalogNodeKind.Folder, "Piece of Mind");
        var pk = await Add("pk", a, null, CatalogNodeKind.Folder, "Pokémon Adventures");
        await Add("pk1", a, pk, CatalogNodeKind.Archive, "Vol 01.cbz");
        var bs = await Add("bs", a, null, CatalogNodeKind.Folder, "Berserk");
        await Add("bs1", a, bs, CatalogNodeKind.Archive, "Vol 01.cbz");
        await Add("bs2", a, bs, CatalogNodeKind.Archive, "Vol 02.cbz");
        var bsd = await Add("bsd", a, bs, CatalogNodeKind.Folder, "Berserk Deluxe");
        await Add("bsd1", a, bsd, CatalogNodeKind.Archive, "Deluxe 01.cbz");
        await Add("bt", a, null, CatalogNodeKind.Folder, "Bersek Tales");
        await Add("rz", a, null, CatalogNodeKind.Folder, "Re:Zero");
        await Add("ak", a, null, CatalogNodeKind.Folder, "Akira");
        await Add("dak", a, null, CatalogNodeKind.Archive, "Domu Akira Special.cbz");
        await Add("ops", b, null, CatalogNodeKind.Folder, "One Piece Secret");
        return new Seed(factory, admin, a.Id, b.Id, ids);
    }

    private static async Task<SearchResultsDto> SearchAsync(HttpClient client, string q, string extra = "")
    {
        var response = await client.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString(q) + extra);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SearchResultsDto>(TestJson.Web))!;
    }

    private static List<string> Ids(SearchResultsDto page) => page.Items.Select(n => n.Id).ToList();

    private static async Task SetFavoritesProminenceAsync(HttpClient client, bool on)
    {
        var current = await client.GetFromJsonAsync<LibraryViewPreferencesDto>("/api/v1/reading/library-preferences", TestJson.Web);
        (await client.PutAsJsonAsync("/api/v1/reading/library-preferences", current! with { FavoritesSearchProminence = on })).EnsureSuccessStatusCode();
    }

    private static async Task FavoriteAsync(Seed seed, long userId, params string[] pids)
    {
        using var scope = seed.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        foreach (var pid in pids)
            db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = seed.Ids[pid], CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task<long> AdminIdAsync(Seed seed)
    {
        using var scope = seed.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().Users
            .Where(u => u.NormalizedUserName == "ADMIN").Select(u => u.Id).SingleAsync();
    }

    [Fact]
    public async Task Words_MatchInAnyOrder_IgnoringAccentsAndPunctuation_AndQuotesKeepAPhrase()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        // Reversed words find the series (one literal phrase before 1.49.0 found nothing).
        Assert.Equal(["op", "opcw", "ops", "opv1"], Ids(await SearchAsync(seed.Admin, "piece one")));
        Assert.Equal(["pk"], Ids(await SearchAsync(seed.Admin, "pokemon")));
        Assert.Equal(["pk"], Ids(await SearchAsync(seed.Admin, "ADVENTURES, Pokémon!")));
        Assert.Equal(["rz"], Ids(await SearchAsync(seed.Admin, "re zero")));
        Assert.Equal(["rz"], Ids(await SearchAsync(seed.Admin, "Re:Zero")));

        // A quoted phrase keeps its order; the other words still match anywhere.
        Assert.Empty(Ids(await SearchAsync(seed.Admin, "\"piece one\"")));
        Assert.Equal(["opcw"], Ids(await SearchAsync(seed.Admin, "walk \"one piece\"")));
    }

    [Fact]
    public async Task NamesBeforePaths_PathOnlyArchivesFoldIntoTheirFolderCard_EveryNodeStillFoundByItsOwnName()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        var berserk = await SearchAsync(seed.Admin, "berserk");
        Assert.Equal(["bs", "bsd", "bt"], Ids(berserk)); // the two exact folders, then the close match
        Assert.Equal(3, berserk.FolderCount);
        Assert.Equal(0, berserk.ArchiveCount);
        Assert.Equal(3, berserk.TotalCount);
        // Vol 01 + Vol 02 inside Berserk; Deluxe 01 belongs to the Berserk Deluxe card (a hit of its own) and is not counted twice.
        Assert.Equal(2, berserk.InsideCounts!["bs"]);
        Assert.Equal(1, berserk.InsideCounts["bsd"]);
        Assert.False(berserk.InsideCounts.ContainsKey("bt"));

        var onePiece = await SearchAsync(seed.Admin, "one piece");
        Assert.Equal(2, onePiece.InsideCounts!["op"]); // the two chapters; "One Piece v01" is listed by its own name
        Assert.Equal(1, onePiece.InsideCounts["opcw"]);
        Assert.Equal(3, onePiece.FolderCount);
        Assert.Equal(1, onePiece.ArchiveCount);

        // An archive is found by its own name, linked or not.
        Assert.Equal(["bs1", "pk1"], Ids(await SearchAsync(seed.Admin, "vol 01")).Order());
    }

    [Fact]
    public async Task Ranking_ExactThenStartsWithThenAWordStartsThenContains_FoldersBeforeArchives()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        // exact (One Piece), starts with: folders by sort key, then the archive.
        Assert.Equal(["op", "opcw", "ops", "opv1"], Ids(await SearchAsync(seed.Admin, "one piece")));
        // "Piece of Mind" starts with the word; the One Piece names only have a word starting with it.
        Assert.Equal(["pom", "op", "opcw", "ops", "opv1"], Ids(await SearchAsync(seed.Admin, "piece")));
        // contains: "iece" starts no word.
        Assert.Equal(["op", "opcw", "ops", "pom", "opv1"], Ids(await SearchAsync(seed.Admin, "iece")));
        Assert.Equal(["ak", "dak"], Ids(await SearchAsync(seed.Admin, "akira")));
    }

    [Fact]
    public async Task CloseMatches_FollowEveryExactHit_AndAreCounted()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        // A typo: only close matches, flagged.
        var typo = await SearchAsync(seed.Admin, "berserc");
        Assert.Equal(["bs", "bsd"], Ids(typo));
        Assert.Equal(2, typo.CloseMatchCount);
        var swapped = await SearchAsync(seed.Admin, "pokmeon");
        Assert.Equal(["pk"], Ids(swapped));
        Assert.Equal(1, swapped.CloseMatchCount);

        // "Bersek Tales" sorts before "Berserk" by name, but a close match always follows the exact hits.
        var exact = await SearchAsync(seed.Admin, "berserk");
        Assert.Equal("bt", Ids(exact)[^1]);
        Assert.Equal(1, exact.CloseMatchCount);

        // A dropped letter in a 4-character word is one typo; a 3-character word never carries one; nothing close: no hits.
        var dropped = await SearchAsync(seed.Admin, "akra");
        Assert.Equal(["ak", "dak"], Ids(dropped));
        Assert.Equal(2, dropped.CloseMatchCount);
        Assert.Empty(Ids(await SearchAsync(seed.Admin, "akr")));
        Assert.Empty(Ids(await SearchAsync(seed.Admin, "zzzzzz")));
    }

    [Fact]
    public async Task ShortInput_MatchesAWordStartingWithIt_WithoutTypos()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        Assert.Equal(["ak", "dak"], Ids(await SearchAsync(seed.Admin, "ak")));
        Assert.Equal(["rz"], Ids(await SearchAsync(seed.Admin, "ze")));
        Assert.Empty(Ids(await SearchAsync(seed.Admin, "er")));
        var page = await SearchAsync(seed.Admin, "b");
        Assert.Equal(["bt", "bs", "bsd"], Ids(page)); // all "starts with": by sort key ("bersek" before "berserk")
        Assert.Equal(0, page.CloseMatchCount);
    }

    [Fact]
    public async Task Paging_UsesTheOpaqueRankCursor_AndAnOlderCursorRestartsAtPageOne()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;

        var walked = new List<string>();
        string? cursor = null;
        do
        {
            var page = await SearchAsync(seed.Admin, "piece", "&pageSize=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            walked.AddRange(Ids(page));
            Assert.Equal(5, page.TotalCount);
            cursor = page.HasMore ? page.NextCursor : null;
        }
        while (cursor is not null && walked.Count < 20);
        Assert.Equal(["pom", "op", "opcw", "ops", "opv1"], walked);

        var restarted = await SearchAsync(seed.Admin, "piece", "&pageSize=2&cursor=" + Uri.EscapeDataString("k:5:0one piece"));
        Assert.Equal(["pom", "op"], Ids(restarted));
    }

    [Fact]
    public async Task Favorites_AreLiftedOnTheServerAcrossPages_ButNeverAboveAnExactHit_AndPerViewer()
    {
        var seed = await SetupAsync();
        using var _ = seed.Factory;
        var adminId = await AdminIdAsync(seed);
        await FavoriteAsync(seed, adminId, "opv1", "bt");

        // Off (the default): the plain order.
        Assert.Equal(["pom", "op", "opcw", "ops", "opv1"], Ids(await SearchAsync(seed.Admin, "piece")));

        await SetFavoritesProminenceAsync(seed.Admin, true);
        // The favourite was last on page 3 of 1-result pages; it now leads page 1.
        Assert.Equal(["opv1"], Ids(await SearchAsync(seed.Admin, "piece", "&pageSize=1")));
        Assert.Equal(["opv1", "pom", "op", "opcw", "ops"], Ids(await SearchAsync(seed.Admin, "piece")));
        // A close-match favourite stays after the exact hits.
        Assert.Equal(["bs", "bsd", "bt"], Ids(await SearchAsync(seed.Admin, "berserk")));

        // A second user: only their granted library, and the admin's favourites do not move their results.
        var reader = await AccessTestUsers.ReaderAsync(seed.Factory, seed.Admin, "wordsreader", [seed.LibraryA]);
        await SetFavoritesProminenceAsync(reader.Client, true);
        var theirs = await SearchAsync(reader.Client, "piece");
        Assert.Equal(["pom", "op", "opcw", "opv1"], Ids(theirs));
        Assert.DoesNotContain("ops", Ids(await SearchAsync(reader.Client, "one piece secret")));
        Assert.Equal(4, theirs.TotalCount);
        Assert.Equal(3, theirs.FolderCount);

        // Their own favourite lifts for them only.
        await FavoriteAsync(seed, reader.UserId, "opcw");
        Assert.Equal("opcw", Ids(await SearchAsync(reader.Client, "piece"))[0]);
        Assert.Equal("opv1", Ids(await SearchAsync(seed.Admin, "piece"))[0]);
    }
}
