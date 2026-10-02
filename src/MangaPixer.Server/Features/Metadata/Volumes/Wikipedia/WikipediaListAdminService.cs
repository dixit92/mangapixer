namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes.Wikipedia;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The admin side of the Wikipedia companion (1.32.0): see it, choose its page, say "no list", check again. Each request to Wikipedia is an
/// ADMIN request (Interactive origin): it needs "Fetch from the web" with the current consent, Wikipedia on the allowlist and the library's
/// switch (the gateway's gates) - and nothing else (it does not depend on "Volume covers from the web"). Choosing the page and "no list" are
/// local. Audited with ids only.
/// </summary>
public sealed class WikipediaListAdminService
{
    private readonly MangaPixerDbContext _db;
    private readonly SeriesInfoResolver _resolver;
    private readonly WikipediaVolumeService _wikipedia;
    private readonly AuditService _audit;

    public WikipediaListAdminService(MangaPixerDbContext db, SeriesInfoResolver resolver, WikipediaVolumeService wikipedia, AuditService audit)
    {
        _db = db;
        _resolver = resolver;
        _wikipedia = wikipedia;
        _audit = audit;
    }

    private sealed record Target(CatalogNodeEntity Node, MetadataRecordEntity Series);

    private async Task<Target?> TargetAsync(string nodePublicId, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.PublicId == nodePublicId && n.Availability != (int)CatalogNodeAvailability.Tombstoned, ct);
        if (node is null)
            return null;
        var record = await _resolver.ResolveWebRecordAsync(node, ct);
        if (record is null || record.Provider != MetadataProviderAllowlist.MangaUpdates)
            return null;
        return new Target(node, await _db.MetadataRecords.FirstAsync(r => r.Id == record.Id, ct));
    }

    /// <summary>The Wikipedia companion of the node's series; null when the node has no MangaUpdates link. No network.</summary>
    public async Task<WikipediaListDto?> GetAsync(string nodePublicId, CancellationToken ct = default)
    {
        var target = await TargetAsync(nodePublicId, ct);
        return target is null ? null : await DtoAsync(target.Series.Id, ct);
    }

    /// <summary>Error codes: <c>not_found</c> (no node / no MangaUpdates link), <c>invalid_page</c>. Refusals and provider failures propagate.</summary>
    public async Task<(string? Error, WikipediaListDto? Result)> SetPageAsync(string nodePublicId, string? page, string? actor, CancellationToken ct = default)
    {
        var target = await TargetAsync(nodePublicId, ct);
        if (target is null)
            return ("not_found", null);
        if (!WikipediaApi.TryParseTitle(page, out var title))
            return ("invalid_page", null);
        await _wikipedia.SetPageAsync(target.Series, target.Node.LibraryId, title, call: null, ct);
        await _audit.RecordAsync(AuditActions.MetadataCompanionChange, "wikipedia-page", actor, ct: ct,
            targetLibraryId: target.Node.LibraryId, targetItemId: target.Node.Id);
        return (null, await DtoAsync(target.Series.Id, ct));
    }

    /// <summary>"No Wikipedia list": never asked again; the stored list is removed. Local only.</summary>
    public async Task<WikipediaListDto?> ClearAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var target = await TargetAsync(nodePublicId, ct);
        if (target is null)
            return null;
        await _wikipedia.ClearAsync(target.Series, ct);
        await _audit.RecordAsync(AuditActions.MetadataCompanionChange, "wikipedia-none", actor, ct: ct,
            targetLibraryId: target.Node.LibraryId, targetItemId: target.Node.Id);
        return await DtoAsync(target.Series.Id, ct);
    }

    /// <summary>"Check Wikipedia again": the page is looked at now (an admin request); "no list" is cleared first.</summary>
    public async Task<WikipediaListDto?> RecheckAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var target = await TargetAsync(nodePublicId, ct);
        if (target is null)
            return null;
        await _wikipedia.RecheckAsync(target.Series, target.Node.LibraryId, call: null, ct);
        await _audit.RecordAsync(AuditActions.MetadataCompanionChange, "wikipedia-recheck", actor, ct: ct,
            targetLibraryId: target.Node.LibraryId, targetItemId: target.Node.Id);
        return await DtoAsync(target.Series.Id, ct);
    }

    /// <summary>The DTO of a series' stored Wikipedia companion (an unknown series answers the empty <c>NotFound</c> state).</summary>
    public async Task<WikipediaListDto> DtoAsync(long seriesRecordId, CancellationToken ct)
    {
        var row = await _db.WikipediaLists.AsNoTracking().FirstOrDefaultAsync(l => l.RecordId == seriesRecordId, ct);
        if (row is null)
            return new WikipediaListDto { State = WikipediaListState.NotFound, Method = WikipediaListMethod.Wikidata };
        var map = await _db.SeriesVolumeMaps.AsNoTracking()
            .FirstOrDefaultAsync(m => m.RecordId == seriesRecordId && m.Source == (int)VolumeMapSource.WikipediaList, ct);
        return new WikipediaListDto
        {
            State = (WikipediaListState)row.State,
            Method = (WikipediaListMethod)row.Method,
            Code = row.RejectCode,
            AdminTitle = row.AdminTitle,
            Pages = WikipediaVolumeService.ReadPages(row.PagesJson)
                .Select(p => new WikipediaPageDto { Title = p.Title, Url = WikipediaApi.PageUrl(p.Title), Revision = p.Revision })
                .ToList(),
            Volumes = map is { State: (int)VolumeMapState.Ok } ? VolumeMapJson.Read(map.VolumesJson).Count : 0,
            Details = WikipediaVolumeService.ReadDetails(row.DetailsJson)
                .Select(d => new WikipediaVolumeDto { Volume = d.Volume, EnglishDate = d.Date, EnglishIsbn = d.Isbn })
                .ToList(),
            CheckedAt = row.CheckedAt,
            NextCheckAt = row.NextCheckAt,
        };
    }
}
