namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers.Adapters;

using System.Net;
using com.lifepixer.mangapixer.Core.Trackers;
using Xunit;

/// <summary>
/// 1.43.0 owner A1 (lane G): the MyAnimeList adapter sends <c>start_date</c> / <c>finish_date</c> with a write that carries them. MAL does not
/// document them on the list update, so a 4xx answer to a write WITH dates (not 401 / 403 / 404 / 429) is retried once without them, and when
/// that works no dates are sent again (the process remembers it). Fake answers only - no request to MyAnimeList.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MyAnimeListDatesTests : IDisposable
{
    private readonly TrackerAdapterHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static TrackerEntryWrite Dated => new(TrackerEntryStatus.Reading, 12, 2, new DateOnly(2026, 1, 2), new DateOnly(2026, 3, 4), null);

    private Task WriteAsync(TrackerEntryWrite write) => _h.Mal.WriteEntryAsync(TrackerAdapterHarness.Tokens, "2", write, CancellationToken.None);

    private string[] Keys(int request) => TrackerAdapterHarness.Form(_h.Handler.Seen[request].Body).Select(p => p.Key).ToArray();

    [Fact]
    public async Task AWriteWithDates_SendsThem_AsDays()
    {
        _h.Answer(_ => TrackerAdapterHarness.Json("{}"));
        await WriteAsync(Dated with { FinishedOn = null });

        var form = TrackerAdapterHarness.Form(Assert.Single(_h.Handler.Seen).Body);
        Assert.Equal("2026-01-02", form.Single(p => p.Key == "start_date").Value);
        Assert.DoesNotContain(form, p => p.Key == "finish_date");
        Assert.False(_h.Mal.DatesRefused);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task ARefusedWriteWithDates_IsSentOnceMoreWithoutThem_AndNoDatesFollow(HttpStatusCode refusal)
    {
        var calls = 0;
        _h.Answer(_ => ++calls == 1 ? TrackerAdapterHarness.Json("""{"error":"invalid_parameters"}""", refusal) : TrackerAdapterHarness.Json("{}"));

        await WriteAsync(Dated);

        Assert.Equal(2, _h.Handler.Seen.Count);
        Assert.Contains("start_date", Keys(0));
        Assert.Equal(["status", "num_chapters_read", "num_volumes_read"], Keys(1));
        Assert.True(_h.Mal.DatesRefused);
        Assert.Contains("refused start / finish dates", _h.AllLogs, StringComparison.Ordinal);

        // From now on: one request, no dates.
        await WriteAsync(Dated);
        Assert.Equal(3, _h.Handler.Seen.Count);
        Assert.Equal(["status", "num_chapters_read", "num_volumes_read"], Keys(2));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerFailure.NeedsReconnect)]
    [InlineData(HttpStatusCode.Forbidden, TrackerFailure.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, TrackerFailure.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, TrackerFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, TrackerFailure.Unavailable)]
    public async Task OtherFailures_AreNotRetried_AndDoNotDropTheDates(HttpStatusCode status, TrackerFailure expected)
    {
        _h.Answer(_ => TrackerAdapterHarness.Status(status));

        var ex = await Assert.ThrowsAsync<TrackerException>(() => WriteAsync(Dated));

        Assert.Equal(expected, ex.Failure);
        Assert.Single(_h.Handler.Seen);
        Assert.False(_h.Mal.DatesRefused);
    }

    [Fact]
    public async Task ARetryThatFailsToo_KeepsTheDates_AndThrowsItsFailure()
    {
        _h.Answer(_ => TrackerAdapterHarness.Status(HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<TrackerException>(() => WriteAsync(Dated));

        Assert.Equal(TrackerFailure.Rejected, ex.Failure);
        Assert.Equal(2, _h.Handler.Seen.Count);
        Assert.False(_h.Mal.DatesRefused); // the dates were not the problem
    }

    [Fact]
    public async Task AWriteWithoutDates_IsNeverRetried()
    {
        _h.Answer(_ => TrackerAdapterHarness.Status(HttpStatusCode.BadRequest));
        await Assert.ThrowsAsync<TrackerException>(() => WriteAsync(Dated with { StartedOn = null, FinishedOn = null }));
        Assert.Single(_h.Handler.Seen);
    }
}
