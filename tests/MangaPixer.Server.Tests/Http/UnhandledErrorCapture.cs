namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;

/// <summary>
/// Remembers the exception behind every HTTP 500 a test host answers. The server's exception handler sends the client only a
/// sanitized <c>internal_error</c> and logs the exception, so an assertion on a 500 cannot say what went wrong; this filter runs
/// outside the app's pipeline and reads the exception the handler leaves on <see cref="IExceptionHandlerFeature"/> after the
/// response (1.33.0, for a rare 500 under parallel load in <c>MissingReportHttpTests</c>).
/// </summary>
public sealed class UnhandledErrorCapture : IStartupFilter
{
    private readonly ConcurrentQueue<string> _errors = new();

    /// <summary>"METHOD /path: exception" for every 500 so far, oldest first.</summary>
    public IReadOnlyCollection<string> Errors => _errors.ToArray();

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, inner) =>
        {
            await inner();
            if (context.Response.StatusCode >= 500 && context.Features.Get<IExceptionHandlerFeature>()?.Error is { } error)
                _errors.Enqueue($"{context.Request.Method} {context.Request.Path}: {error}");
        });
        next(app);
    };

    /// <summary>An assertion message: the response's status and body, and every exception captured so far.</summary>
    public async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var errors = Errors;
        return $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} -> {(int)response.StatusCode}: {body}"
            + (errors.Count == 0 ? string.Empty : $"{Environment.NewLine}Unhandled server errors:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
    }

    /// <summary>Fails with <see cref="DescribeAsync"/> unless the response has <paramref name="expected"/> status.</summary>
    public async Task ExpectAsync(HttpResponseMessage response, System.Net.HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            Xunit.Assert.Fail($"Expected {(int)expected} {expected}, got {await DescribeAsync(response)}");
    }

    /// <summary>Fails with <see cref="DescribeAsync"/> unless the response is a success.</summary>
    public async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            Xunit.Assert.Fail($"Expected success, got {await DescribeAsync(response)}");
    }
}
