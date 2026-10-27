namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Xunit;

/// <summary>
/// 1.49.0 lane J (owner decision 10 a): <c>GET libraries/{id}/jump-index</c> takes browse's filter params and answers the rail of the FILTERED
/// level, each cursor the key of the previous filtered row. Two readers of one library with different reading state and favorites prove the
/// filters are per viewer (the rail never shows another user's state), and a chip's cursor lands browse on the chip's first row.
/// </summary>
public sealed class JumpIndexFiltersHttpTests
{
    private static readonly string[] Names = ["Alder Story", "Birch Story", "Cedar Story", "Dogwood Story", "Elm Story", "Fir Story"];

    private sealed record Setup(HttpClient A, HttpClient B, long UserA, long UserB);

    private static async Task<(MangaPixerWebApplicationFactory Factory, Setup Readers)> SeedAsync()
    {
        var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var lib = await s.LibraryAsync("jf1", "Jump Filters");
        var ids = new Dictionary<string, long>();
        foreach (var name in Names)
            ids[name] = await s.NodeAsync(lib, null, "jf-" + name[..3].ToLowerInvariant(), name, folder: false);
        var a = await AccessTestUsers.ReaderAsync(factory, admin, "jfreadera", [lib]);
        var b = await AccessTestUsers.ReaderAsync(factory, admin, "jfreaderb", [lib]);

        await s.WithDbAsync(async db =>
        {
            // Reader A: Birch, Dogwood and Fir are read; Cedar is starred. Reader B has read and starred nothing.
            foreach (var name in new[] { "Birch Story", "Dogwood Story", "Fir Story" })
                db.ReadMarks.Add(new ReadMarkEntity { UserId = a.UserId, ItemId = ids[name], MarkedAt = DateTimeOffset.UtcNow });
            db.Favorites.Add(new FavoriteEntity { UserId = a.UserId, CatalogNodeId = ids["Cedar Story"], CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        return (factory, new Setup(a.Client, b.Client, a.UserId, b.UserId));
    }

    private static async Task<JumpIndexDto> RailAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync("/api/v1/libraries/jf1/jump-index" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JumpIndexDto>(TestJson.Web))!;
    }

    private static async Task<PageResponse<CatalogNodeDto>> BrowseAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<PageResponse<CatalogNodeDto>>("/api/v1/libraries/jf1/browse" + query, TestJson.Web))!;

    [Fact]
    public async Task TheRail_FollowsTheViewersReadStateFilter_AndEachChipLandsBrowseOnItsFirstRow()
    {
        var (factory, r) = await SeedAsync();
        await using var _ = factory;

        Assert.Equal(["A", "B", "C", "D", "E", "F"], (await RailAsync(r.A, string.Empty)).Buckets.Select(b => b.Label));

        var read = await RailAsync(r.A, "?readState=read");
        Assert.Equal(["B", "D", "F"], read.Buckets.Select(b => b.Label));
        Assert.All(read.Buckets, b => Assert.Equal(1, b.Count));
        Assert.Null(read.Buckets[0].FirstCursor);

        // Each chip's cursor, handed to browse under the same filter, lands on that chip's first row.
        foreach (var bucket in read.Buckets)
        {
            var query = "?readState=read&pageSize=1" + (bucket.FirstCursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(bucket.FirstCursor));
            var page = await BrowseAsync(r.A, query);
            Assert.Equal(bucket.FirstNodeId, page.Items.Single().Id);
        }

        var unread = await RailAsync(r.A, "?readState=unread");
        Assert.Equal(["A", "C", "E"], unread.Buckets.Select(b => b.Label));
        Assert.Equal(3, (await BrowseAsync(r.A, "?readState=unread")).TotalCount);
    }

    [Fact]
    public async Task TheRail_NeverShowsAnotherUsersReadingStateOrFavorites()
    {
        var (factory, r) = await SeedAsync();
        await using var _ = factory;

        // Reader B has read nothing: the Read filter leaves an empty level (no chips, no node ids), the Unread one lists everything.
        var none = await RailAsync(r.B, "?readState=read");
        Assert.Empty(none.Buckets);
        var raw = await r.B.GetStringAsync("/api/v1/libraries/jf1/jump-index?readState=read");
        Assert.DoesNotContain("jf-bir", raw, StringComparison.Ordinal);
        Assert.Equal(6, (await RailAsync(r.B, "?readState=unread")).Buckets.Count);

        // Starred: A has one favorite (one chip), B none (an empty level).
        Assert.Equal(["C"], (await RailAsync(r.A, "?favoritesOnly=true")).Buckets.Select(b => b.Label));
        Assert.Empty((await RailAsync(r.B, "?favoritesOnly=true")).Buckets);
    }

    [Fact]
    public async Task TheRail_ComposesFilters_AndIgnoresUnknownValues()
    {
        var (factory, r) = await SeedAsync();
        await using var _ = factory;

        // Read AND starred: nothing (the starred Cedar is unread).
        Assert.Empty((await RailAsync(r.A, "?readState=read&favoritesOnly=true")).Buckets);
        // Unread AND starred: Cedar alone.
        Assert.Equal(["C"], (await RailAsync(r.A, "?readState=unread&favoritesOnly=true")).Buckets.Select(b => b.Label));
        // hideEmpty keeps every archive; an unknown read state or Completion value is no filter, as in browse.
        Assert.Equal(6, (await RailAsync(r.A, "?hideEmpty=true")).Buckets.Count);
        Assert.Equal(6, (await RailAsync(r.A, "?readState=bogus&completion=12")).Buckets.Count);
        // Descending keeps its meaning under a filter: the chips reverse, the first one starts the listing.
        var desc = await RailAsync(r.A, "?readState=read&direction=desc");
        Assert.Equal(["F", "D", "B"], desc.Buckets.Select(b => b.Label));
        Assert.Null(desc.Buckets[0].FirstCursor);
    }
}
