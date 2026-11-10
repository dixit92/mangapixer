namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Xunit;

/// <summary>
/// 1.53.0 lane C, through the real pipeline: <c>GET recommendations</c> (Home "Recommended for you") and <c>GET nodes/{id}/similar</c>
/// ("More like this") with two readers and an Incognito session. The tag index is seeded directly (synthetic names).
/// <code>
///   library rcA (both readers):   W1 "Pluto"  @rcW1 (folder, archive rcW1x)  creator Naoki Urasawa, Horror    record R1 lists R5 as "sequel"
///                                 W2 "Monster" @rcW2                         creator Naoki Urasawa
///                                 W3 "Ghost"   @rcW3                         Horror, School Life
///                                 W4 "Keaton"  @rcW4 (archive rcW4x)         creator Naoki Urasawa       (reader A has read rcW4x)
///                                 W5 "Pluto Two" @rcW5                       Drama                       record R5
///   library rcB (reader B only):  W6 "Billy"   @rcW6                         creator Naoki Urasawa
///   library rcP (both; Private for reader A): W7 "Yawara" @rcW7              creator Naoki Urasawa
///   genre Seinen on every work (in every scope: weight 0).
/// </code>
/// Reader A rates W1 10 (a seed). Reader B has no signal.
/// </summary>
[Trait("Category", "Http")]
public sealed class RecommendationsHttpTests
{
    private sealed record World(MangaPixerWebApplicationFactory Factory, HttpClient Admin, AccessReader A, AccessReader B, FacetQueryTestSupport S,
        long LibA, long LibB, long LibP);

