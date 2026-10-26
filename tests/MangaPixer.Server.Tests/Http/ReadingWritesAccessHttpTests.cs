namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Text;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.48.0 lane A2 (owner answer L4): the six reading-state writes - <c>PUT / DELETE reading/progress/{id}</c>, <c>PUT / DELETE
/// reading/{id}/read</c>, <c>DELETE reading/continue/{id}</c>, <c>POST reading/{id}/bookmarks</c> - answer an item in a library the caller
/// cannot open with 404, exactly as an unknown item and as the reads (they answered 401 "unauthorized" to a signed-in reader before), and
/// store nothing. The same writes on a granted item still work (the control).
/// </summary>
public sealed class ReadingWritesAccessHttpTests
{
    private static readonly (string Method, string Route, string? Body)[] Writes =
    [
        ("PUT", "/api/v1/reading/progress/{0}", "{\"pageIndex\":1,\"expectedContentVersion\":1,\"mutationId\":\"rw-{1}\",\"normalizedAnchor\":0}"),
        ("DELETE", "/api/v1/reading/progress/{0}", null),
        ("PUT", "/api/v1/reading/{0}/read", "{}"),
        ("DELETE", "/api/v1/reading/{0}/read", null),
        ("DELETE", "/api/v1/reading/continue/{0}", null),
        ("POST", "/api/v1/reading/{0}/bookmarks", "{\"ordinal\":1,\"label\":\"rw\"}"),
    ];

    [Fact]
    public async Task TheSixWrites_AnswerAnInaccessibleItemAsUnknown_404_AndStoreNothing()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var granted = await s.LibraryAsync("rwG", "Writes Granted");
        var ungranted = await s.LibraryAsync("rwU", "Writes Ungranted");
        var grantedItem = await s.NodeAsync(granted, null, "rwGi", "Granted Story", folder: false);
        var ungrantedItem = await s.NodeAsync(ungranted, null, "rwUi", "Ungranted Story", folder: false);
        await s.WithDbAsync(async db =>
        {
            foreach (var id in new[] { grantedItem, ungrantedItem })
                db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = id, ContentVersion = 1, AnalysisState = 0, PageCount = 12, ByteLength = 1024 });
            await db.SaveChangesAsync();
        });
        var reader = await AccessTestUsers.ReaderAsync(factory, admin, "rwreader", [granted]);

        var failures = new List<string>();
        var n = 0;
        foreach (var (method, route, body) in Writes)
        {
            foreach (var client in new[] { reader.Client, reader.Incognito })
            {
                var ungrantedAnswer = await SendAsync(client, method, route, "rwUi", body, ++n);
                var unknownAnswer = await SendAsync(client, method, route, "rwnosuch", body, ++n);
                if (ungrantedAnswer.Status != HttpStatusCode.NotFound)
                    failures.Add($"{method} {route} (ungranted): {(int)ungrantedAnswer.Status}, expected 404");
                if (unknownAnswer.Status != HttpStatusCode.NotFound)
                    failures.Add($"{method} {route} (unknown): {(int)unknownAnswer.Status}, expected 404");
                if (ungrantedAnswer.Body.Contains("Ungranted", StringComparison.Ordinal))
                    failures.Add($"{method} {route}: the answer names the item");
            }
            // The control: the same write on a granted item works.
            var control = await SendAsync(reader.Client, method, route, "rwGi", body, ++n);
            if ((int)control.Status is < 200 or >= 300)
                failures.Add($"{method} {route} (granted): {(int)control.Status}, expected 2xx");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        // The reads answer the same "not found" for that item.
        using (var read = await reader.Client.GetAsync("/api/v1/reading/progress/rwUi"))
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        // Nothing of the reader's landed on the inaccessible item; the control's rows did.
        Assert.Equal(0, await s.WithDbAsync(db => db.ReadingProgress.CountAsync(p => p.UserId == reader.UserId && p.ItemId == ungrantedItem)));
        Assert.Equal(0, await s.WithDbAsync(db => db.ReadMarks.CountAsync(m => m.UserId == reader.UserId && m.ItemId == ungrantedItem)));
        Assert.Equal(0, await s.WithDbAsync(db => db.Bookmarks.CountAsync(b => b.UserId == reader.UserId && b.ItemId == ungrantedItem)));
        Assert.Equal(1, await s.WithDbAsync(db => db.Bookmarks.CountAsync(b => b.UserId == reader.UserId && b.ItemId == grantedItem)));
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, string method, string route, string id, string? body, int n)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), string.Format(System.Globalization.CultureInfo.InvariantCulture, route, id));
        if (body is not null)
            request.Content = new StringContent(body.Replace("{1}", n.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
