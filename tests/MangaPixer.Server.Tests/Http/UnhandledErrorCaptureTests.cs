namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// The test-host seam that names the exception behind a sanitized 500 (<see cref="UnhandledErrorCapture"/>), on a minimal
/// host whose exception handler answers like the server's (a 500 with a generic body, the exception left on the feature).
/// </summary>
public sealed class UnhandledErrorCaptureTests
{
    private static async Task<IHost> StartAsync(UnhandledErrorCapture capture)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddSingleton<IStartupFilter>(capture))
                .Configure(app =>
                {
                    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
                    {
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync("{\"error\":\"internal_error\"}");
                    }));
                    app.Run(context => context.Request.Path == "/boom"
                        ? throw new InvalidOperationException("synthetic failure")
                        : context.Response.WriteAsync("ok"));
                }))
            .Build();
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task A500_IsDescribedWithItsException_ASuccessCapturesNothing()
    {
        var capture = new UnhandledErrorCapture();
        using var host = await StartAsync(capture);
        var client = host.GetTestClient();

        var ok = await client.GetAsync("/fine");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Empty(capture.Errors);

        var boom = await client.GetAsync("/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, boom.StatusCode);
        var error = Assert.Single(capture.Errors);
        Assert.Contains("GET /boom", error);
        Assert.Contains("InvalidOperationException: synthetic failure", error);

        var message = await capture.DescribeAsync(boom);
        Assert.Contains("-> 500: {\"error\":\"internal_error\"}", message);
        Assert.Contains("synthetic failure", message);
        await Assert.ThrowsAnyAsync<Exception>(() => capture.ExpectAsync(boom, HttpStatusCode.OK));
        await capture.ExpectAsync(ok, HttpStatusCode.OK);
    }
}
