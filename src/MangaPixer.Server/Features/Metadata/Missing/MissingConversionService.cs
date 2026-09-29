namespace com.lifepixer.mangapixer.Server.Features.Metadata.Missing;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.AniList;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// Chapters per volume for the Missing report (1.28.0): an ADMIN action (one series, or a batch of up to
/// <see cref="MaxBatch"/>) asks AniList for the entry matching a linked MangaUpdates record and stores it as a
/// record row with <c>Provider = "anilist"</c> (no new column): its volumes in <c>OriginVolumes</c>, its chapters in
/// <c>LatestChapter</c>, and the cross reference <c>{"mangaupdates": "&lt;id&gt;"}</c> in <c>CrossIdsJson</c>. No folder
/// is linked to it. 1.29.0: the volume-cover pass also calls <see cref="LookupSeriesAsync"/> in the background (with
/// Automatic matching on) for a linked series that MangaDex gives no volume list for.
///
/// What is sent: the AniList id when one is already known (a stored row, the MangaUpdates record's own cross
/// reference, or - 1.29.0 - the linked MangaDex record's own AniList link); otherwise the LINKED MangaUpdates record's
/// title - never a folder or file name. The answer is
/// accepted only when one entry's title matches the record's titles (<see cref="MinTitleScore"/>) and the start
/// years agree within a year. Every call goes through <see cref="MetadataGateway.ConversionCallAsync{T}"/>.
/// Logs and the audit carry counts and ids only.
/// </summary>
public sealed class MissingConversionService
{
    public const int MaxBatch = 20;
    public const double MinTitleScore = 0.9;

    /// <summary>A lead the best entry needs over the next one when both clear the title score.</summary>
    public const double MinLead = 0.05;

    /// <summary>A miss is not asked again in a batch for this long (the per-series button always asks).</summary>
    public static readonly TimeSpan MissMemory = TimeSpan.FromHours(24);

    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly IUnitConversionProvider _provider;
    private readonly MissingReportService _report;
    private readonly AuditService _audit;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<MissingConversionService> _logger;

    public MissingConversionService(
        MangaPixerDbContext db,
        MetadataGateway gateway,
        IUnitConversionProvider provider,
        MissingReportService report,
        AuditService audit,
        IMemoryCache cache,
        TimeProvider time,
        ILogger<MissingConversionService> logger)
    {
        _db = db;
        _gateway = gateway;
        _provider = provider;
        _report = report;
        _audit = audit;
        _cache = cache;
        _time = time;
        _logger = logger;
    }

    /// <summary>The MangaUpdates id a stored AniList row points at, or null.</summary>
    public static string? LinkedMangaUpdatesId(string? crossIdsJson)
    {
        if (string.IsNullOrWhiteSpace(crossIdsJson))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(crossIdsJson)?.GetValueOrDefault(MetadataProviderAllowlist.MangaUpdates);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Chapters per volume of a FINISHED entry with both totals (a running entry's totals are not final).</summary>
    public static double? Ratio(int? status, int? volumes, int? chapters) =>
        status == (int)MetadataOriginStatus.Complete && volumes is > 0 && chapters is { } c && c >= volumes
            ? Math.Round((double)c / volumes.Value, 2)
            : null;

    private sealed record Target(long NodeId, string NodePublicId, long LibraryId, MetadataRecordEntity Record);

    /// <summary>
    /// Looks up one linked series folder. Error <c>not_found</c> when the node is not a folder with its own link to a
    /// MangaUpdates record. Gateway refusals and provider failures throw <see cref="MetadataGatewayException"/>.
    /// </summary>
    public async Task<(string? Error, MissingConversionResultDto? Result)> LookupAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var target = (await TargetsAsync(null, nodePublicId, ct)).FirstOrDefault();
        if (target is null)
            return ("not_found", null);
        var outcome = await LookupRecordAsync(target, ct);
        await _audit.RecordAsync(AuditActions.MetadataConversionLookup, outcome.ToString().ToLowerInvariant(), actor, ct: ct,
            targetLibraryId: target.LibraryId);
        var row = await _report.ForNodeAsync(nodePublicId, ct);
        return (null, new MissingConversionResultDto { Outcome = outcome, Row = row! });
    }

    /// <summary>
    /// Up to <see cref="MaxBatch"/> linked series without a stored source (a recent miss is skipped), one lookup each,
    /// paced by the gateway. Stops at the first refusal (budget, backoff, switch off) and says why. Error
    /// <c>library_not_found</c> for an unknown library filter.
    /// </summary>
    public async Task<(string? Error, MissingConversionBatchResultDto? Result)> LookupBatchAsync(
        string? libraryPublicId, string? actor, CancellationToken ct = default)
    {
        long? libraryId = null;
        if (!string.IsNullOrEmpty(libraryPublicId))
        {
            libraryId = await _db.Libraries.Where(l => l.PublicId == libraryPublicId).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct);
            if (libraryId is null)
                return ("library_not_found", null);
        }

        var covered = await CoveredMangaUpdatesIdsAsync(ct);
        var pending = (await TargetsAsync(libraryId, null, ct))
            .Where(t => !covered.Contains(t.Record.ExternalId))
            .GroupBy(t => t.Record.Id)
            .Select(g => g.First())
            .ToList();
        var due = pending.Where(t => !_cache.TryGetValue(MissKey(t.Record.ExternalId), out _)).ToList();

        int looked = 0, found = 0, noCounts = 0, noMatch = 0;
        MetadataGatewayException? stopped = null;
        foreach (var target in due.Take(MaxBatch))
        {
            try
            {
                var outcome = await LookupRecordAsync(target, ct);
                looked++;
                switch (outcome)
                {
                    case MissingConversionOutcome.Found: found++; break;
                    case MissingConversionOutcome.NoCounts: noCounts++; break;
                    default: noMatch++; break;
                }
            }
            catch (MetadataGatewayException ex)
            {
                stopped = ex;
                break;
            }
        }
        if (looked > 0)
            await _audit.RecordAsync(AuditActions.MetadataConversionLookup, $"batch_{looked}", actor, ct: ct, targetLibraryId: libraryId);
        _logger.LogInformation(LogEvents.Metadata.ConversionLookups,
            "Chapters-per-volume lookups: {Looked} looked up, {Found} found, {NoCounts} without counts, {NoMatch} no match, stopped {Code}",
            looked, found, noCounts, noMatch, stopped?.Code ?? "no");
        return (null, new MissingConversionBatchResultDto
        {
            Looked = looked,
            Found = found,
            NoCounts = noCounts,
            NoMatch = noMatch,
            Remaining = pending.Count - found - noCounts,
            StoppedCode = stopped?.Code,
            StoppedMessage = stopped?.Message,
        });
    }

