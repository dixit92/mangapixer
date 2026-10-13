namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers.Adapters;

using System.Net;
using System.Text;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// The two tracker clients over their REAL named-client registration (allowlist, bearer handler, User-Agent, timeout) whose primary
/// handler is a <see cref="ScriptedHandler"/> - fake answers only, never a recorded account. Every log line at every level is captured.
/// NO test ever reaches MyAnimeList or MangaUpdates.
/// </summary>
public sealed class TrackerAdapterHarness : IDisposable
{
    public const string Access = "synthetic-access-token-0001";
    public const string Refresh = "synthetic-refresh-token-0001";

    /// <summary>A PKCE verifier (43+ unreserved characters); with MAL's <c>plain</c> method it is also the challenge.</summary>
    public const string Verifier = "synthetic-verifier-0123456789-abcdefghijklmnopqrstuvwxyz";

    private readonly ServiceProvider _http;

    public TrackerAdapterHarness(MetadataGatewayState? state = null, bool withAllowlist = true)
    {
        Handler = new ScriptedHandler { Respond = _ => throw new InvalidOperationException("No scripted answer.") };
        Time = new ManualTime(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        Logs = new CapturingLoggerProvider();
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        _ownsState = state is null;
        State = state ?? new MetadataGatewayState(new MetadataRateLimitOptions());
        Pacing = new TrackerPacing(State, new TrackerRateLimitOptions
        {
            // No waiting between scripted calls in a test.
            MyAnimeList = new() { TokenLimit = 100, TokensPerPeriod = 100, ReplenishmentPeriod = TimeSpan.FromMilliseconds(10), QueueLimit = 10, AutoReplenishment = true },
        });

        var services = new ServiceCollection();
        services.AddSingleton(LoggerFactory);
        services.AddLogging();
        if (withAllowlist)
        {
            TrackerAdapterServicesExtensions.AddTrackerClient(services, TrackerHttp.MyAnimeListClient, TrackerHttp.MyAnimeListApiHost, TrackerHttp.MyAnimeListSiteHost)
                .ConfigurePrimaryHttpMessageHandler(() => Handler);
            TrackerAdapterServicesExtensions.AddTrackerClient(services, TrackerHttp.MangaUpdatesClient, MetadataHttp.MangaUpdatesApiHost)
                .ConfigurePrimaryHttpMessageHandler(() => Handler);
        }
        else
        {
            // For the red-first proof only: the same clients without their allowlist handler.
            services.AddHttpClient(TrackerHttp.MyAnimeListClient).AddHttpMessageHandler(() => new TrackerBearerHandler()).ConfigurePrimaryHttpMessageHandler(() => Handler);
            services.AddHttpClient(TrackerHttp.MangaUpdatesClient).AddHttpMessageHandler(() => new TrackerBearerHandler()).ConfigurePrimaryHttpMessageHandler(() => Handler);
        }
        _http = services.BuildServiceProvider();
        var factory = _http.GetRequiredService<IHttpClientFactory>();
        Mal = new MyAnimeListTrackerClient(factory, Pacing, Time, LoggerFactory.CreateLogger<MyAnimeListTrackerClient>());
        MangaUpdates = new MangaUpdatesTrackerClient(factory, Pacing, Time, LoggerFactory.CreateLogger<MangaUpdatesTrackerClient>());
    }

    private readonly bool _ownsState;

    public ScriptedHandler Handler { get; }
    public ManualTime Time { get; }
    public CapturingLoggerProvider Logs { get; }
    public ILoggerFactory LoggerFactory { get; }
    public MetadataGatewayState State { get; }
    public TrackerPacing Pacing { get; }
    public MyAnimeListTrackerClient Mal { get; }
    public MangaUpdatesTrackerClient MangaUpdates { get; }

    public static TrackerTokens Tokens => new(Access, Refresh, null);

    public static TrackerAppSettings App(string? secret = null) =>
        new("synthetic-client-id", secret, new Uri("https://reader.example/trackers/callback/mal"));

    /// <summary>Answers every request with <paramref name="answer"/>.</summary>
    public void Answer(Func<HttpRequestMessage, HttpResponseMessage> answer) => Handler.Respond = answer;

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => ScriptedHandler.Json(json, status);

    public static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"synthetic\"}", Encoding.UTF8, "application/json") };
        if (retryAfter is { } wait)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        return response;
    }

    /// <summary>The form fields of a captured url-encoded body, in order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Form(string? body) =>
        (body ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => new KeyValuePair<string, string>(Uri.UnescapeDataString(p[0].Replace('+', ' ')), Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : "")))
            .ToList();

    /// <summary>Every captured log line, joined.</summary>
    public string AllLogs => string.Join('\n', Logs.Lines);

    public void Dispose()
    {
        _http.Dispose();
        Pacing.Dispose();
        if (_ownsState)
            State.Dispose();
        LoggerFactory.Dispose();
    }
}
