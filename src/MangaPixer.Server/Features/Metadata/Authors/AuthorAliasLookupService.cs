namespace com.lifepixer.mangapixer.Server.Features.Metadata.Authors;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>One author id to request, "for" a library whose Fetch switch is on and that links a record naming it.</summary>
public sealed record AuthorAliasTarget(string AuthorId, long LibraryId);

/// <summary>What a look-up would do now: the known (eligible) author ids and the ones it would request.</summary>
public sealed record AuthorAliasPlan(int Eligible, int Fetched, IReadOnlyList<AuthorAliasTarget> ToFetch);

/// <summary>
/// Artists' other names (1.38.0, owner-approved 2026-10-09): reads MangaUpdates author records - <c>GET /v1/authors/{author_id}</c> -
/// ONLY when an admin asks. The author ids come only from creators on stored MangaUpdates records (<c>CreatorsJson[].providerId</c>)
/// that are linked (Confirmed, Auto or Collection about) in a library whose "Fetch from the web" is on; never a name, never a search.
/// Every request goes through <see cref="MetadataGateway.DetailCallAsync{T}"/> with the Interactive origin - every switch gate (the
/// config kill, Fetch with the CURRENT consent, the allowlist, the library), the persisted backoff, the ONE daily budget and the
/// MangaUpdates bucket - and NOT the automatic consent. Requests run one at a time, paced at one per second on the injected clock
/// (<see cref="Interval"/>); the gateway's own pacing is tied to the Automatic origin. Never part of automatic matching, the
/// scheduled refresh or any background pass of its own accord.
/// Logs record ids, counts, codes and timings only - never a name.
/// </summary>
public sealed class AuthorAliasLookupService
{
    /// <summary>At least this long between two requests of a look-up (owner: 1 per second).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>A stored answer is asked again only when it is older than this (owner: 180 days).</summary>
    public const int RefreshAfterDays = 180;

    /// <summary>A full bucket (another admin action in flight): wait and try the same id again, at most this many times in a row.</summary>
    internal const int MaxBusyRetries = 10;

    /// <summary>Link states whose record counts: an admin-confirmed link, an automatic link, a "Collection about" folder.</summary>
    private static readonly int[] s_linkedStates =
        [(int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto, (int)SeriesLinkState.CollectionAbout];

    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly MetadataProviderRegistry _providers;
    private readonly MetadataBudget _budget;
    private readonly MetadataBackoff _backoff;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthorAliasLookupService> _logger;

    public AuthorAliasLookupService(
        MangaPixerDbContext db,
        MetadataGateway gateway,
        MetadataProviderRegistry providers,
        MetadataBudget budget,
        MetadataBackoff backoff,
        TimeProvider time,
        ILogger<AuthorAliasLookupService> logger)
    {
        _db = db;
        _gateway = gateway;
        _providers = providers;
        _budget = budget;
        _backoff = backoff;
        _time = time;
        _logger = logger;
    }

    /// <summary>The eligible author ids and the ones a look-up would request now (stored data only, no request).</summary>
    public async Task<AuthorAliasPlan> PlanAsync(CancellationToken ct)
    {
        var linked = await (
                from link in _db.NodeSeriesLinks.AsNoTracking()
                where link.RecordId != null && s_linkedStates.Contains(link.State)
                join library in _db.Libraries.AsNoTracking() on link.LibraryId equals library.Id
                where library.MetadataEnabled
                join record in _db.MetadataRecords.AsNoTracking() on link.RecordId equals record.Id
                where record.Provider == MangaUpdatesProvider.ProviderId && record.CreatorsJson != null
                select new { link.LibraryId, RecordId = record.Id, record.CreatorsJson })
            .Distinct()
            .ToListAsync(ct);

        // Each id is requested "for" the smallest library id that links a record naming it.
        var libraryOf = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in linked)
        {
            foreach (var creator in MetadataJson.ReadList<MetadataJson.Creator>(row.CreatorsJson))
            {
                if (!IsAuthorId(creator.ProviderId))
                    continue;
                var id = creator.ProviderId!;
                if (!libraryOf.TryGetValue(id, out var library) || row.LibraryId < library)
                    libraryOf[id] = row.LibraryId;
            }
        }

        var stored = await _db.MetadataAuthors.AsNoTracking()
            .Where(a => a.Provider == MangaUpdatesProvider.ProviderId)
            .Select(a => new { a.ExternalId, a.Status, a.FetchedAt })
            .ToListAsync(ct);
        var storedById = stored.ToDictionary(a => a.ExternalId, StringComparer.Ordinal);
        var staleBefore = _time.GetUtcNow().AddDays(-RefreshAfterDays);

        var fetched = 0;
        var toFetch = new List<(AuthorAliasTarget Target, int Order)>();
        foreach (var (id, library) in libraryOf)
        {
            if (storedById.TryGetValue(id, out var row))
            {
                var answered = row.Status is (int)MetadataAuthorStatus.Ok or (int)MetadataAuthorStatus.NotFound;
                if (answered && row.FetchedAt > staleBefore)
                {
                    fetched++;
                    continue;
                }
                // Asked before: after every id never asked.
                toFetch.Add((new AuthorAliasTarget(id, library), 1));
            }
            else
            {
                toFetch.Add((new AuthorAliasTarget(id, library), 0));
            }
        }

        var ordered = toFetch
            .OrderBy(t => t.Order)
            .ThenBy(t => long.Parse(t.Target.AuthorId, NumberStyles.None, CultureInfo.InvariantCulture))
            .Select(t => t.Target)
            .ToList();
        return new AuthorAliasPlan(libraryOf.Count, fetched, ordered);
    }

