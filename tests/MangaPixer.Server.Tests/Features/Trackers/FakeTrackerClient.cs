namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Collections.Concurrent;
using com.lifepixer.mangapixer.Core.Trackers;

/// <summary>
/// A fake <see cref="ITrackerClient"/> for the engine tests (1.43.0): no network - entries live in memory, every call is recorded, and
/// failures can be queued. Tokens and passwords it hands out are made-up marker strings so a test can search logs / responses / the
/// database for them.
/// </summary>
internal sealed class FakeTrackerClient(string slug, TrackerConnectKind kind) : ITrackerClient
{
    public const string AccessPrefix = "AT-SECRET-";
    public const string RefreshPrefix = "RT-SECRET-";

    private int _issued;

    public string Slug { get; } = slug;
    public TrackerConnectKind ConnectKind { get; } = kind;

    /// <summary>The user's entries by tracker series id (absent = on no list).</summary>
    public ConcurrentDictionary<string, TrackerEntry> Entries { get; } = new();

    public ConcurrentQueue<(string MediaId, TrackerEntryWrite Write, string AccessToken)> Writes { get; } = new();
    public ConcurrentQueue<string> Reads { get; } = new();
    public ConcurrentQueue<TrackerConnectRequest> Connects { get; } = new();
    public ConcurrentQueue<TrackerTokens> Refreshes { get; } = new();
    public ConcurrentQueue<TrackerTokens> Revokes { get; } = new();

    /// <summary>Failures thrown by the next writes / reads / refreshes, in order.</summary>
    public ConcurrentQueue<TrackerException> WriteFailures { get; } = new();
    public ConcurrentQueue<TrackerException> ReadFailures { get; } = new();
    public ConcurrentQueue<TrackerException> RefreshFailures { get; } = new();

    /// <summary>Lifetime of the tokens it issues.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(30);

    public int CallCount => Reads.Count + Writes.Count;

    public Uri? BuildAuthorizeUrl(TrackerAppSettings app, string state, string codeChallenge) =>
        ConnectKind == TrackerConnectKind.Password
            ? null
            : new Uri($"https://tracker.invalid/authorize?client_id={Uri.EscapeDataString(app.ClientId ?? "")}&state={Uri.EscapeDataString(state)}"
                + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}&redirect_uri={Uri.EscapeDataString(app.RedirectUri?.AbsoluteUri ?? "")}");

    public Task<TrackerTokens> ConnectAsync(TrackerAppSettings app, TrackerConnectRequest request, CancellationToken ct)
    {
        Connects.Enqueue(request);
        return Task.FromResult(Issue());
    }

    public Task<TrackerTokens> RefreshAsync(TrackerAppSettings app, TrackerTokens tokens, CancellationToken ct)
    {
        Refreshes.Enqueue(tokens);
        if (RefreshFailures.TryDequeue(out var failure))
            throw failure;
        return Task.FromResult(Issue());
    }

    public Task<TrackerIdentity> WhoAmIAsync(TrackerTokens tokens, CancellationToken ct) =>
        Task.FromResult(new TrackerIdentity("4242", "Synthetic Reader"));

    public Task<TrackerEntry?> ReadEntryAsync(TrackerTokens tokens, string mediaId, CancellationToken ct)
    {
        Reads.Enqueue(mediaId);
        if (ReadFailures.TryDequeue(out var failure))
            throw failure;
        return Task.FromResult(Entries.TryGetValue(mediaId, out var entry) ? entry : null);
    }

    public Task WriteEntryAsync(TrackerTokens tokens, string mediaId, TrackerEntryWrite write, CancellationToken ct)
    {
        if (WriteFailures.TryDequeue(out var failure))
            throw failure;
        Writes.Enqueue((mediaId, write, tokens.AccessToken));
        var before = Entries.TryGetValue(mediaId, out var e) ? e : null;
        Entries[mediaId] = new TrackerEntry(write.Status, write.ChaptersRead ?? before?.ChaptersRead, write.VolumesRead ?? before?.VolumesRead,
            before?.TotalChapters, before?.TotalVolumes, before?.SeriesFinished ?? false, write.StartedOn ?? before?.StartedOn,
            write.FinishedOn ?? before?.FinishedOn);
        return Task.CompletedTask;
    }

    public Task RevokeAsync(TrackerAppSettings app, TrackerTokens tokens, CancellationToken ct)
    {
        Revokes.Enqueue(tokens);
        return Task.CompletedTask;
    }

    private TrackerTokens Issue()
    {
        var n = Interlocked.Increment(ref _issued);
        return new TrackerTokens($"{AccessPrefix}{Slug}-{n}", ConnectKind == TrackerConnectKind.OAuthPkce ? $"{RefreshPrefix}{Slug}-{n}" : null,
            DateTimeOffset.UtcNow + TokenLifetime);
    }
}