    /// <summary>MangaUpdates ids that already have a stored AniList row.</summary>
    private async Task<HashSet<string>> CoveredMangaUpdatesIdsAsync(CancellationToken ct) =>
        (await _db.MetadataRecords.AsNoTracking()
            .Where(r => r.Provider == MetadataProviderAllowlist.AniList && r.CrossIdsJson != null)
            .Select(r => r.CrossIdsJson)
            .ToListAsync(ct))
        .Select(LinkedMangaUpdatesId).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>Folders with their own confirmed / automatic link to a MangaUpdates record, by name.</summary>
    private async Task<List<Target>> TargetsAsync(long? libraryId, string? nodePublicId, CancellationToken ct)
    {
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        var folder = (int)CatalogNodeKind.Folder;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var rows = await (
            from l in _db.NodeSeriesLinks
            where (l.State == confirmed || l.State == auto) && l.RecordId != null && (libraryId == null || l.LibraryId == libraryId)
            join n in _db.CatalogNodes on l.NodeId equals n.Id
            where n.Kind == folder && n.Availability != tombstoned && (nodePublicId == null || n.PublicId == nodePublicId)
            join r in _db.MetadataRecords on l.RecordId equals r.Id
            where r.Provider == MetadataProviderAllowlist.MangaUpdates
            orderby n.SortKey, n.Id
            select new { n.Id, n.PublicId, n.LibraryId, Record = r })
            .ToListAsync(ct);
        return rows.Select(x => new Target(x.Id, x.PublicId, x.LibraryId, x.Record)).ToList();
    }

    private async Task<MissingConversionOutcome> LookupRecordAsync(Target target, CancellationToken ct) =>
        (await LookupSeriesAsync(target.Record, target.LibraryId, null, null, ct)).Outcome;

    /// <summary>The stored AniList row of a MangaUpdates record, or null.</summary>
    public async Task<MetadataRecordEntity?> StoredRowAsync(string muExternalId, CancellationToken ct = default) =>
        (await _db.MetadataRecords
            .Where(r => r.Provider == MetadataProviderAllowlist.AniList && r.CrossIdsJson != null)
            .ToListAsync(ct))
        .FirstOrDefault(r => LinkedMangaUpdatesId(r.CrossIdsJson) == muExternalId);

