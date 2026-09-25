namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Series-metadata settings (1.24.0): the two owner-decided toggles, each global
/// (<c>app_settings</c>) and per library (<c>libraries</c>):
/// - "Show series information": when off, ALL metadata (web and ComicInfo) is
///   hidden from every surface; the data stays stored. Effective = global AND
///   library. Stored inverted (<c>MetadataSeriesInfoHidden</c>) so 0 = shown.
/// - "Fetch from the web": the network switch lane B2's gateway enforces. B1 only
///   stores it (and requires the current consent version to turn it on).
/// Plus the daily budget and the read-only network status fields B2 maintains.
/// </summary>
public sealed class MetadataSettingsService
{
    /// <summary>Default daily outbound request budget (owner decision #10).</summary>
    public const int DefaultDailyBudget = 5000;

    /// <summary>Sanity bound on the budget field (the owner wanted no product cap).</summary>
    public const int MaxDailyBudget = 1_000_000;

    /// <summary>Operator hard kill (overrides the UI), read by lane B2's gateway too.</summary>
    public const string NetworkDisabledConfigKey = "Metadata:NetworkDisabled";

    private readonly MangaPixerDbContext _db;
    private readonly AuditService _audit;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<MetadataSettingsService> _logger;

    public MetadataSettingsService(
        MangaPixerDbContext db,
        AuditService audit,
        IConfiguration configuration,
        TimeProvider time,
        ILogger<MetadataSettingsService> logger)
    {
        _db = db;
        _audit = audit;
        _configuration = configuration;
        _time = time;
        _logger = logger;
    }

    /// <summary>True when the operator disabled all metadata network access in configuration.</summary>
    public bool NetworkDisabledByConfig =>
        bool.TryParse(_configuration[NetworkDisabledConfigKey], out var disabled) && disabled;

    /// <summary>
    /// True when series information of <paramref name="libraryId"/> must be hidden
    /// (global OR library "Show series information" off).
    /// </summary>
    public async Task<bool> IsSeriesInfoHiddenAsync(long libraryId, CancellationToken ct = default)
    {
        if (await IsGloballyHiddenAsync(ct))
            return true;
        return await _db.Libraries
            .Where(l => l.Id == libraryId)
            .Select(l => l.MetadataSeriesInfoHidden)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> IsGloballyHiddenAsync(CancellationToken ct = default) =>
        await _db.AppSettings
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => s.MetadataSeriesInfoHidden)
            .FirstOrDefaultAsync(ct);

