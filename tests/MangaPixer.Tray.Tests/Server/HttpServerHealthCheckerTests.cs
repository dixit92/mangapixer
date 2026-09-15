namespace com.lifepixer.mangaplex.Tests.Tray.Server;

using System.Net;
using com.lifepixer.mangaplex.Tray.Server;
using Xunit;

public sealed class HttpServerHealthCheckerTests
{
    private sealed class StubHandler(HttpStatusCode statusCode, Exception? throwOnSend = null) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            if (throwOnSend is not null)
                throw throwOnSend;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    [Fact]
    public async Task IsHealthyAsync_WhenServerReturns200_ReturnsTrue()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var checker = new HttpServerHealthChecker(new HttpClient(handler));

        var healthy = await checker.IsHealthyAsync(new Uri("http://127.0.0.1:6280"), CancellationToken.None);

        Assert.True(healthy);
        Assert.Equal(new Uri("http://127.0.0.1:6280/health"), handler.LastRequestUri);
    }

    [Fact]
    public async Task IsHealthyAsync_WhenServerReturns503_ReturnsFalse()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var checker = new HttpServerHealthChecker(new HttpClient(handler));

        var healthy = await checker.IsHealthyAsync(new Uri("http://127.0.0.1:6280"), CancellationToken.None);

        Assert.False(healthy);
    }

    [Fact]
    public async Task IsHealthyAsync_WhenConnectionRefused_ReturnsFalseInsteadOfThrowing()
    {
        var handler = new StubHandler(HttpStatusCode.OK, throwOnSend: new HttpRequestException("refused"));
        var checker = new HttpServerHealthChecker(new HttpClient(handler));

        var healthy = await checker.IsHealthyAsync(new Uri("http://127.0.0.1:6280"), CancellationToken.None);

        Assert.False(healthy);
    }
}
