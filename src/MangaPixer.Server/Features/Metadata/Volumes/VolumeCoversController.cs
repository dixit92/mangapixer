namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The admin side of the MangaDex companion and the stored volume covers (1.29.0). Each companion action is an ADMIN
/// request (Interactive origin): it needs "Fetch from the web" with the current consent, MangaDex on the allowlist and the
/// library's switch (the gateway's gates), plus "Volume covers from the web" on - with Automatic matching off, these are
/// the only way MangaDex is contacted. Deleting stored covers is local only. Audited with ids and counts.
/// </summary>
public sealed class VolumeCoverAdminService
{
    private readonly MangaPixerDbContext _db;
    private readonly SeriesInfoResolver _resolver;
    private readonly CompanionLinkService _companions;
    private readonly VolumeCoverPass _pass;
    private readonly MetadataSettingsService _settings;
    private readonly VolumeCoverStore _store;
    private readonly MetadataAutoMatchState _signal;
    private readonly AuditService _audit;
    private readonly ILogger<VolumeCoverAdminService> _logger;

    public VolumeCoverAdminService(
        MangaPixerDbContext db, SeriesInfoResolver resolver, CompanionLinkService companions, VolumeCoverPass pass, MetadataSettingsService settings,
        VolumeCoverStore store, MetadataAutoMatchState signal, AuditService audit, ILogger<VolumeCoverAdminService> logger)
    {
        _db = db;
        _resolver = resolver;
        _companions = companions;
        _pass = pass;
        _settings = settings;
        _store = store;
        _signal = signal;
        _audit = audit;
        _logger = logger;
    }

    private sealed record Target(CatalogNodeEntity Node, MetadataRecordEntity Series);

