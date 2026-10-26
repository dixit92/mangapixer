namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.48.0 lane A (LEAK found by the id sweep): <c>GET /api/v1/reading/{itemId}/effective-mode</c> resolved any item it was given - an item
/// in a library the caller cannot open answered 200 with that library's (or its folder's) default reader mode. It now answers 404, exactly
/// like an unknown item; the caller's own libraries (their Private one under Incognito included, a direct reader URL) are unchanged.
/// </summary>
public sealed class EffectiveModeAccessHttpTests
{
    [Fact]
    public async Task AnItemInALibraryTheCallerCannotOpen_AnswersLikeAnUnknownItem()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var granted = await s.LibraryAsync("emG", "Mode Granted");
        var hidden = await s.LibraryAsync("emU", "Mode Hidden");
        await s.NodeAsync(granted, null, "emGa", "Granted Strip", folder: false);
        await s.NodeAsync(hidden, null, "emUa", "Hidden Strip", folder: false);
        // Both libraries default to the vertical (webtoon) reader - the value an outsider must not learn.
        await s.WithDbAsync(async db =>
        {
            await db.Libraries.Where(l => l.Id == granted || l.Id == hidden).ExecuteUpdateAsync(u => u.SetProperty(l => l.DefaultReaderMode, (int?)3));
        });
        var reader = await AccessTestUsers.ReaderAsync(factory, admin, "emreader", [granted], [granted]);

        Assert.Equal("VerticalWebtoon", await ModeAsync(reader.Client, "emGa"));
        Assert.Equal("VerticalWebtoon", await ModeAsync(reader.Incognito, "emGa")); // own Private library: a direct URL keeps working
        Assert.Equal("VerticalWebtoon", await ModeAsync(admin, "emUa"));

        using var unknown = await reader.Client.GetAsync("/api/v1/reading/no-such-item/effective-mode");
        using var ungranted = await reader.Client.GetAsync("/api/v1/reading/emUa/effective-mode");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ungranted.StatusCode);
        Assert.DoesNotContain("Webtoon", await ungranted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<string?> ModeAsync(HttpClient client, string itemId)
    {
        using var response = await client.GetAsync($"/api/v1/reading/{itemId}/effective-mode");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("readerMode").GetString();
    }
}
