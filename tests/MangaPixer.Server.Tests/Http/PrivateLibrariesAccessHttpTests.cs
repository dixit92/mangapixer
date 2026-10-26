namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.48.0 lane A2 (owner answer L3): <c>PUT reading/private-libraries</c> refuses an id the caller cannot open with 404, exactly as an
/// unknown library - for the whole request, nothing stored (a request mixing valid and invalid ids changes nothing). The list names only
/// libraries the caller can open; a mark stored earlier on a library the caller lost is kept as it is (neither listed nor dropped by a
/// PUT) and counts again when the grant comes back. The Settings page edits the set under Incognito too.
/// </summary>
public sealed class PrivateLibrariesAccessHttpTests
{
    private const string Route = "/api/v1/reading/private-libraries";

    [Fact]
    public async Task AnIdTheCallerCannotOpen_IsRefusedAsUnknown_ForTheWholeRequest_AndNothingIsStored()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var granted = await s.LibraryAsync("plG", "Private Granted");
        var mine = await s.LibraryAsync("plP", "Private Mine");
        var ungranted = await s.LibraryAsync("plU", "Private Ungranted");
        await s.NodeAsync(ungranted, null, "plUf", "Ungranted Folder");
        var reader = await AccessTestUsers.ReaderAsync(factory, admin, "plreader", [granted, mine]);

        // An ungranted id and an unknown id answer the same: 404 with the same body, nothing stored - alone or next to valid ids.
        var (ungrantedStatus, ungrantedBody) = await PutAsync(reader.Client, "plU");
        var (unknownStatus, unknownBody) = await PutAsync(reader.Client, "plnosuch");
        Assert.Equal(HttpStatusCode.NotFound, ungrantedStatus);
        Assert.Equal(HttpStatusCode.NotFound, unknownStatus);
        Assert.Equal(unknownBody, ungrantedBody);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(reader.Client, "plG", "plU")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(reader.Incognito, "plP", "plU")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(reader.Client, "plG", "plnosuch")).Status);
        Assert.Empty(await ListAsync(reader.Client));
        Assert.Equal(0, await s.WithDbAsync(db => db.PrivateLibraries.CountAsync(p => p.UserId == reader.UserId)));

        // Ids the caller can open are stored and listed back (also from an Incognito session: Settings edits the set there too).
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(reader.Client, "plG", "plP")).Status);
        Assert.Equal(["plG", "plP"], await ListAsync(reader.Client));
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(reader.Incognito, "plP")).Status);
        Assert.Equal(["plP"], await ListAsync(reader.Client));

        // A refused request left the stored set as it was.
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(reader.Client, "plG", "plU")).Status);
        Assert.Equal(["plP"], await ListAsync(reader.Client));

        // The admin may open every library: an existing id is accepted, an unknown one is not.
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(admin, "plU")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(admin, "plU", "plnosuch")).Status);
        Assert.Equal(["plU"], await ListAsync(admin));
    }

    [Fact]
    public async Task AMarkOnALibraryTheCallerLost_IsNotListed_SurvivesAPut_AndCountsAgainWithTheGrant()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var mine = await s.LibraryAsync("pkP", "Kept Mine");
        var lost = await s.LibraryAsync("pkL", "Kept Lost");
        await s.NodeAsync(lost, null, "pkLs", "Lost Story", folder: false);
        var reader = await AccessTestUsers.ReaderAsync(factory, admin, "pkreader", [mine, lost], [mine, lost]);
        Assert.Equal(["pkL", "pkP"], await ListAsync(reader.Client));

        // The grant of one Private library is revoked: the list no longer names it, and what the web sends back (the listed ids) is accepted.
        await s.WithDbAsync(db => db.LibraryGrants.Where(g => g.UserId == reader.UserId && g.LibraryId == lost).ExecuteDeleteAsync());
        Assert.Equal(["pkP"], await ListAsync(reader.Client));
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(reader.Client, "pkP")).Status);
        Assert.Equal(["pkP"], await ListAsync(reader.Client));
        // The stored mark itself stays (a PUT replaces the set within the libraries the caller can open).
        Assert.True(await s.WithDbAsync(db => db.PrivateLibraries.AnyAsync(p => p.UserId == reader.UserId && p.LibraryId == lost)));

        // The grant comes back: the library is Private again - listed, and hidden from browse under Incognito.
        await s.WithDbAsync(async db =>
        {
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = reader.UserId, LibraryId = lost, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        Assert.Equal(["pkL", "pkP"], await ListAsync(reader.Client));
        var browse = await FacetQueryTestSupport.GetAsync<PageResponse<CatalogNodeDto>>(reader.Incognito, "/api/v1/libraries/pkL/browse");
        Assert.Empty(browse.Items);
        var plain = await FacetQueryTestSupport.GetAsync<PageResponse<CatalogNodeDto>>(reader.Client, "/api/v1/libraries/pkL/browse");
        Assert.NotEmpty(plain.Items);
    }

    private static async Task<(HttpStatusCode Status, string Body)> PutAsync(HttpClient client, params string[] ids)
    {
        using var response = await client.PutAsJsonAsync(Route, new SetPrivateLibrariesRequest { LibraryIds = ids });
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<string>> ListAsync(HttpClient client)
    {
        var dto = await client.GetFromJsonAsync<PrivateLibrariesDto>(Route, TestJson.Web);
        return [.. dto!.LibraryIds.Order(StringComparer.Ordinal)];
    }
}