    /// <summary>The node and the MangaUpdates record that applies to it (own or inherited link); null when none.</summary>
    private async Task<Target?> TargetAsync(string nodePublicId, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.PublicId == nodePublicId && n.Availability != (int)CatalogNodeAvailability.Tombstoned, ct);
        if (node is null)
            return null;
        var record = await _resolver.ResolveWebRecordAsync(node, ct);
        if (record is null || record.Provider != MetadataProviderAllowlist.MangaUpdates)
            return null;
        var tracked = await _db.MetadataRecords.FirstAsync(r => r.Id == record.Id, ct);
        return new Target(node, tracked);
    }

    /// <summary>The companions of the node's series (MangaDex, AniList); null when the node has no MangaUpdates link.</summary>
    public async Task<IReadOnlyList<CompanionDto>?> ListAsync(string nodePublicId, CancellationToken ct = default)
    {
        var target = await TargetAsync(nodePublicId, ct);
        return target is null ? null : await DtosAsync(target.Series.Id, ct);
    }

    private async Task<IReadOnlyList<CompanionDto>> DtosAsync(long seriesRecordId, CancellationToken ct)
    {
        var rows = await (
            from c in _db.MetadataCompanions.AsNoTracking()
            where c.RecordId == seriesRecordId
            join r in _db.MetadataRecords.AsNoTracking() on c.CompanionRecordId equals r.Id into records
            from r in records.DefaultIfEmpty()
            select new { c.Provider, c.State, c.CheckedAt, SiteUrl = r == null ? null : r.SiteUrl })
            .ToListAsync(ct);
        return rows
            .OrderBy(r => r.Provider == MetadataProviderAllowlist.MangaDex ? 0 : 1)
            .Select(r => new CompanionDto
            {
                Provider = r.Provider,
                ProviderName = MetadataHttp.Transport(r.Provider)?.DisplayName ?? r.Provider,
                SiteUrl = r.SiteUrl,
                State = (CompanionState)r.State,
                CheckedAt = r.CheckedAt,
            })
            .ToList();
    }

    private MetadataGatewayException? SwitchedOff() =>
        _settings.VolumeCoversDisabledByConfig
            ? new MetadataGatewayException(StatusCodes.Status409Conflict, "volume_covers_disabled",
                "Volume covers from the web are disabled in the server configuration (Metadata:AutoMatch:VolumeCovers).")
            : null;

    private async Task ThrowIfOffAsync(CancellationToken ct)
    {
        if (SwitchedOff() is { } off)
            throw off;
        var on = await _db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => (bool?)s.MetadataVolumeCoversEnabled).FirstOrDefaultAsync(ct) ?? true;
        if (!on)
            throw new MetadataGatewayException(StatusCodes.Status409Conflict, "volume_covers_off",
                "Volume covers from the web are off. An admin can turn them on in Metadata Manager > Settings.");
    }

    /// <summary>
    /// "Change MangaDex match...": a pasted MangaDex title URL or id, parsed locally, then one GET by id. Error codes:
    /// <c>not_found</c> (no node / no MangaUpdates link), <c>invalid_reference</c>, <c>mangadex_not_found</c>.
    /// </summary>
    public async Task<(string? Error, IReadOnlyList<CompanionDto>? Result)> SetReferenceAsync(
        string nodePublicId, string? reference, string? actor, CancellationToken ct = default)
    {
        var target = await TargetAsync(nodePublicId, ct);
        if (target is null)
            return ("not_found", null);
        if (!MangaDexProvider.TryParseReference(reference, out var mangaId))
            return ("invalid_reference", null);
        await ThrowIfOffAsync(ct);
        if (await _companions.SetByReferenceAsync(target.Series, target.Node.LibraryId, mangaId, ct) is null)
            return ("mangadex_not_found", null);
        await _audit.RecordAsync(AuditActions.MetadataCompanionChange, "reference", actor, ct: ct,
            targetLibraryId: target.Node.LibraryId, targetItemId: target.Node.Id);
        _signal.Signal();
        return (null, await DtosAsync(target.Series.Id, ct));
    }

    /// <summary>"Not on MangaDex": never looked up again; its stored covers are removed. Local only.</summary>
    public async Task<IReadOnlyList<CompanionDto>?> SetNoneAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var target = await TargetAsync(nodePublicId, ct);
        if (target is null)
            return null;
        await _companions.SetNoneAsync(target.Series, ct);
        await _audit.RecordAsync(AuditActions.MetadataCompanionChange, "none", actor, ct: ct,
            targetLibraryId: target.Node.LibraryId, targetItemId: target.Node.Id);
        return await DtosAsync(target.Series.Id, ct);
    }

    /// <summary>
    /// "Check MangaDex again": the companion, its cover list and volume list (and, without a volume list, the AniList
    /// totals) are read now, as an admin request. "Not on MangaDex" is cleared first, so it is looked up again.
    /// </summary>
    public async Task<IReadOnlyList<CompanionDto>?> RecheckAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var target = await TargetAsync(nodePublicId, ct);
        if (target is null)
            return null;
        await ThrowIfOffAsync(ct);
        var none = await _companions.FindAsync(target.Series.Id, CompanionLinkService.MangaDex, ct);
        if (none is { State: (int)CompanionState.None })
        {
            _db.MetadataCompanions.Remove(none);
            await _db.SaveChangesAsync(ct);
        }
        await RefreshSeriesAsync(target, ct);
        await _audit.RecordAsync(AuditActions.MetadataCompanionChange, "recheck", actor, ct: ct,
            targetLibraryId: target.Node.LibraryId, targetItemId: target.Node.Id);
        _signal.Signal();
        return await DtosAsync(target.Series.Id, ct);
    }

    /// <summary>
    /// The companion part of an admin's Refresh of a series (best effort, after the record itself was refreshed): skipped
    /// quietly when volume covers are off or MangaDex is not allowed; a refusal or provider failure never fails the Refresh.
    /// </summary>
    public async Task RefreshAfterRecordAsync(string nodePublicId, CancellationToken ct = default)
    {
        try
        {
            var target = await TargetAsync(nodePublicId, ct);
            if (target is null)
                return;
            await ThrowIfOffAsync(ct);
            var companion = await _companions.FindAsync(target.Series.Id, CompanionLinkService.MangaDex, ct);
            if (companion is { State: (int)CompanionState.None })
                return;
            await RefreshSeriesAsync(target, ct);
        }
        catch (MetadataGatewayException ex)
        {
            _logger.LogInformation(LogEvents.Metadata.CompanionChecked, "Companion refresh after a record refresh skipped: {Code}", ex.Code);
        }
    }

    private async Task RefreshSeriesAsync(Target target, CancellationToken ct)
    {
        var language = await _db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => s.MetadataCoverLanguage).FirstOrDefaultAsync(ct) ?? "en";
        var series = new VolumeSeries(target.Series.Id, target.Node.LibraryId, [target.Node.Id], null, DateTimeOffset.MinValue);
        await _pass.MetadataStepsAsync(series, language, call: null, force: true, allowAniList: true, ct);
    }

    /// <summary>
    /// After a settings change (1.29.0): a new preferred cover language makes every MangaDex cover list due once (so the
    /// covers in that language are found - budgeted like any other list refresh); turning volume covers on, the
    /// allowlist or a consent wakes the pass. Local only.
    /// </summary>
    public async Task AfterSettingsChangeAsync(MetadataSettingsDto before, MetadataSettingsDto after, CancellationToken ct = default)
    {
        if (!string.Equals(before.PreferredCoverLanguage, after.PreferredCoverLanguage, StringComparison.Ordinal))
        {
            await _db.SeriesVolumeMaps
                .Where(m => m.Source == (int)VolumeMapSource.MangaDexAggregate)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextCheckAt, (DateTimeOffset?)null), ct);
            _signal.Signal();
        }
        else if (after.VolumeCoversEnabled && (!before.VolumeCoversEnabled || before.AutoMatchEnabled != after.AutoMatchEnabled))
        {
            _signal.Signal();
        }
    }

    /// <summary>
    /// "Delete stored volume covers": every stored cover file and every known-cover row, and the automatic cover
    /// decisions that pointed at a web cover (they are decided again from what is left). Local only - nothing is sent.
    /// Admin cover choices of a deleted cover fall back to automatic (the column is cleared by the database).
    /// </summary>
    public async Task<int> DeleteStoredAsync(string? actor, CancellationToken ct = default)
    {
        var rows = await _db.VolumeCovers.CountAsync(ct);
        await _db.NodeAutoCovers
            .Where(a => a.Source == (int)AutoCoverSource.WebVolume || a.Source == (int)AutoCoverSource.WebMain)
            .ExecuteDeleteAsync(ct);
        await _db.NodeCoverChoices.Where(c => c.Mode == (int)CoverChoiceMode.VolumeCover).ExecuteDeleteAsync(ct);
        await _db.VolumeCovers.ExecuteDeleteAsync(ct);
        var files = _store.DeleteAll();
        await _audit.RecordAsync(AuditActions.MetadataVolumeCoversDelete, AuditResults.Success, actor, ct: ct);
        _logger.LogInformation(LogEvents.Metadata.VolumeCoversDeleted, "Stored volume covers deleted: {Rows} rows, {Files} files", rows, files);
        return rows;
    }
}

