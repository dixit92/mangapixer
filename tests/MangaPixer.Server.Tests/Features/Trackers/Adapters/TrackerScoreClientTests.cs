namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers.Adapters;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;
using Xunit;

/// <summary>
/// 1.53.0 (lane T): the two clients' score calls against recorded-SHAPE fake answers (MyAnimeList's API v2 reference; MangaUpdates' public
/// <c>openapi.yaml</c> - <c>SeriesRatingModelV1</c>, <c>ListsModelV1</c>, <c>ListsSearchResponseV1</c>); nothing recorded from an account,
/// no request leaves the test. Pins what each call sends (only the series id and the score), how a score is read, and the request count
/// of "Take my scores".
/// </summary>
[Trait("Category", "Unit")]
public sealed class TrackerScoreClientTests : IDisposable
{
    private const long MuSeries = 51239621230;
    private readonly TrackerAdapterHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ---- MyAnimeList

    [Fact]
    public async Task Mal_ReadEntry_CarriesTheUsersScore_AndZeroIsNone()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""
            {"id":13,"num_chapters":0,"num_volumes":0,"status":"currently_publishing",
             "my_list_status":{"status":"reading","score":7,"num_chapters_read":4,"num_volumes_read":0,"is_rereading":false}}
            """));
        var scored = await _h.Mal.ReadEntryAsync(TrackerAdapterHarness.Tokens, "13", CancellationToken.None);
        Assert.Equal(7, scored!.Score);
        Assert.True(_h.Mal.ScoreInEntry);
        Assert.Equal(7, await _h.Mal.ReadScoreAsync(TrackerAdapterHarness.Tokens, "13", scored, CancellationToken.None));
        Assert.Equal(1, _h.Handler.CallCount); // the score came with the entry: no request of its own

        _h.Answer(_ => TrackerAdapterHarness.Json("""{"id":13,"my_list_status":{"status":"reading","score":0,"num_chapters_read":4}}"""));
        Assert.Null((await _h.Mal.ReadEntryAsync(TrackerAdapterHarness.Tokens, "13", CancellationToken.None))!.Score);
    }

    [Theory]
    [InlineData(8, "8")]
    [InlineData(1, "1")]
    [InlineData(null, "0")]
    public async Task Mal_WriteScore_PatchesOnlyTheScore(int? score, string sent)
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"status":"reading","score":8}"""));

        await _h.Mal.WriteScoreAsync(TrackerAdapterHarness.Tokens, "13", score, CancellationToken.None);

        var write = Assert.Single(_h.Handler.Seen);
        Assert.Equal(HttpMethod.Patch, write.Method);
        Assert.Equal("https://api.myanimelist.net/v2/manga/13/my_list_status", write.Uri.ToString());
        Assert.Equal("Bearer " + TrackerAdapterHarness.Access, write.Headers["Authorization"]);
        Assert.Equal(new[] { new KeyValuePair<string, string>("score", sent) }, TrackerAdapterHarness.Form(write.Body));
    }

    [Fact]
    public async Task Mal_WriteScore_RefusesAScoreOutsideOneToTen_WithoutARequest()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("{}"));
        await Assert.ThrowsAsync<TrackerException>(() => _h.Mal.WriteScoreAsync(TrackerAdapterHarness.Tokens, "13", 11, CancellationToken.None));
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Mal_ScorePages_AreTheWholeListRead_WithScoreInTheFixedFields_AndLeaveUnscoredEntriesOut()
    {
        _h.Answer(r => TrackerAdapterHarness.Json(r.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal)
            ? """
              {"data":[
                {"node":{"id":13,"num_chapters":0,"num_volumes":0,"status":"currently_publishing"},"list_status":{"status":"reading","score":9}},
                {"node":{"id":21,"num_chapters":10,"num_volumes":1,"status":"finished"},"list_status":{"status":"completed","score":0}}],
               "paging":{"next":"https://api.myanimelist.net/v2/users/@me/mangalist?offset=1000&limit=1000"}}
              """
            : """{"data":[{"node":{"id":34},"list_status":{"status":"on_hold","score":6}}],"paging":{}}"""));

        var first = await _h.Mal.ReadScorePageAsync(TrackerAdapterHarness.Tokens, null, CancellationToken.None);
        var second = await _h.Mal.ReadScorePageAsync(TrackerAdapterHarness.Tokens, first.NextCursor, CancellationToken.None);

        Assert.Equal(new[] { new TrackerScoreItem("13", 9) }, first.Items);
        Assert.Equal("1000", first.NextCursor);
        Assert.Equal(new[] { new TrackerScoreItem("34", 6) }, second.Items);
        Assert.Null(second.NextCursor);
        Assert.Equal(2, _h.Handler.CallCount); // 1000 entries a request
        var query = System.Web.HttpUtility.ParseQueryString(_h.Handler.Seen[0].Uri.Query);
        Assert.Equal(MyAnimeListTrackerClient.ListFields, query["fields"]);
        Assert.Contains("list_status{status,score,", MyAnimeListTrackerClient.ListFields, StringComparison.Ordinal);
        Assert.Equal("1000", query["limit"]);
        Assert.Equal("true", query["nsfw"]);
    }

    // ---- MangaUpdates

    [Fact]
    public async Task Mu_ReadScore_IsItsOwnRequest_ARatingRoundsToWholeSteps_And404IsNone()
    {
        Assert.False(_h.MangaUpdates.ScoreInEntry);
        var entry = new TrackerEntry(TrackerEntryStatus.Reading, 3, null, null, null, false, null, null);
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"rating":7.5,"last_updated":{"timestamp":1700000000,"as_rfc3339":"2023-11-14T22:13:20+00:00"}}"""));
        Assert.Equal(8, await _h.MangaUpdates.ReadScoreAsync(TrackerAdapterHarness.Tokens, MuSeries.ToString(), entry, CancellationToken.None));
        var read = Assert.Single(_h.Handler.Seen);
        Assert.Equal(HttpMethod.Get, read.Method);
        Assert.Equal($"https://api.mangaupdates.com/v1/series/{MuSeries}/rating", read.Uri.ToString());
        Assert.Equal("Bearer " + TrackerAdapterHarness.Access, read.Headers["Authorization"]);

        _h.Answer(_ => TrackerAdapterHarness.Json("""{"status":"exception","reason":"Series not found"}""", HttpStatusCode.NotFound));
        Assert.Null(await _h.MangaUpdates.ReadScoreAsync(TrackerAdapterHarness.Tokens, MuSeries.ToString(), entry, CancellationToken.None));
    }

    [Fact]
    public async Task Mu_WriteScore_PutsOnlyTheRating_AndAClearDeletesIt()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"status":"success","reason":"Series rating was updated"}"""));

        await _h.MangaUpdates.WriteScoreAsync(TrackerAdapterHarness.Tokens, MuSeries.ToString(), 9, CancellationToken.None);
        await _h.MangaUpdates.WriteScoreAsync(TrackerAdapterHarness.Tokens, MuSeries.ToString(), null, CancellationToken.None);

        Assert.Equal(2, _h.Handler.CallCount);
        var put = _h.Handler.Seen[0];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal($"https://api.mangaupdates.com/v1/series/{MuSeries}/rating", put.Uri.ToString());
        Assert.Equal("""{"rating":9}""", put.Body);
        var delete = _h.Handler.Seen[1];
        Assert.Equal(HttpMethod.Delete, delete.Method);
        Assert.Equal($"https://api.mangaupdates.com/v1/series/{MuSeries}/rating", delete.Uri.ToString());
        Assert.True(string.IsNullOrEmpty(delete.Body));
    }

    [Fact]
    public async Task Mu_WriteScore_TheFiveSecondDelayIsRateLimited_AndAClearOfNoRatingIsDone()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("""{"status":"exception","reason":"Five second update delay"}""", HttpStatusCode.PreconditionFailed));
        var ex = await Assert.ThrowsAsync<TrackerException>(() =>
            _h.MangaUpdates.WriteScoreAsync(TrackerAdapterHarness.Tokens, MuSeries.ToString(), 4, CancellationToken.None));
        Assert.Equal(TrackerFailure.RateLimited, ex.Failure);
        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);

        _h.Answer(_ => TrackerAdapterHarness.Json("{}", HttpStatusCode.NotFound));
        await _h.MangaUpdates.WriteScoreAsync(TrackerAdapterHarness.Tokens, MuSeries.ToString(), null, CancellationToken.None);
    }

    [Fact]
    public async Task Mu_ScorePages_ReadTheListsThenEachListPageByPage_WithOnlyPageAndPerPage()
    {
        // Two lists: 150 entries on list 0 (two pages of 100), 30 on list 101 (one page). 1 + 2 + 1 = 4 requests for 180 entries.
        _h.Answer(r =>
        {
            if (r.Method == HttpMethod.Get)
                return TrackerAdapterHarness.Json("""[{"list_id":0,"title":"Reading List","type":"read"},{"list_id":101,"title":"Mine","type":"hold","custom":true}]""");
            var page = JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result).RootElement.GetProperty("page").GetInt32();
            var list = r.RequestUri!.AbsolutePath.Contains("/lists/101/", StringComparison.Ordinal) ? 101 : 0;
            var (total, count) = list == 0 ? (150, page == 1 ? 100 : 50) : (30, 30);
            var items = Enumerable.Range(0, count).Select(i =>
            {
                var id = 1000 + list * 1000 + (page - 1) * 100 + i;
                var rating = i == 0 ? "null" : (i % 10 + 0.4).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return $$"""{"record":{"series":{"id":{{id}},"title":"Public Title {{id}}"},"list_id":{{list}},"status":{"chapter":1}},"metadata":{"series":{"series_id":{{id}},"title":"Public Title {{id}}"},"user_rating":{{rating}}}}""";
            });
            return TrackerAdapterHarness.Json($$"""{"total_hits":{{total}},"page":{{page}},"per_page":100,"results":[{{string.Join(',', items)}}]}""");
        });

        var all = new List<TrackerScoreItem>();
        string? cursor = null;
        var requests = 0;
        do
        {
            var page = await _h.MangaUpdates.ReadScorePageAsync(TrackerAdapterHarness.Tokens, cursor, CancellationToken.None);
            requests++;
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(4, requests);
        Assert.Equal(4, _h.Handler.CallCount);
        Assert.Equal("https://api.mangaupdates.com/v1/lists", _h.Handler.Seen[0].Uri.ToString());
        Assert.Equal("https://api.mangaupdates.com/v1/lists/0/search", _h.Handler.Seen[1].Uri.ToString());
        Assert.Equal("""{"page":1,"perpage":100}""", _h.Handler.Seen[1].Body);
        Assert.Equal("""{"page":2,"perpage":100}""", _h.Handler.Seen[2].Body);
        Assert.Equal("https://api.mangaupdates.com/v1/lists/101/search", _h.Handler.Seen[3].Uri.ToString());
        Assert.All(_h.Handler.Seen.Skip(1), s => Assert.Equal(HttpMethod.Post, s.Method));
        // Unrated items (null; a rating below 0.5) are left out; 7.4 -> 7, 0.4 -> none.
        Assert.Equal(180 - 18, all.Count);
        Assert.Contains(new TrackerScoreItem("1001", 1), all);
        Assert.Contains(new TrackerScoreItem("1007", 7), all);
        Assert.DoesNotContain(all, i => i.MediaId == "1000" || i.MediaId == "1010");
        // Titles in the answer are never kept: only ids and scores leave the client.
        Assert.DoesNotContain("Public Title", string.Join(',', all.Select(i => i.ToString())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mu_ScorePages_NoListsIsOneRequest()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("[]"));
        var page = await _h.MangaUpdates.ReadScorePageAsync(TrackerAdapterHarness.Tokens, null, CancellationToken.None);
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
        Assert.Equal(1, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Mu_ScoreCursor_FromElsewhere_IsRefusedWithoutARequest()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("[]"));
        foreach (var bad in new[] { "x", "0:0:1", "5:1:1,2", "0:1:", "0:1:a" })
            await Assert.ThrowsAsync<TrackerException>(() => _h.MangaUpdates.ReadScorePageAsync(TrackerAdapterHarness.Tokens, bad, CancellationToken.None));
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public void ScoreRules_RoundHoldAndDecide()
    {
        Assert.Null(TrackerScoreRules.FromTracker(null));
        Assert.Null(TrackerScoreRules.FromTracker(0));
        Assert.Null(TrackerScoreRules.FromTracker(0.4));
        Assert.Equal(1, TrackerScoreRules.FromTracker(0.5));
        Assert.Equal(8, TrackerScoreRules.FromTracker(7.5));
        Assert.Equal(7, TrackerScoreRules.FromTracker(7.49));
        Assert.Equal(10, TrackerScoreRules.FromTracker(12));
        Assert.Null(TrackerScoreRules.FromTracker(double.NaN));
        Assert.Equal(TrackerScoreImportOutcome.Fill, TrackerScoreRules.Decide(null, 6));
        Assert.Equal(TrackerScoreImportOutcome.Same, TrackerScoreRules.Decide(6, 6));
        Assert.Equal(TrackerScoreImportOutcome.Differ, TrackerScoreRules.Decide(4, 6));
        Assert.False(TrackerScoreRules.NeedsWrite(null, null));
        Assert.True(TrackerScoreRules.NeedsWrite(5, null));
        Assert.True(TrackerScoreRules.NeedsWrite(null, 5));
        Assert.False(TrackerScoreRules.IsSendable(0));
        Assert.True(TrackerScoreRules.IsSendable(null));
    }
}