    /// <summary>
    /// Looks up the AniList entry of one linked MangaUpdates record <paramref name="mu"/> and stores it (1.29.0: shared
    /// by the admin actions and the volume-cover pass). The id asked for, first that applies: a stored row's, the
    /// MangaUpdates record's own cross reference, <paramref name="companionAniListId"/> (the linked MangaDex record's
    /// <c>links.al</c>); otherwise a search by <paramref name="mu"/>'s title. <paramref name="call"/> carries the
    /// origin (null = an admin action). Gateway refusals and provider failures throw <see cref="MetadataGatewayException"/>.
    /// </summary>
    public async Task<(MissingConversionOutcome Outcome, MetadataRecordEntity? Record, bool ById)> LookupSeriesAsync(
        MetadataRecordEntity mu, long libraryId, string? companionAniListId, MetadataCallContext? call, CancellationToken ct = default)
    {
        var existing = await StoredRowAsync(mu.ExternalId, ct);
        var knownId = existing?.ExternalId ?? CrossId(mu.CrossIdsJson)
            ?? (int.TryParse(companionAniListId, NumberStyles.None, CultureInfo.InvariantCulture, out var al) && al > 0 ? companionAniListId : null);

        ConversionCandidate? match;
        if (knownId is not null)
        {
            match = await _gateway.ConversionCallAsync(_provider, "get", libraryId, c => _provider.GetAsync(knownId, c), ct, call);
        }
        else
        {
            var query = MetadataGateway.NormalizeQuery(mu.Title);
            if (query.Length is 0 or > MetadataGateway.MaxQueryLength)
                return (Miss(mu), existing, false);
            var candidates = await _gateway.ConversionCallAsync(_provider, "search", libraryId, c => _provider.SearchAsync(query, c), ct, call);
            match = Pick(mu, candidates);
        }
        if (match is null)
            return (Miss(mu), existing, knownId is not null);

        var now = _time.GetUtcNow();
        var record = existing ?? await _db.MetadataRecords.FirstOrDefaultAsync(r => r.Provider == MetadataProviderAllowlist.AniList && r.ExternalId == match.ExternalId, ct);
        if (record is null)
        {
            record = new MetadataRecordEntity { PublicId = await NewPublicIdAsync(ct), Provider = MetadataProviderAllowlist.AniList, ExternalId = match.ExternalId };
            _db.MetadataRecords.Add(record);
        }
        record.ExternalId = match.ExternalId;
        record.Title = match.Title;
        record.AltTitlesJson = MetadataJson.WriteList(match.AltTitles.ToList());
        record.ProviderType = match.Format;
        record.StartYear = match.StartYear;
        record.OriginStatus = (int?)match.Status;
        record.OriginVolumes = match.Volumes;
        record.LatestChapter = match.Chapters;
        record.SiteUrl = match.SiteUrl;
        record.CrossIdsJson = JsonSerializer.Serialize(new Dictionary<string, string> { [MetadataProviderAllowlist.MangaUpdates] = mu.ExternalId });
        record.FetchedAt = now;
        record.FetchState = 0;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(LogEvents.Metadata.RecordStored, "Metadata record {RecordId} stored ({Provider} {ExternalId})",
            record.Id, record.Provider, record.ExternalId);
        var outcome = Ratio(record.OriginStatus, record.OriginVolumes, match.Chapters) is null ? MissingConversionOutcome.NoCounts : MissingConversionOutcome.Found;
        return (outcome, record, knownId is not null);
    }

    private MissingConversionOutcome Miss(MetadataRecordEntity mu)
    {
        _cache.Set(MissKey(mu.ExternalId), true, MissMemory);
        return MissingConversionOutcome.NoMatch;
    }

    private static string MissKey(string muExternalId) => "anilist-miss:" + muExternalId;

    /// <summary>
    /// The one entry that matches the record: title score at least <see cref="MinTitleScore"/> against any of the
    /// record's titles, start years within one year when both are known, and a clear lead over a second such entry.
    /// </summary>
    internal static ConversionCandidate? Pick(MetadataRecordEntity mu, IReadOnlyList<ConversionCandidate> candidates)
    {
        var titles = new[] { mu.Title }.Concat(MetadataJson.ReadList<string>(mu.AltTitlesJson)).ToList();
        var scored = candidates
            .Where(c => mu.StartYear is not { } y || c.StartYear is not { } cy || Math.Abs(y - cy) <= 1)
            .Select(c => (Candidate: c, Score: TitleSimilarity.Best(titles, new[] { c.Title }.Concat(c.AltTitles))))
            .Where(x => x.Score >= MinTitleScore)
            .OrderByDescending(x => x.Score)
            .ToList();
        if (scored.Count == 0)
            return null;
        if (scored.Count > 1 && scored[0].Score - scored[1].Score < MinLead)
            return null;
        return scored[0].Candidate;
    }

    /// <summary>An AniList id the MangaUpdates record itself names (<c>{"anilist": "123"}</c>), when valid.</summary>
    private static string? CrossId(string? crossIdsJson)
    {
        if (string.IsNullOrWhiteSpace(crossIdsJson))
            return null;
        try
        {
            var id = JsonSerializer.Deserialize<Dictionary<string, string>>(crossIdsJson)?.GetValueOrDefault(MetadataProviderAllowlist.AniList);
            return int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> NewPublicIdAsync(CancellationToken ct)
    {
        while (true)
        {
            var id = "mr" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            if (!await _db.MetadataRecords.AnyAsync(r => r.PublicId == id, ct))
                return id;
        }
    }
}
