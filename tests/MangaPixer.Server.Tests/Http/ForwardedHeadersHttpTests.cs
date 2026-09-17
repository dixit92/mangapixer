namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server;
using com.lifepixer.mangapixer.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// End-to-end HTTP tests for the reverse-proxy / forwarded-headers hardening
/// (1.16.0), driven through the real middleware pipeline via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>. These assert the OBSERVABLE
/// public behavior — whether the auth / CSRF <c>Set-Cookie</c> is emitted
/// <c>Secure</c> — as a function of (a) whether the immediate peer is a trusted
/// proxy and (b) whether it sent <c>X-Forwarded-Proto: https</c>.
///
/// In the "HttpSerial" collection because every host boot reassigns the
/// process-global Serilog <c>Log.Logger</c> (see HttpTestCollection).
/// </summary>
[Collection("HttpSerial")]
public sealed class ForwardedHeadersHttpTests
{
    private const string TrustedProxyIp = "10.0.0.5";     // RFC1918 → trusted by default
    private const string UntrustedPublicIp = "8.8.8.8";   // public → not trusted

    [Fact]
    public async Task TrustedProxy_ForwardedProtoHttps_AuthAndCsrfCookiesAreSecure()
    {
        await using var factory = new ForwardedHeadersTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        // Setup signs the first admin in, so its response carries the auth cookie.
        var setupReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/setup")
        {
            Content = JsonContent.Create(new SetupRequest { Username = "admin", Password = "MangaPixer-Change-Me-Now!" }),
        };
        AddProxyHeaders(setupReq, TrustedProxyIp, forwardedProto: "https");
        var setupResp = await client.SendAsync(setupReq);
        setupResp.EnsureSuccessStatusCode();

        var authCookie = GetSetCookie(setupResp, ".MangaPixer.Auth");
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);

        // CSRF cookie is issued via GET /auth/csrf with the same trusted-https shape.
        var csrfReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/csrf");
        AddProxyHeaders(csrfReq, TrustedProxyIp, forwardedProto: "https");
        var csrfResp = await client.SendAsync(csrfReq);
        csrfResp.EnsureSuccessStatusCode();

        var csrfCookie = GetSetCookie(csrfResp, ".MangaPixer.Csrf");
        Assert.Contains("secure", csrfCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UntrustedSource_ForwardedProtoHttps_IsIgnored_CookieNotSecure()
    {
        await using var factory = new ForwardedHeadersTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        // Same spoofed X-Forwarded-Proto: https, but the peer is a public IP the
        // default trust model does NOT recognize → the header must be ignored,
        // the effective scheme stays http, and the cookie is NOT Secure.
        var csrfReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/csrf");
        AddProxyHeaders(csrfReq, UntrustedPublicIp, forwardedProto: "https");
        var csrfResp = await client.SendAsync(csrfReq);
        csrfResp.EnsureSuccessStatusCode();

        var csrfCookie = GetSetCookie(csrfResp, ".MangaPixer.Csrf");
        Assert.DoesNotContain("secure", csrfCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlainHttpLan_NoForwardedProto_CookieNotSecure()
    {
        await using var factory = new ForwardedHeadersTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

        // A genuine plain-http LAN client (trusted source, but no forwarded proto):
        // the app must keep working over http and NOT force Secure cookies, which
        // a plain-http client would silently drop.
        var csrfReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/csrf");
        AddProxyHeaders(csrfReq, TrustedProxyIp, forwardedProto: null);
        var csrfResp = await client.SendAsync(csrfReq);
        csrfResp.EnsureSuccessStatusCode();

        var csrfCookie = GetSetCookie(csrfResp, ".MangaPixer.Csrf");
        Assert.DoesNotContain("secure", csrfCookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds the test-only <c>X-Test-Remote-Ip</c> header (consumed by
    /// <see cref="ForwardedHeadersTestFactory"/> to set the connection's peer
    /// address, which TestServer otherwise leaves unset) plus a realistic
    /// <c>X-Forwarded-For</c> and an optional <c>X-Forwarded-Proto</c>.
    /// Added without validation because these are hop-by-hop-style headers the
    /// HttpClient request-header allow-list would otherwise reject.
    /// </summary>
    private static void AddProxyHeaders(HttpRequestMessage request, string remoteIp, string? forwardedProto)
    {
        request.Headers.TryAddWithoutValidation("X-Test-Remote-Ip", remoteIp);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9");
        if (forwardedProto is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);
    }

    private static string GetSetCookie(HttpResponseMessage response, string cookieName)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies),
            $"response had no Set-Cookie header (looking for {cookieName})");
        var match = cookies!.FirstOrDefault(c => c.StartsWith(cookieName + "=", StringComparison.Ordinal));
        Assert.NotNull(match);
        return match!;
    }
}

/// <summary>
/// A self-contained <see cref="WebApplicationFactory{TEntryPoint}"/> for the
/// forwarded-headers tests. Kept separate from
/// <c>MangaPixerWebApplicationFactory</c> (which is sealed and owned broadly)
/// so this lane adds only NEW files. It reuses the same non-global storage
/// isolation seam (<see cref="TestHostStorageOverride"/>) and additionally
/// installs an <see cref="IStartupFilter"/> that seeds
/// <c>Connection.RemoteIpAddress</c> from a test header — TestServer does not
/// otherwise populate a peer address, and the forwarded-headers middleware's
/// trust check depends entirely on it.
/// </summary>
internal sealed class ForwardedHeadersTestFactory : WebApplicationFactory<Program>
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "mangapixer-fhhttp-" + Guid.NewGuid().ToString("N")[..8]);

    private string DataRoot => Path.Combine(_tempRoot, "data");
    private string CacheRoot => Path.Combine(_tempRoot, "cache");
    private string ScratchRoot => Path.Combine(_tempRoot, "scratch");

    public ForwardedHeadersTestFactory()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(ScratchRoot);

        // Push per-instance storage roots for the synchronous boot triggered by
        // CreateClient(), on this same call stack — see TestHostStorageOverride.
        using (TestHostStorageOverride.Push(new StorageRootOverride(
            DataRoot: DataRoot,
            CacheRoot: CacheRoot,
            ScratchRoot: ScratchRoot,
            WorkerExecutablePath: "",
            RateLimitDisabled: true)))
        {
            using var bootClient = CreateClient();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Don't spawn real worker processes in tests.
            var workerHosted = services.FirstOrDefault(d => d.ImplementationType == typeof(MediaWorkerHostedService));
            if (workerHosted is not null)
                services.Remove(workerHosted);
            var thumbBackfill = services.FirstOrDefault(d => d.ImplementationType == typeof(ThumbnailBackfillHostedService));
            if (thumbBackfill is not null)
                services.Remove(thumbBackfill);

            // Seed the peer IP so the forwarded-headers trust check has something
            // to evaluate. Runs outermost (before UseForwardedHeaders) because
            // IStartupFilter middleware wraps the app's own pipeline.
            services.AddSingleton<IStartupFilter, RemoteIpTestStartupFilter>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
        base.Dispose(disposing);
    }

    private sealed class RemoteIpTestStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Remote-Ip", out var raw)
                    && IPAddress.TryParse(raw.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }
                await nextMiddleware();
            });
            next(app);
        };
    }
}