    /// <summary>
    /// Why a look-up cannot start now (the gateway's refusal code), or null. No network: the switch gates for the library the first id
    /// would be requested for, then the provider's backoff and the daily budget. With no eligible library, <c>library_metadata_disabled</c>.
    /// </summary>
    public async Task<(string Code, DateTimeOffset? RetryAt)?> BlockedAsync(AuthorAliasPlan plan, CancellationToken ct)
    {
        var libraryId = plan.ToFetch.Count > 0
            ? plan.ToFetch[0].LibraryId
            : await _db.Libraries.AsNoTracking().Where(l => l.MetadataEnabled).OrderBy(l => l.Id).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct)
              ?? 0;
        if (await _gateway.CheckSwitchesAsync(libraryId, MetadataCallOrigin.Interactive, MangaUpdatesProvider.ProviderId, ct) is { } refusal)
            return (refusal.Code, null);
        if (await _backoff.ActiveUntilAsync(MangaUpdatesProvider.ProviderId, ct) is { } until)
            return ("provider_backoff", until);
        if ((await _budget.GetAsync(ct)).Exhausted)
            return ("budget_exhausted", null);
        return null;
    }

    /// <summary>Stored author records that carry other names (all libraries).</summary>
    public Task<int> CountWithOtherNamesAsync(CancellationToken ct) =>
        _db.MetadataAuthors.AsNoTracking()
            .CountAsync(a => a.Provider == MangaUpdatesProvider.ProviderId && a.Name != null && a.OtherNamesJson != null, ct);

    /// <summary>
    /// Requests the author records of <paramref name="targets"/> one after the other, at most one per <see cref="Interval"/>, and stores
    /// each answer. Stops on a provider backoff, a spent budget, a switch turned off (the answer that arrives after it is dropped), or
    /// <paramref name="ct"/>; the ids not reached stay as they were, so the next look-up resumes there. Never throws for a refusal or a
    /// provider failure (they end up in <paramref name="state"/>).
    /// </summary>
    public async Task RunAsync(IReadOnlyList<AuthorAliasTarget> targets, AuthorAliasRunState state, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation(LogEvents.Metadata.AuthorLookupStarted, "Author look-up started: {Count} author id(s)", targets.Count);
        var outcome = AuthorAliasRunState.Completed;
        DateTimeOffset? retryAt = null;
        try
        {
            if (_providers.Find(MangaUpdatesProvider.ProviderId) is not MangaUpdatesProvider provider)
            {
                outcome = AuthorAliasRunState.FailedOutcome;
                return;
            }

            DateTimeOffset? lastRequest = null;
            foreach (var target in targets)
            {
                var id = long.Parse(target.AuthorId, NumberStyles.None, CultureInfo.InvariantCulture);
                var busy = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (lastRequest is { } last && last + Interval - _time.GetUtcNow() is { Ticks: > 0 } wait)
                        await Task.Delay(wait, _time, ct);

                    var call = new MetadataCallContext { Origin = MetadataCallOrigin.Interactive };
                    var startedAt = _time.GetUtcNow();
                    try
                    {
                        var author = await _gateway.DetailCallAsync(MangaUpdatesProvider.ProviderId, "author", target.LibraryId,
                            c => provider.GetAuthorAsync(id, c), call, ct);
                        lastRequest = startedAt;
                        state.CountRequest();
                        await MetadataAuthorStore.SaveAsync(_db, target.AuthorId,
                            author is null ? MetadataAuthorStatus.NotFound : MetadataAuthorStatus.Ok, author, _time.GetUtcNow(), ct);
                        state.Count(author is null ? MetadataAuthorStatus.NotFound : MetadataAuthorStatus.Ok);
                        break;
                    }
                    catch (MetadataGatewayException ex)
                    {
                        if (call.RequestsSent > 0)
                        {
                            lastRequest = startedAt;
                            state.CountRequest();
                        }
                        switch (ex.Code)
                        {
                            case "provider_backoff":
                                outcome = AuthorAliasRunState.Backoff;
                                retryAt = ex.RetryAt;
                                return;
                            case "budget_exhausted":
                                outcome = AuthorAliasRunState.Budget;
                                return;
                            case "metadata_disabled" or "metadata_network_disabled" or "provider_not_allowed" or "automatic_off" or "unknown_provider":
                                outcome = AuthorAliasRunState.SwitchedOff;
                                return;
                            case "library_metadata_disabled" when call.RequestsSent == 0:
                                // This library was switched off meanwhile: its ids are not requested (nothing stored for them).
                                break;
                            case "library_metadata_disabled":
                                outcome = AuthorAliasRunState.SwitchedOff;
                                return;
                            case "provider_busy" when ++busy <= MaxBusyRetries:
                                await Task.Delay(Interval, _time, ct);
                                continue;
                            case "provider_busy":
                                outcome = AuthorAliasRunState.FailedOutcome;
                                return;
                            default:
                                // The provider answered with an error or something unreadable: asked again by the next look-up.
                                await MetadataAuthorStore.SaveAsync(_db, target.AuthorId, MetadataAuthorStatus.Failed, null, _time.GetUtcNow(), ct);
                                state.Count(MetadataAuthorStatus.Failed);
                                break;
                        }
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = AuthorAliasRunState.Cancelled;
        }
        catch (Exception ex)
        {
            outcome = AuthorAliasRunState.FailedOutcome;
            _logger.LogWarning(LogEvents.Metadata.AuthorLookupFailed, "Author look-up failed: {Error}", ex.GetType().Name);
        }
        finally
        {
            state.Finish(outcome, retryAt, _time.GetUtcNow());
            var snapshot = state.Snapshot();
            _logger.LogInformation(LogEvents.Metadata.AuthorLookupFinished,
                "Author look-up {Outcome}: {Requests} request(s), {Stored} stored, {NotFound} not found, {Failed} failed of {Total} in {ElapsedMs} ms",
                snapshot.Outcome, snapshot.Requests, snapshot.Stored, snapshot.NotFound, snapshot.Failed, snapshot.Total, watch.ElapsedMilliseconds);
        }
    }

    /// <summary>A MangaUpdates author id as stored on a creator: a positive decimal int64.</summary>
    internal static bool IsAuthorId(string? value) =>
        value is { Length: > 0 and <= 19 }
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
        && value[0] != '0';
}

/// <summary>The progress of one look-up (thread-safe; read by the status endpoint while the run writes it).</summary>
public sealed class AuthorAliasRunState
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Budget = "budget_exhausted";
    public const string Backoff = "provider_backoff";
    public const string SwitchedOff = "switched_off";
    public const string FailedOutcome = "failed";

    private readonly object _gate = new();
    private AuthorAliasRunDto _dto;

    public AuthorAliasRunState(int total, DateTimeOffset startedAt) =>
        _dto = new AuthorAliasRunDto { Total = total, StartedAt = startedAt, Outcome = Running };

    public AuthorAliasRunDto Snapshot()
    {
        lock (_gate)
            return _dto;
    }

    internal void CountRequest()
    {
        lock (_gate)
            _dto = _dto with { Requests = _dto.Requests + 1 };
    }

    internal void Count(MetadataAuthorStatus status)
    {
        lock (_gate)
        {
            _dto = status switch
            {
                MetadataAuthorStatus.Ok => _dto with { Stored = _dto.Stored + 1 },
                MetadataAuthorStatus.NotFound => _dto with { NotFound = _dto.NotFound + 1 },
                _ => _dto with { Failed = _dto.Failed + 1 },
            };
        }
    }

    internal void Finish(string outcome, DateTimeOffset? retryAt, DateTimeOffset at)
    {
        lock (_gate)
            _dto = _dto with { Outcome = outcome, RetryAt = retryAt, FinishedAt = at };
    }
}
