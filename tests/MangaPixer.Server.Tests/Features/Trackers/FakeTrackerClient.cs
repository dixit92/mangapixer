namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Collections.Concurrent;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers;

/// <summary>
/// A fake <see cref="ITrackerClient"/> for the engine tests (1.43.0): no network - entries live in memory, every call is recorded, and
/// failures can be queued. Tokens and passwords it hands out are made-up marker strings so a test can search logs / responses / the
/// database for them.
/// </summary>
internal sealed class FakeTrackerClient(string slug, TrackerConnectKind kind) : ITrackerClient, ITrackerListReader, ITrackerScores
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

    public int CallCount => Reads.Count + Writes.Count + ListReads.Count + ScoreReads.Count + ScoreWrites.Count + ScorePages.Count;

    /// <summary>1.43.0: a whole-list reader with pages of this size (null = it reads series by series, like MangaUpdates).</summary>
    public int? ListPageSize { get; set; }

    /// <summary>The offsets of the whole-list pages read.</summary>
    public ConcurrentQueue<int> ListReads { get; } = new();

    public ConcurrentQueue<TrackerException> ListFailures { get; } = new();

    public bool ReadsWholeList => ListPageSize is not null;

    public Task<TrackerListPage> ReadListPageAsync(TrackerTokens tokens, int offset, CancellationToken ct)
    {
        ListReads.Enqueue(offset);
        if (ListFailures.TryDequeue(out var failure))
            throw failure;
        var size = ListPageSize ?? int.MaxValue;
        var all = Entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        var page = all.Skip(offset).Take(size).Select(e => new TrackerListItem(e.Key, e.Value)).ToList();
        return Task.FromResult(new TrackerListPage(page, offset + page.Count < all.Count ? offset + page.Count : null));
    }

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
            write.FinishedOn ?? before?.FinishedOn)
        { Score = before?.Score };
        return Task.CompletedTask;
    }

    // 1.53.0 (lane T) scores. ScoreInEntry = true behaves like MyAnimeList (the score is TrackerEntry.Score, read with the entry);
    // false like MangaUpdates (SeparateScores, one request per read).

    public bool ScoreInEntry { get; set; } = true;

    /// <summary>The user's scores when <see cref="ScoreInEntry"/> is false (absent = none).</summary>
    public ConcurrentDictionary<string, int> SeparateScores { get; } = new();

    public ConcurrentQueue<string> ScoreReads { get; } = new();
    public ConcurrentQueue<(string MediaId, int? Score)> ScoreWrites { get; } = new();
    public ConcurrentQueue<string?> ScorePages { get; } = new();
    public ConcurrentQueue<TrackerException> ScoreWriteFailures { get; } = new();
    public ConcurrentQueue<TrackerException> ScorePageFailures { get; } = new();

    /// <summary>Scored entries per page of <see cref="ReadScorePageAsync"/>.</summary>
    public int ScorePageSize { get; set; } = 2;

    public Task<int?> ReadScoreAsync(TrackerTokens tokens, string mediaId, TrackerEntry entry, CancellationToken ct)
    {
        if (ScoreInEntry)
            return Task.FromResult(entry.Score);
        ScoreReads.Enqueue(mediaId);
        return Task.FromResult(SeparateScores.TryGetValue(mediaId, out var score) ? score : (int?)null);
    }

    public Task WriteScoreAsync(TrackerTokens tokens, string mediaId, int? score, CancellationToken ct)
    {
        if (ScoreWriteFailures.TryDequeue(out var failure))
            throw failure;
        ScoreWrites.Enqueue((mediaId, score));
        if (!ScoreInEntry)
        {
            if (score is { } s)
                SeparateScores[mediaId] = s;
            else
                SeparateScores.TryRemove(mediaId, out _);
        }
        else if (Entries.TryGetValue(mediaId, out var entry))
        {
            Entries[mediaId] = entry with { Score = score };
        }
        return Task.CompletedTask;
    }

    public Task<TrackerScorePage> ReadScorePageAsync(TrackerTokens tokens, string? cursor, CancellationToken ct)
    {
        ScorePages.Enqueue(cursor);
        if (ScorePageFailures.TryDequeue(out var failure))
            throw failure;
        var offset = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var scored = Entries.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => (e.Key, Score: ScoreInEntry ? e.Value.Score : SeparateScores.TryGetValue(e.Key, out var s) ? s : (int?)null))
            .Where(e => e.Score is not null).ToList();
        var page = scored.Skip(offset).Take(ScorePageSize).Select(e => new TrackerScoreItem(e.Key, e.Score!.Value)).ToList();
        var next = offset + page.Count < scored.Count ? (offset + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new TrackerScorePage(page, next));
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
