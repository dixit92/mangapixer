namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers.Adapters;

using System.Net;
using System.Web;
using com.lifepixer.mangapixer.Core.Trackers;
using Xunit;

/// <summary>
/// 1.43.0 "Check my list" (owner option C): the MyAnimeList adapter reads the user's own manga list page by page - exactly these parameters,
/// the user's bearer token, entries mapped like a single read, titles dropped, the next offset from <c>paging.next</c>. Synthetic answers in
/// the documented shape only - no request to MyAnimeList.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MyAnimeListListTests : IDisposable
{
    private readonly TrackerAdapterHarness _h = new();

    public void Dispose() => _h.Dispose();

    private const string Page = """
        {"data":[
          {"node":{"id":2,"title":"Synthetic Title A","num_chapters":0,"num_volumes":0,"status":"currently_publishing"},
           "list_status":{"status":"reading","num_chapters_read":12,"num_volumes_read":2,"start_date":"2026-01-02","is_rereading":false}},
          {"node":{"id":7,"title":"Synthetic Title B","num_chapters":40,"num_volumes":4,"status":"finished"},
           "list_status":{"status":"completed","num_chapters_read":40,"num_volumes_read":4,"finish_date":"2026-03-04","is_rereading":true}},
          {"node":{"id":-1,"title":"bad"},"list_status":{"status":"reading"}},
          {"node":{"id":9,"title":"no status"},"list_status":{"status":"something_new"}}
        ],
        "paging":{"next":"https://api.myanimelist.net/v2/users/@me/mangalist?offset=1000&limit=1000&fields=x"}}
        """;

    [Fact]
    public async Task APage_SendsExactlyTheListParameters_WithTheUsersToken_AndNsfw()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json(Page));

        var page = await _h.Mal.ReadListPageAsync(TrackerAdapterHarness.Tokens, 0, CancellationToken.None);

        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal(HttpMethod.Get, seen.Method);
        Assert.Equal("https://api.myanimelist.net/v2/users/@me/mangalist", seen.Uri.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(seen.Uri.Query);
        Assert.Equal("fields,limit,offset,nsfw", string.Join(",", query.AllKeys));
        Assert.Equal("list_status{status,num_chapters_read,num_volumes_read,start_date,finish_date,is_rereading},num_chapters,num_volumes,status",
            query["fields"]);
        Assert.Equal(("1000", "0", "true"), (query["limit"], query["offset"], query["nsfw"]));
        Assert.Equal("Bearer " + TrackerAdapterHarness.Access, seen.Headers["Authorization"]);

        Assert.Equal(1000, page.NextOffset);
        Assert.Equal(["2", "7"], page.Items.Select(i => i.MediaId).ToArray());
        Assert.Equal(new TrackerEntry(TrackerEntryStatus.Reading, 12, 2, null, null, false, new DateOnly(2026, 1, 2), null), page.Items[0].Entry);
        Assert.Equal(new TrackerEntry(TrackerEntryStatus.Rereading, 40, 4, 40, 4, true, null, new DateOnly(2026, 3, 4)), page.Items[1].Entry);
    }

    [Fact]
    public async Task TheLastPage_HasNoNextOffset_AndTheOffsetIsSent()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"data":[],"paging":{}}"""));
        var page = await _h.Mal.ReadListPageAsync(TrackerAdapterHarness.Tokens, 2000, CancellationToken.None);
        Assert.Null(page.NextOffset);
        Assert.Equal("2000", HttpUtility.ParseQueryString(Assert.Single(_h.Handler.Seen).Uri.Query)["offset"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerFailure.NeedsReconnect)]
    [InlineData(HttpStatusCode.Forbidden, TrackerFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, TrackerFailure.Unavailable)]
    public async Task Failures_MapOntoTheSeam(HttpStatusCode status, TrackerFailure expected)
    {
        _h.Answer(_ => TrackerAdapterHarness.Status(status));
        var ex = await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.ReadListPageAsync(TrackerAdapterHarness.Tokens, 0, CancellationToken.None));
        Assert.Equal(expected, ex.Failure);
    }
}
