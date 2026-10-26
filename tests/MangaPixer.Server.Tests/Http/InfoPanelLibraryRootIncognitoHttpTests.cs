namespace com.lifepixer.mangapixer.Tests.Server.Http;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using Xunit;

/// <summary>
/// 1.48.0 lane A2 (owner answer L1): the library-root Info panel follows the VISIBLE libraries under Incognito, as the library's browse,
/// jump rail and rings do - the viewer's own Private library answers an empty panel (and empty bucket pages) while Incognito is on. Node
/// panels stay direct URLs (reachable under Incognito, as reader URLs); without Incognito, for a library that is not Private, and for an
/// admin nothing changes.
/// </summary>
public sealed class InfoPanelLibraryRootIncognitoHttpTests
{
    [Fact]
    public async Task TheRootPanel_OfTheViewersPrivateLibrary_IsEmptyUnderIncognito_NodePanelsStayDirect()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var privateLib = await s.LibraryAsync("ipP", "Info Private");
        var plainLib = await s.LibraryAsync("ipG", "Info Plain");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (lib, key) in new[] { (privateLib, "P"), (plainLib, "G") })
        {
            var folder = await s.NodeAsync(lib, null, $"ip{key}f", "Alder Saga");
            var work = await s.WorkAsync($"ip-{key}", "Alder Saga");
            var genre = await s.ValueAsync(FacetBucket.Genres, $"ip-genre-{key}", $"Quietwood {key}");
            await s.EntryAsync(work, folder, lib, genre.Id);
            values[key] = genre.PublicId;
        }
        s.Bump();
        var reader = await AccessTestUsers.ReaderAsync(factory, admin, "ipreader", [privateLib, plainLib], [privateLib]);

        // Without Incognito the Private library's panel is the reader's like any other.
        var normal = await FacetQueryTestSupport.GetAsync<InfoPanelDto>(reader.Client, "/api/v1/libraries/ipP/info");
        Assert.NotNull(FacetQueryTestSupport.Chip(normal, values["P"]));
        var normalPage = await FacetQueryTestSupport.GetAsync<FacetBucketPageDto>(reader.Client, "/api/v1/libraries/ipP/info/buckets/genre");
        Assert.Equal(1, normalPage.Total);

        // Under Incognito: its browse lists nothing, and the root panel and its bucket pages say nothing either.
        var browse = await FacetQueryTestSupport.GetAsync<PageResponse<CatalogNodeDto>>(reader.Incognito, "/api/v1/libraries/ipP/browse");
        Assert.Empty(browse.Items);
        var hidden = await FacetQueryTestSupport.GetAsync<InfoPanelDto>(reader.Incognito, "/api/v1/libraries/ipP/info");
        Assert.Equal(InfoPanelMode.None, hidden.Mode);
        Assert.Empty(hidden.Buckets);
        Assert.Equal("ipP", hidden.NodeId);
        var raw = await FacetQueryTestSupport.GetRawAsync(reader.Incognito, "/api/v1/libraries/ipP/info");
        Assert.DoesNotContain("Quietwood", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(values["P"], raw, StringComparison.Ordinal);
        var hiddenPage = await FacetQueryTestSupport.GetAsync<FacetBucketPageDto>(reader.Incognito, "/api/v1/libraries/ipP/info/buckets/genre");
        Assert.Empty(hiddenPage.Chips);
        Assert.Equal(0, hiddenPage.Total);
        Assert.Null(hiddenPage.NextOffset);
        Assert.DoesNotContain("Quietwood", await FacetQueryTestSupport.GetRawAsync(reader.Incognito, "/api/v1/libraries/ipP/info/buckets/genre"),
            StringComparison.Ordinal);

        // A folder's panel is a direct URL: still answered under Incognito (as the reader opens a direct link).
        var node = await FacetQueryTestSupport.GetAsync<InfoPanelDto>(reader.Incognito, "/api/v1/nodes/ipPf/info");
        Assert.NotNull(FacetQueryTestSupport.Chip(node, values["P"]));

        // A library that is not Private keeps its root panel under Incognito; the admin (no Private set) is unchanged.
        Assert.NotNull(FacetQueryTestSupport.Chip(
            await FacetQueryTestSupport.GetAsync<InfoPanelDto>(reader.Incognito, "/api/v1/libraries/ipG/info"), values["G"]));
        Assert.NotNull(FacetQueryTestSupport.Chip(
            await FacetQueryTestSupport.GetAsync<InfoPanelDto>(admin, "/api/v1/libraries/ipP/info", incognito: true), values["P"]));
    }
}
