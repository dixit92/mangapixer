namespace com.lifepixer.mangapixer.Tests.Server.Features.Reading;

using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// How the page endpoint answers a failed extraction: a client that went away (<c>cancelled</c>) is not a server
/// fault - Debug log, no 5xx - while a real <c>busy</c> / <c>timeout</c> keeps its Warning and 503.
/// </summary>
public sealed class PageExtractionFailureMappingTests
{
    private static (PageController Controller, List<(LogLevel Level, string Message)> Entries) Create()
    {
        var entries = new List<(LogLevel, string)>();
        var controller = new PageController(null!, null!, null!, null!, null!, null!, new ListLogger(entries));
        return (controller, entries);
    }

    private static int? StatusOf(IActionResult result) => (result as ObjectResult)?.StatusCode;

    [Fact]
    public void Cancelled_IsNotAServerError_AndLogsAtDebugOnly()
    {
        var (controller, entries) = Create();

        var result = controller.MapExtractionFailure(PageExtractionOutcome.Failed("cancelled", "Request cancelled."), nodeId: 7, entryKey: "k");

        Assert.Equal(499, StatusOf(result));
        Assert.DoesNotContain(entries, e => e.Level >= LogLevel.Information);
        Assert.Contains(entries, e => e.Level == LogLevel.Debug);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("timeout")]
    public void RealBusyAndTimeout_StayWarnings_With503(string errorType)
    {
        var (controller, entries) = Create();

        var result = controller.MapExtractionFailure(PageExtractionOutcome.Failed(errorType, "x"), nodeId: 7, entryKey: "k");

        Assert.Equal(503, StatusOf(result));
        Assert.Contains(entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void AnUnknownFailure_StaysA500Warning()
    {
        var (controller, entries) = Create();

        var result = controller.MapExtractionFailure(PageExtractionOutcome.Failed("extraction_failed", "x"), nodeId: 7, entryKey: "k");

        Assert.Equal(500, StatusOf(result));
        Assert.Contains(entries, e => e.Level == LogLevel.Warning);
    }

    private sealed class ListLogger(List<(LogLevel, string)> entries) : ILogger<PageController>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Add((logLevel, formatter(state, exception)));
    }
}