    public async Task<MetadataSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var row = await _db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);

        var today = UtcDay(_time.GetUtcNow());
        var budgetUsed = row?.MetadataBudgetDayUtc is { } day && UtcDay(day) == today ? row.MetadataBudgetUsed : 0;

        var linkCounts = await _db.NodeSeriesLinks
            .GroupBy(l => l.LibraryId)
            .Select(g => new { LibraryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.LibraryId, x => x.Count, ct);

        var libraries = await _db.Libraries.AsNoTracking()
            .OrderBy(l => l.DisplayName)
            .Select(l => new { l.Id, l.PublicId, l.DisplayName, l.MetadataEnabled, l.MetadataSeriesInfoHidden, l.MetadataPrecedence })
            .ToListAsync(ct);

        var (total, read, found) = await ComicInfoBackfillService.CountAsync(_db, ct);

        return new MetadataSettingsDto
        {
            ShowSeriesInfo = !(row?.MetadataSeriesInfoHidden ?? false),
            FetchEnabled = row?.MetadataEnabled ?? false,
            NetworkDisabledByConfig = NetworkDisabledByConfig,
            AcceptedConsentVersion = row?.MetadataConsentVersion,
            CurrentConsentVersion = MetadataConsent.CurrentVersion,
            ConsentAt = row?.MetadataConsentAt,
            DailyBudget = row?.MetadataDailyBudget ?? DefaultDailyBudget,
            DefaultDailyBudget = DefaultDailyBudget,
            BudgetUsedToday = budgetUsed,
            BackoffUntil = row?.MetadataBackoffUntil,
            LastErrorAt = row?.MetadataLastErrorAt,
            LastErrorCode = row?.MetadataLastErrorCode,
            ComicInfo = new MetadataComicInfoStatsDto
            {
                ArchivesTotal = total,
                ArchivesRead = read,
                ArchivesWithComicInfo = found,
            },
            WebRecordCount = await _db.MetadataRecords.CountAsync(ct),
            Libraries = libraries.Select(l => new MetadataLibrarySettingsDto
            {
                LibraryId = l.PublicId,
                Name = l.DisplayName,
                FetchEnabled = l.MetadataEnabled,
                ShowSeriesInfo = !l.MetadataSeriesInfoHidden,
                Precedence = (MetadataPrecedence?)l.MetadataPrecedence,
                LinkCount = linkCounts.GetValueOrDefault(l.Id),
            }).ToList(),
        };
    }

    /// <summary>Applies a partial update; returns a validation error code, or null on success.</summary>
    public async Task<string?> UpdateAsync(UpdateMetadataSettingsRequest request, string? actor, CancellationToken ct = default)
    {
        if (request.DailyBudget is { } budget && (budget < 1 || budget > MaxDailyBudget))
            return "invalid_daily_budget";

        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);
        if (row is null)
        {
            row = new AppSettingsEntity();
            _db.AppSettings.Add(row);
        }

        // Turning the web switch on needs the CURRENT consent version - unless it is
        // already on with that consent (a client re-sending its whole state, e.g. to
        // change the budget, must not be forced to re-consent).
        var consentGiven = request.AcceptedConsentVersion == MetadataConsent.CurrentVersion;
        var alreadyConsented = row.MetadataEnabled && row.MetadataConsentVersion == MetadataConsent.CurrentVersion;
        if (request.FetchEnabled == true && !consentGiven && !alreadyConsented)
            return "consent_required";

        var audits = new List<string>();
        if (request.ShowSeriesInfo is { } show && show == row.MetadataSeriesInfoHidden)
        {
            row.MetadataSeriesInfoHidden = !show;
            audits.Add(show ? AuditActions.MetadataShowEnable : AuditActions.MetadataShowDisable);
        }
        if (request.FetchEnabled == true && !alreadyConsented)
        {
            // Off -> on, or on with a stale consent version (a network-surface change re-prompts).
            row.MetadataEnabled = true;
            row.MetadataConsentVersion = MetadataConsent.CurrentVersion;
            row.MetadataConsentAt = _time.GetUtcNow();
            audits.Add(AuditActions.MetadataSettingsEnable);
        }
        else if (request.FetchEnabled == false && row.MetadataEnabled)
        {
            row.MetadataEnabled = false;
            audits.Add(AuditActions.MetadataSettingsDisable);
        }
        if (request.ResetDailyBudget && row.MetadataDailyBudget is not null)
        {
            row.MetadataDailyBudget = null;
            audits.Add(AuditActions.MetadataSettingsChange);
        }
        else if (request.DailyBudget is { } newBudget && newBudget != row.MetadataDailyBudget)
        {
            row.MetadataDailyBudget = newBudget;
            audits.Add(AuditActions.MetadataSettingsChange);
        }

        await _db.SaveChangesAsync(ct);
        foreach (var action in audits.Distinct())
        {
            // The consent version rides in Result for enable, as designed.
            var result = action == AuditActions.MetadataSettingsEnable
                ? $"consent_v{MetadataConsent.CurrentVersion}"
                : AuditResults.Success;
            await _audit.RecordAsync(action, result, actor, ct: ct);
        }
        if (audits.Count > 0)
            _logger.LogInformation(LogEvents.Metadata.SettingsChanged, "Metadata settings changed: {Count} change(s)", audits.Count);
        return null;
    }

    /// <summary>Applies a partial update to one library's toggles; false when the library does not exist.</summary>
    public async Task<bool> UpdateLibraryAsync(string libraryPublicId, UpdateMetadataLibraryRequest request, string? actor, CancellationToken ct = default)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == libraryPublicId, ct);
        if (library is null)
            return false;

        var audits = new List<string>();
        if (request.FetchEnabled is { } fetch && fetch != library.MetadataEnabled)
        {
            library.MetadataEnabled = fetch;
            audits.Add(fetch ? AuditActions.MetadataLibraryEnable : AuditActions.MetadataLibraryDisable);
        }
        if (request.ShowSeriesInfo is { } show && show == library.MetadataSeriesInfoHidden)
        {
            library.MetadataSeriesInfoHidden = !show;
            audits.Add(show ? AuditActions.MetadataLibraryShowEnable : AuditActions.MetadataLibraryShowDisable);
        }

        await _db.SaveChangesAsync(ct);
        foreach (var action in audits)
            await _audit.RecordAsync(action, AuditResults.Success, actor, ct: ct, targetLibraryId: library.Id);
        if (audits.Count > 0)
            _logger.LogInformation(LogEvents.Metadata.SettingsChanged, "Metadata library settings changed for library {LibraryId}: {Count} change(s)", library.Id, audits.Count);
        return true;
    }

    private static DateOnly UtcDay(DateTimeOffset value) => DateOnly.FromDateTime(value.UtcDateTime);
}
