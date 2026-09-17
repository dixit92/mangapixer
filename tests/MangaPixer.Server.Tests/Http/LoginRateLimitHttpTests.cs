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
/// End-to-end HTTP tests for the 1.16.0 RL lane's login/activation
/// rate-limit correctness fixes, driven through the real middleware pipeline
/// via <see cref="WebApplicationFactory{TEntryPoint}"/> — forwarded-headers
/// resolution, <c>AuthController</c>, and <c>LoginRateLimiter</c> together,
/// not the limiter in isolation (see LoginRateLimiterTests for that).
///
/// In the "HttpSerial" collection because every host boot reassigns the
/// process-global Serilog <c>Log.Logger</c> (see HttpTestCollection).
/// </summary>
[Collection("HttpSerial")]
public sealed class LoginRateLimitHttpTests
{
    private const string TrustedProxyIp = "10.0.0.5"; // RFC1918 -> trusted by default

    [Fact]
    public async Task Login_TwoForwardedClientIps_GetIndependentRateLimitBuckets_AndRetryAfterMatchesWindow()
    {
        await using var factory = new LoginRateLimitTestFactory();
        var client = factory.CreateClient();

        const string clientIpA = "203.0.113.10";
        const string clientIpB = "203.0.113.20";

        // Exhaust client A's per-IP bucket (default MaxAttemptsPerIp = 10).
        // A distinct username per attempt keeps the separate per-username
        // bucket (default MaxAttemptsPerUser = 5) from tripping first, so the
        // eventual 429 is attributable purely to the per-IP bucket.
        for (var i = 0; i < 10; i++)
        {
            var resp = await SendLoginAsync(client, clientIpA, $"no-such-user-a-{i}");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // The 11th attempt from client A is now rate-limited...
        var blocked = await SendLoginAsync(client, clientIpA, "no-such-user-a-overflow");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // ...and Retry-After reflects the real 5-minute sliding window, not
        // the old hardcoded 15-minute constant (900s).
        Assert.True(blocked.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        var retryAfterSeconds = int.Parse(retryAfterValues!.Single());
        Assert.InRange(retryAfterSeconds, 1, 300);

        // A DIFFERENT forwarded client IP, behind the SAME trusted proxy, is
        // completely unaffected — proving the limiter keys on the resolved
        // real client IP rather than the shared proxy peer address.
        var otherClient = await SendLoginAsync(client, clientIpB, "no-such-user-b");
        Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode);
    }

    [Fact]
    public async Task Activate_TwoActivationTargets_DoNotShareRateLimitBucket()
    {
        await using var factory = new LoginRateLimitTestFactory();
        var client = factory.CreateClient();

        const string tokenA = "activation-token-aaaa";
        const string tokenB = "activation-token-bbbb";

        // Exhaust the per-target bucket for token A (default MaxAttemptsPerUser = 5).
        // Every attempt uses an invalid token, so each call returns 400 rather
        // than actually activating anything.
        for (var i = 0; i < 5; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/v1/auth/activate", new ActivateAccountRequest
            {
                Token = tokenA,
                Password = "SomeStrongPassword123!",
            });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        // The 6th attempt against token A is now rate-limited.
        var blocked = await client.PostAsJsonAsync("/api/v1/auth/activate", new ActivateAccountRequest
        {
            Token = tokenA,
            Password = "SomeStrongPassword123!",
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // A DIFFERENT activation token is unaffected — it does NOT share the
        // old single "__activation__" bucket with token A.
        var otherTarget = await client.PostAsJsonAsync("/api/v1/auth/activate", new ActivateAccountRequest
        {
            Token = tokenB,
            Password = "SomeStrongPassword123!",
        });
        Assert.Equal(HttpStatusCode.BadRequest, otherTarget.StatusCode);
    }

    /// <summary>
    /// Sends a login attempt through the trusted proxy with the given
    /// forwarded client IP and username (any password — these usernames
    /// never exist, so the response is always 401 or 429, never a real
    /// sign-in).
    /// </summary>
    private static async Task<HttpResponseMessage> SendLoginAsync(HttpClient client, string forwardedClientIp, string username)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest { Username = username, Password = "wrong-password" }),
        };
        request.Headers.TryAddWithoutValidation("X-Test-Remote-Ip", TrustedProxyIp);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedClientIp);
        return await client.SendAsync(request);
    }
}

/// <summary>
/// A self-contained <see cref="WebApplicationFactory{TEntryPoint}"/> for the
/// login rate-limit HTTP tests, with rate limiting LEFT ENABLED (unlike
/// <c>MangaPixerWebApplicationFactory</c>, which always disables it) and the
/// same test-only peer-IP seam as <c>ForwardedHeadersTestFactory</c>
/// (duplicated here rather than shared, per the lane convention of adding
/// only NEW files) so <c>X-Forwarded-For</c> is honored from a trusted peer.
/// </summary>
internal sealed class LoginRateLimitTestFactory : WebApplicationFactory<Program>
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "mangapixer-rlhttp-" + Guid.NewGuid().ToString("N")[..8]);

    private string DataRoot => Path.Combine(_tempRoot, "data");
    private string CacheRoot => Path.Combine(_tempRoot, "cache");
    private string ScratchRoot => Path.Combine(_tempRoot, "scratch");

    public LoginRateLimitTestFactory()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(ScratchRoot);

        using (TestHostStorageOverride.Push(new StorageRootOverride(
            DataRoot: DataRoot,
            CacheRoot: CacheRoot,
            ScratchRoot: ScratchRoot,
            WorkerExecutablePath: "",
            RateLimitDisabled: false)))
        {
            using var bootClient = CreateClient();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            var workerHosted = services.FirstOrDefault(d => d.ImplementationType == typeof(MediaWorkerHostedService));
            if (workerHosted is not null)
                services.Remove(workerHosted);
            var thumbBackfill = services.FirstOrDefault(d => d.ImplementationType == typeof(ThumbnailBackfillHostedService));
            if (thumbBackfill is not null)
                services.Remove(thumbBackfill);

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
