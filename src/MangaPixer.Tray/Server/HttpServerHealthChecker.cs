namespace com.lifepixer.mangapixer.Tray.Server;

/// <summary>
/// Polls the server's <c>/health</c> endpoint over plain HTTP on loopback.
/// </summary>
public sealed class HttpServerHealthChecker : IServerHealthChecker, IDisposable
{
    private readonly HttpClient _httpClient;

    public HttpServerHealthChecker(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    public async Task<bool> IsHealthyAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(new Uri(baseUri, "/health"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