    private static async Task<World> SeedAsync()
    {
        var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var libA = await s.LibraryAsync("rcA", "Rec Lib A");
        var libB = await s.LibraryAsync("rcB", "Rec Lib B");
        var libP = await s.LibraryAsync("rcP", "Rec Lib P Xqprivate");

        async Task<long> Folder(long lib, string id, string name) => await s.WithDbAsync(async db =>
        {
            var node = new CatalogNodeEntity
            {
                PublicId = id,
                LibraryId = lib,
                Kind = (int)CatalogNodeKind.Folder,
                DisplayName = name,
                RelativePath = id,
                PathKey = id,
                SortKey = SortKey.ForNode(CatalogNodeKind.Folder, name),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            return node.Id;
        });
        async Task<long> Archive(long lib, long parent, string id, string name) => await s.WithDbAsync(async db =>
        {
            var node = new CatalogNodeEntity
            {
                PublicId = id,
                LibraryId = lib,
                ParentId = parent,
                Kind = (int)CatalogNodeKind.Archive,
                DisplayName = name,
                RelativePath = id,
                PathKey = id,
                SortKey = SortKey.ForNode(CatalogNodeKind.Archive, name),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            return node.Id;
        });

        var n1 = await Folder(libA, "rcW1", "Pluto Saga");
        await Archive(libA, n1, "rcW1x", "Pluto 01");
        var n2 = await Folder(libA, "rcW2", "Monster Saga");
        await Archive(libA, n2, "rcW2x", "Monster 01");
        var n3 = await Folder(libA, "rcW3", "Ghost Saga");
        var n4 = await Folder(libA, "rcW4", "Keaton Saga");
        var n4x = await Archive(libA, n4, "rcW4x", "Keaton 01");
        var n5 = await Folder(libA, "rcW5", "Pluto Two Saga");
        var n6 = await Folder(libB, "rcW6", "Billy Saga Zqhidden");
        var n7 = await Folder(libP, "rcW7", "Yawara Saga Xqprivate");

        var r1 = await s.RecordAsync("5001", "Pluto");
        var r5 = await s.RecordAsync("6001", "Pluto Two");
        await s.WithDbAsync(async db =>
        {
            var record = await db.MetadataRecords.FindAsync(r1);
            record!.RelationsJson = """[{"externalId":"6001","relation":"sequel"}]""";
            await db.SaveChangesAsync();
        });

        var w1 = await s.WorkAsync("rec:" + r1, "Pluto", r1);
        var w2 = await s.WorkAsync("node:" + n2, "Monster");
        var w3 = await s.WorkAsync("node:" + n3, "Ghost");
        var w4 = await s.WorkAsync("node:" + n4, "Keaton");
        var w5 = await s.WorkAsync("rec:" + r5, "Pluto Two", r5);
        var w6 = await s.WorkAsync("node:" + n6, "Billy Zqhidden");
        var w7 = await s.WorkAsync("node:" + n7, "Yawara Xqprivate");

        var urasawa = await s.CreatorAsync("Naoki Urasawa", []);
        var creator = await s.ValueAsync(FacetBucket.Creators, "c:" + urasawa, "Naoki Urasawa", urasawa);
        var horror = await s.ValueAsync(FacetBucket.Tags, "horror", "Horror");
        var school = await s.ValueAsync(FacetBucket.Tags, "school life", "School Life");
        var drama = await s.ValueAsync(FacetBucket.Genres, "drama", "Drama");
        var seinen = await s.ValueAsync(FacetBucket.Genres, "seinen", "Seinen");

        foreach (var (work, node, lib) in new[] { (w1, n1, libA), (w2, n2, libA), (w4, n4, libA), (w6, n6, libB), (w7, n7, libP) })
            await s.EntryAsync(work, node, lib, creator.Id);
        await s.EntryAsync(w1, n1, libA, horror.Id);
        await s.EntryAsync(w3, n3, libA, horror.Id);
        await s.EntryAsync(w3, n3, libA, school.Id);
        await s.EntryAsync(w5, n5, libA, drama.Id);
        foreach (var (work, node, lib) in new[] { (w1, n1, libA), (w2, n2, libA), (w3, n3, libA), (w4, n4, libA), (w5, n5, libA), (w6, n6, libB), (w7, n7, libP) })
            await s.EntryAsync(work, node, lib, seinen.Id);
        s.Bump();

        var a = await AccessTestUsers.ReaderAsync(factory, admin, "rcreadera", [libA, libP], privateLibraryIds: [libP]);
        var b = await AccessTestUsers.ReaderAsync(factory, admin, "rcreaderb", [libA, libB, libP]);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.PutAsJsonAsync("/api/v1/nodes/rcW1/rating", new { score = 10 })).StatusCode);
        await s.WithDbAsync(async db =>
        {
            db.ReadMarks.Add(new ReadMarkEntity { UserId = a.UserId, ItemId = n4x, MarkedAt = DateTimeOffset.UtcNow, Source = "manual" });
            await db.SaveChangesAsync();
        });
        return new World(factory, admin, a, b, s, libA, libB, libP);
    }