/// <summary>
/// Admin endpoints of the MangaDex companion and the stored volume covers (1.29.0). Every companion action sends
/// requests only through the gateway (typed <see cref="ApiError"/> refusals as the identify endpoints).
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata")]
[Authorize(Policy = "Admin")]
public sealed class VolumeCoversController : ControllerBase
{
    private readonly VolumeCoverAdminService _admin;
    private readonly VolumeCoverPass _pass;

    public VolumeCoversController(VolumeCoverAdminService admin, VolumeCoverPass pass)
    {
        _admin = admin;
        _pass = pass;
    }

    private string? Actor => User.Identity?.Name;

    /// <summary>The companions (MangaDex, AniList) of the series the node is linked to. No network.</summary>
    [HttpGet("nodes/{nodeId}/companions")]
    [ProducesResponseType<List<CompanionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Companions(string nodeId, CancellationToken ct) =>
        await _admin.ListAsync(nodeId, ct) is { } list ? Ok(list) : NotFound();

    /// <summary>"Change MangaDex match...": a MangaDex title URL or id (parsed locally; then one GET by id).</summary>
    [HttpPut("nodes/{nodeId}/companions/mangadex")]
    [ProducesResponseType<List<CompanionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetMangaDex(string nodeId, [FromBody] CompanionReferenceRequest request, CancellationToken ct)
    {
        try
        {
            var (error, result) = await _admin.SetReferenceAsync(nodeId, request.Reference, Actor, ct);
            return error switch
            {
                null => Ok(result),
                "not_found" => NotFound(),
                "invalid_reference" => BadRequest(new ApiError { Error = error, Message = "Paste a MangaDex title address (mangadex.org/title/...) or its id." }),
                _ => NotFound(new ApiError { Error = error, Message = "MangaDex has no title with this id." }),
            };
        }
        catch (MetadataGatewayException ex)
        {
            return MetadataIdentifyController.Error(this, ex);
        }
    }

    /// <summary>"Not on MangaDex": the series is never looked up there again. No network.</summary>
    [HttpDelete("nodes/{nodeId}/companions/mangadex")]
    [ProducesResponseType<List<CompanionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearMangaDex(string nodeId, CancellationToken ct) =>
        await _admin.SetNoneAsync(nodeId, Actor, ct) is { } list ? Ok(list) : NotFound();

    /// <summary>"Check MangaDex again": the companion and its lists, now (admin request).</summary>
    [HttpPost("nodes/{nodeId}/companions/mangadex/recheck")]
    [ProducesResponseType<List<CompanionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Recheck(string nodeId, CancellationToken ct)
    {
        try
        {
            return await _admin.RecheckAsync(nodeId, Actor, ct) is { } list ? Ok(list) : NotFound();
        }
        catch (MetadataGatewayException ex)
        {
            return MetadataIdentifyController.Error(this, ex);
        }
    }

    /// <summary>The background volume-cover pass: pending series, listed / stored covers, why it waits. No network.</summary>
    [HttpGet("volume-covers/status")]
    [ProducesResponseType<CoverPassStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(await _pass.StatusAsync(ct));

    /// <summary>"Delete stored volume covers" (local only).</summary>
    [HttpDelete("volume-covers")]
    [ProducesResponseType<CoverPassStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteStored(CancellationToken ct)
    {
        await _admin.DeleteStoredAsync(Actor, ct);
        return Ok(await _pass.StatusAsync(ct));
    }
}
