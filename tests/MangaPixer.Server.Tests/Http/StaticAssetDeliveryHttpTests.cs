namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.IO.Compression;
using System.Net;
using System.Text;
using com.lifepixer.mangapixer.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// HTTP tests (in-memory host) for the web app's delivery (1.46.0 lane D, <see cref="StaticAssetDelivery"/>): precompressed variants
/// by <c>Accept-Encoding</c> with the original Content-Type and <c>Vary</c>; <c>immutable</c> caching for content-hashed files only and
/// <c>no-cache</c> for <c>index.html</c> (the default document and the SPA fallback); files without variants and API answers unchanged.
/// The same wwwroot layout the Angular build writes (<c>main-XXXXXXXX.js</c>, <c>media/</c>, <c>assets/</c>) plus the build script's
/// <c>.br</c> / <c>.gz</c> copies.
/// </summary>
public sealed class StaticAssetDeliveryHttpTests : IAsyncLifetime
{
    private const string Bundle = "/main-AB12CD34.js";
    private const string Immutable = "public, max-age=31536000, immutable";

    private static readonly byte[] BundleBytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("export function f(){return 42}\n", 400)));
    private static readonly byte[] IndexBytes = Encoding.UTF8.GetBytes(
        "<!doctype html><html><head><title>MangaPixer</title></head><body>" + string.Concat(Enumerable.Repeat("<i></i>", 300)) + "</body></html>");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mangapixer-static-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        Directory.CreateDirectory(Path.Combine(_root, "assets", "icons"));
        WriteWithVariants("index.html", IndexBytes);
        WriteWithVariants(Bundle.TrimStart('/'), BundleBytes);
        File.WriteAllBytes(Path.Combine(_root, "media", "roboto-latin-400-normal-QWER7890.woff2"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(_root, "assets", "icons", "icon-192.png"), [137, 80, 78, 71]);
        File.WriteAllBytes(Path.Combine(_root, "favicon.ico"), [0, 0, 1, 0]);
        // A copy older than its file (a rebuild without the script): never served.
        WriteWithVariants("chunk-STALE001.js", BundleBytes);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "chunk-STALE001.js.br"), DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "chunk-STALE001.js.gz"), DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "chunk-STALE001.js"), DateTime.UtcNow);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.MapGet("/api/v1/ping", () => Results.Json(new { ok = true }));
        _app.UseMangaPixerWebApp(_root);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private void WriteWithVariants(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        using (var br = new MemoryStream())
        {
            using (var s = new BrotliStream(br, CompressionLevel.SmallestSize, leaveOpen: true)) s.Write(bytes);
            File.WriteAllBytes(path + ".br", br.ToArray());
        }
        using (var gz = new MemoryStream())
        {
            using (var s = new GZipStream(gz, CompressionLevel.SmallestSize, leaveOpen: true)) s.Write(bytes);
            File.WriteAllBytes(path + ".gz", gz.ToArray());
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? acceptEncoding, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        return await _client.SendAsync(request);
    }

    private static async Task<byte[]> DecodedBodyAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStreamAsync();
        var encoding = response.Content.Headers.ContentEncoding.SingleOrDefault();
        Stream decoded = encoding switch
        {
            "br" => new BrotliStream(raw, CompressionMode.Decompress),
            "gzip" => new GZipStream(raw, CompressionMode.Decompress),
            null => raw,
            _ => throw new InvalidOperationException("unexpected encoding " + encoding),
        };
        using var ms = new MemoryStream();
        await decoded.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static string? CacheControl(HttpResponseMessage r) => r.Headers.CacheControl?.ToString();

    [Theory]
    [InlineData("gzip, deflate, br, zstd", "br")]
    [InlineData("gzip, deflate", "gzip")]
    [InlineData("br;q=0, gzip", "gzip")]
    [InlineData("gzip;q=1.0, br;q=0.5", "gzip")]
    [InlineData("*", "br")]
    [InlineData("identity", null)]
    [InlineData(null, null)]
    public async Task HashedBundle_IsSentInTheAcceptedEncoding_WithItsOwnType_Varying_AndImmutable(string? acceptEncoding, string? expected)
    {
        var response = await GetAsync(Bundle, acceptEncoding);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, response.Content.Headers.ContentEncoding.SingleOrDefault());
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.Equal(Immutable, CacheControl(response));
        Assert.Equal(BundleBytes, await DecodedBodyAsync(response));
        if (expected is not null)
            Assert.True(response.Content.Headers.ContentLength < BundleBytes.Length);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/libraries/abc/browse")]
    public async Task IndexHtml_DefaultDocumentAndSpaFallback_IsNoCache_AndCompressedWhenAccepted(string path)
    {
        var compressed = await GetAsync(path, "gzip, deflate, br");
        Assert.Equal(HttpStatusCode.OK, compressed.StatusCode);
        Assert.Equal("text/html", compressed.Content.Headers.ContentType?.MediaType);
        Assert.Equal("br", compressed.Content.Headers.ContentEncoding.SingleOrDefault());
        Assert.Equal("no-cache", CacheControl(compressed));
        Assert.Contains("Accept-Encoding", compressed.Headers.Vary);
        Assert.Equal(IndexBytes, await DecodedBodyAsync(compressed));

        var plain = await GetAsync(path, null);
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.Equal("no-cache", CacheControl(plain));
        Assert.Equal(IndexBytes, await plain.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ApiAnswers_AreUntouched()
    {
        var ok = await GetAsync("/api/v1/ping", "gzip, deflate, br");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Empty(ok.Content.Headers.ContentEncoding);
        Assert.Null(ok.Headers.CacheControl);
        Assert.Empty(ok.Headers.Vary);

        var missing = await GetAsync("/api/v1/nothing", "br");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Null(missing.Headers.CacheControl);
        Assert.Equal(0, (await missing.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task HashedFileWithoutVariants_IsImmutable_NotVarying()
    {
        var font = await GetAsync("/media/roboto-latin-400-normal-QWER7890.woff2", "br, gzip");
        Assert.Equal(HttpStatusCode.OK, font.StatusCode);
        Assert.Empty(font.Content.Headers.ContentEncoding);
        Assert.Equal(Immutable, CacheControl(font));
        Assert.Empty(font.Headers.Vary);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await font.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/favicon.ico")]
    [InlineData("/assets/icons/icon-192.png")]
    public async Task FilesWithoutAHashInTheName_KeepNoCacheControl(string path)
    {
        var response = await GetAsync(path, "br, gzip");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.CacheControl);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task AVariantOlderThanItsFile_IsNeverServed()
    {
        var response = await GetAsync("/chunk-STALE001.js", "br, gzip");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(BundleBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(Bundle + ".br")]
    [InlineData(Bundle + ".gz")]
    [InlineData("/index.html.gz")]
    public async Task AVariantsOwnPath_IsNotFound(string path)
    {
        var response = await GetAsync(path, "br, gzip");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revalidation_AndHead_KeepTheEncodingAndCacheHeaders()
    {
        var first = await GetAsync(Bundle, "br");
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        var identityTag = (await GetAsync(Bundle, null)).Headers.ETag;
        Assert.NotEqual(etag, identityTag); // each representation has its own validator

        var request = new HttpRequestMessage(HttpMethod.Get, Bundle);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");
        request.Headers.IfNoneMatch.Add(etag!);
        var notModified = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(Immutable, CacheControl(notModified));

        var head = await GetAsync(Bundle, "br", HttpMethod.Head);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal("br", head.Content.Headers.ContentEncoding.SingleOrDefault());
        Assert.Equal(first.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData("/main-AB12CD34.js", true)]
    [InlineData("/chunk-Q7ZK2M4P.js", true)]
    [InlineData("/polyfills-B6TNHZQY.js", true)]
    [InlineData("/styles-5INURTSO.css", true)]
    [InlineData("/media/roboto-latin-400-normal-QWER7890.woff2", true)]
    [InlineData("/index.html", false)]
    [InlineData("/favicon.ico", false)]
    [InlineData("/manifest.webmanifest", false)]
    [InlineData("/assets/icons/icon-192.png", false)]
    [InlineData("/assets/icons/icon-ABCD1234.png", false)]
    [InlineData("/main-ab12cd34.js", false)]
    [InlineData("/3rdpartylicenses.txt", false)]
    public void ContentHashedNames_AreTheBuildsHashedOutputOnly(string path, bool hashed) =>
        Assert.Equal(hashed, StaticAssetDelivery.IsContentHashed(new PathString(path)));
}
