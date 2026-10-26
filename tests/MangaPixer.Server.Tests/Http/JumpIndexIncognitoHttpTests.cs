namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using Xunit;

/// <summary>
/// 1.48.0 lane A (owner decision 6b, found by lane Q): the jump rail follows the VISIBLE libraries under Incognito, as browse does - the
/// viewer's own Private library answers an empty rail (no letters, no node ids) while Incognito is on, at the root and in a folder; the
/// same library without Incognito keeps its rail, and nothing changes for an admin or a library that is not Private.
/// </summary>
public sealed class JumpIndexIncognitoHttpTests
{
    [Fact]
    public async Task TheRail_OfTheViewersPrivateLibrary_IsEmptyUnderIncognito_AsItsBrowse()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var privateLib = await s.LibraryAsync("jiP", "Jump Private");
        var plainLib = await s.LibraryAsync("jiG", "Jump Plain");
        foreach (var (lib, key) in new[] { (privateLib, "P"), (plainLib, "G") })
        {
            var folder = await s.NodeAsync(lib, null, $"ji{key}f", "Alder Folder");
            await s.NodeAsync(lib, null, $"ji{key}b", "Birch Story", folder: false);
            await s.NodeAsync(lib, folder, $"ji{key}c", "Cedar Story", folder: false);
            await s.NodeAsync(lib, folder, $"ji{key}d", "Dogwood Story", folder: false);
        }
        var reader = await AccessTestUsers.ReaderAsync(factory, admin, "jireader", [privateLib, plainLib], [privateLib]);

        foreach (var query in new[] { string.Empty, "?parentId=jiPf" })
        {
            // Without Incognito the Private library is the reader's like any other.
            var normal = await RailAsync(reader.Client, "jiP", query);
            Assert.NotEmpty(normal.Buckets);

            // Under Incognito: browse lists nothing there, and the rail says nothing either.
            var browse = await reader.Incognito.GetFromJsonAsync<PageResponse<CatalogNodeDto>>(
                "/api/v1/libraries/jiP/browse" + query, TestJson.Web);
            Assert.Empty(browse!.Items);
            var hidden = await RailAsync(reader.Incognito, "jiP", query);
            Assert.Empty(hidden.Buckets);
            var raw = await reader.Incognito.GetStringAsync("/api/v1/libraries/jiP/jump-index" + query);
            foreach (var id in new[] { "jiPb", "jiPc", "jiPd" })
                Assert.DoesNotContain(id, raw, StringComparison.Ordinal);
        }

        // A library that is not Private keeps its rail under Incognito; the admin (no Private set) is unchanged.
        Assert.NotEmpty((await RailAsync(reader.Incognito, "jiG", string.Empty)).Buckets);
        Assert.NotEmpty((await RailAsync(admin, "jiP", string.Empty)).Buckets);
    }

    private static async Task<JumpIndexDto> RailAsync(HttpClient client, string library, string query)
    {
        using var response = await client.GetAsync($"/api/v1/libraries/{library}/jump-index{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JumpIndexDto>(TestJson.Web))!;
    }
}