    private static async Task<RecommendationsDto> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RecommendationsDto>(TestJson.Web))!;
    }

    private static Task<RecommendationsDto> HomeAsync(HttpClient client) => GetAsync(client, "/api/v1/recommendations?limit=12");

    private static List<string> Ids(RecommendationsDto dto) => dto.Items.Select(i => i.Node.Id).ToList();

    [Fact]
    public async Task Home_RecommendsFromTheReadersOwnSeeds_WithOneReasonEach_AndSkipsSeedsReadAndUngranted()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        var home = await HomeAsync(w.A.Client);
        var ids = Ids(home);
        Assert.Equal("rcW2", ids[0]); // the same author, the strongest share
        Assert.Contains("rcW3", ids); // Horror
        Assert.Contains("rcW5", ids); // the stored relation: R1 lists R5 as its sequel
        Assert.Contains("rcW7", ids); // the reader's Private library, outside Incognito
        Assert.DoesNotContain("rcW1", ids); // the seed itself
        Assert.DoesNotContain("rcW4", ids); // read
        Assert.DoesNotContain("rcW6", ids); // a library the reader may not open

        var byId = home.Items.ToDictionary(i => i.Node.Id);
        Assert.Equal(RecommendationReasonKind.Creator, byId["rcW2"].Reason.Kind);
        Assert.Equal("Same author: Naoki Urasawa", byId["rcW2"].Reason.Display);
        Assert.Null(byId["rcW2"].Reason.RelatedNodeId);
        Assert.Equal(RecommendationReasonKind.SharedValues, byId["rcW3"].Reason.Kind);
        Assert.Equal("Like Pluto: Horror", byId["rcW3"].Reason.Display);
        Assert.Equal("rcW1", byId["rcW3"].Reason.RelatedNodeId);
        Assert.Equal(RecommendationReasonKind.Relation, byId["rcW5"].Reason.Kind);
        Assert.Equal("Sequel of Pluto", byId["rcW5"].Reason.Display);

        // Cards come through the browse enrichment: a folder with a cover rule, its library and parent ids.
        Assert.Equal(CatalogNodeKind.Folder, byId["rcW2"].Node.Kind);
        Assert.Equal("rcA", byId["rcW2"].Node.LibraryId);
    }

    [Fact]
    public async Task Home_TwoUsers_NeverShareSignals_AndIncognitoLeavesThePrivateLibraryOut()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        // Reader B has no seed of their own: nothing, although A rated W1 and B sees everything A sees and more.
        Assert.Empty((await HomeAsync(w.B.Client)).Items);

        // B stars W3: B's answer comes from B's own seed only (W1 is A's seed, not B's: it is a pick for B).
        using (var star = await w.B.Client.PostAsync("/api/v1/nodes/rcW3/favorite", null))
            Assert.True(star.IsSuccessStatusCode, $"favorite {(int)star.StatusCode}");
        var b = Ids(await HomeAsync(w.B.Client));
        Assert.Contains("rcW1", b); // Horror; A's seed is no seed of B's
        Assert.DoesNotContain("rcW3", b);

        // A: the Private library leaves the row under Incognito (and no canary of it is in the body).
        var incognito = await w.A.Incognito.GetStringAsync("/api/v1/recommendations");
        Assert.DoesNotContain("rcW7", incognito, StringComparison.Ordinal);
        Assert.DoesNotContain("Xqprivate", incognito, StringComparison.Ordinal);
        Assert.Contains("rcW2", incognito, StringComparison.Ordinal);
        // ...and nothing of a library A may not open, under either session.
        Assert.DoesNotContain("Zqhidden", await w.A.Client.GetStringAsync("/api/v1/recommendations"), StringComparison.Ordinal);
        Assert.DoesNotContain("Zqhidden", incognito, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_ASignalChange_ChangesTheNextAnswer_AndHiddenLibrariesLeaveTheRow()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;
        Assert.Contains("rcW2", Ids(await HomeAsync(w.A.Client)));

        // A neutral rating (3 stars) is no seed: what came from W1 (Horror, the relation) leaves at once - the cached answer is keyed by the
        // signal stamp. W4 (finished: its one archive is read) is still a seed, so the same author stays; W1 itself, rated, is no pick.
        Assert.Equal(HttpStatusCode.NoContent, (await w.A.Client.PutAsJsonAsync("/api/v1/nodes/rcW1/rating", new { score = 6 })).StatusCode);
        var neutral = Ids(await HomeAsync(w.A.Client));
        Assert.DoesNotContain("rcW3", neutral);
        Assert.DoesNotContain("rcW5", neutral);
        Assert.DoesNotContain("rcW1", neutral);
        Assert.Contains("rcW2", neutral);

        // Cleared: W1 is a candidate again (the same author as the finished W4).
        Assert.Equal(HttpStatusCode.NoContent, (await w.A.Client.DeleteAsync("/api/v1/nodes/rcW1/rating")).StatusCode);
        var cleared = Ids(await HomeAsync(w.A.Client));
        Assert.Contains("rcW1", cleared);
        Assert.DoesNotContain("rcW4", cleared);

        // A favourite of W1 makes it a seed again (Horror is back); then hiding library rcP from Home drops W7.
        using (var star = await w.A.Client.PostAsync("/api/v1/nodes/rcW1/favorite", null))
            Assert.True(star.IsSuccessStatusCode, $"favorite {(int)star.StatusCode}");
        var starred = Ids(await HomeAsync(w.A.Client));
        Assert.Contains("rcW3", starred);
        Assert.Contains("rcW7", starred);
        using (var hide = await w.A.Client.PutAsJsonAsync("/api/v1/reading/home-libraries", new { excludedLibraryIds = new[] { "rcP" } }))
            Assert.True(hide.IsSuccessStatusCode, $"home-libraries {(int)hide.StatusCode}");
        var hidden = Ids(await HomeAsync(w.A.Client));
        Assert.DoesNotContain("rcW7", hidden);
        Assert.Contains("rcW2", hidden);
    }

    [Fact]
    public async Task Similar_IsTheNodesOwnWork_ReadLast_404WithoutAccess_AndEnrichedPerViewer()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        // B rates W2 3 stars (neutral): "More like this" still lists it, with B's own rating on the card.
        Assert.Equal(HttpStatusCode.NoContent, (await w.B.Client.PutAsJsonAsync("/api/v1/nodes/rcW2/rating", new { score = 6 })).StatusCode);

        // From an archive inside W1's folder: the nearest series folder's work is the seed.
        var forA = await GetAsync(w.A.Client, "/api/v1/nodes/rcW1x/similar");
        var a = Ids(forA);
        Assert.DoesNotContain("rcW1", a);
        Assert.Contains("rcW2", a);
        Assert.Contains("rcW4", a);
        Assert.Equal("rcW4", a[^1]); // A read it: after the unread ones
        Assert.Equal(FolderReadRollup.Read, forA.Items[^1].Node.ReadRollup);
        Assert.Equal("Horror", forA.Items.Single(i => i.Node.Id == "rcW3").Reason.Display); // no "Like <this page>" on its own page
        Assert.Equal("Sequel of Pluto", forA.Items.Single(i => i.Node.Id == "rcW5").Reason.Display);
        Assert.Null(forA.Items.Single(i => i.Node.Id == "rcW2").Node.MyRating);

        var forB = await GetAsync(w.B.Client, "/api/v1/nodes/rcW1/similar");
        Assert.Equal("rcW2", Ids(forB)[0]); // nothing read by B: rank order
        Assert.Equal(6, forB.Items[0].Node.MyRating);
        Assert.Contains("rcW6", Ids(forB)); // B may open rcB

        // A may not open rcB: 404, as unknown; and nothing of rcB in A's answer.
        Assert.Equal(HttpStatusCode.NotFound, (await w.A.Client.GetAsync("/api/v1/nodes/rcW6/similar")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.A.Client.GetAsync("/api/v1/nodes/nosuchnode/similar")).StatusCode);
        Assert.DoesNotContain("rcW6", a);

        // A direct URL to A's own Private library works under Incognito; its answer leaves the Private library out.
        var privatePage = await GetAsync(w.A.Incognito, "/api/v1/nodes/rcW7/similar");
        Assert.Contains("rcW2", Ids(privatePage));
        Assert.DoesNotContain("rcW7", Ids(privatePage));
        Assert.DoesNotContain("rcW7", Ids(await GetAsync(w.A.Incognito, "/api/v1/nodes/rcW1/similar")));
        Assert.Contains("rcW7", Ids(await GetAsync(w.A.Client, "/api/v1/nodes/rcW1/similar")));
    }

    [Fact]
    public async Task SeriesInformationHidden_EmptiesBothAnswers()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;
        await w.S.SetSeriesInfoHiddenAsync(w.LibA, true);

        Assert.Empty((await GetAsync(w.A.Client, "/api/v1/nodes/rcW1/similar")).Items);
        // A's seeds are all in rcA, whose tags are hidden now: no seed, no row.
        Assert.Empty((await HomeAsync(w.A.Client)).Items);
    }
}
