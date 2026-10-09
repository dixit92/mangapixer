namespace com.lifepixer.mangapixer.Server.Features.Export;

using System.Globalization;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes.Wikipedia;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>One current export item of a library: the node and its item (times not set yet - the rebuild sets them).</summary>
public sealed record ExportBuiltItem(long NodeId, string NodePublicId, ExportItemDto Item);

/// <summary>
/// Computes the current export items of one library (1.33.0) from stored rows only: every live node (folder or archive) of the
/// library with its OWN link row, its trail of on-disk names, the linked record, the companions and their official links, the
/// per-volume list, the Completion answer (linked folders, one <see cref="SeriesProgressLoader"/> batch for the whole library) and the
/// refresh cadence. One query per table for the whole library; never contacts a provider; logs nothing (the rebuild logs counts).
/// </summary>
public sealed class ExportItemBuilder(MangaPixerDbContext db)
{
    private sealed record LinkRow(
        long NodeId, string PublicId, int Kind, long? ParentId, string Name, int State, long? RecordId, int? Method, double? Score, DateTimeOffset UpdatedAt);

    private sealed record CompanionRecord(long Id, string ExternalId, string? ExtraJson, double? LatestChapter, int? OriginVolumes);

    public async Task<IReadOnlyList<ExportBuiltItem>> BuildAsync(long libraryId, DateTimeOffset now, CancellationToken ct = default)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var folder = (int)CatalogNodeKind.Folder;
        var links = await (
                from l in db.NodeSeriesLinks.AsNoTracking()
                join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
                where n.LibraryId == libraryId && n.Availability != tombstoned
                select new LinkRow(n.Id, n.PublicId, n.Kind, n.ParentId, n.DisplayName, l.State, l.RecordId, l.MatchMethod, l.MatchScore, l.UpdatedAt))
            .ToListAsync(ct);
        if (links.Count == 0)
            return [];

        // Trails: the live folders of the library (the library root is not a node).
        var folders = await db.CatalogNodes.AsNoTracking()
            .Where(n => n.LibraryId == libraryId && n.Kind == folder && n.Availability != tombstoned)
            .Select(n => new { n.Id, n.ParentId, n.DisplayName })
            .ToDictionaryAsync(n => n.Id, n => (n.ParentId, n.DisplayName), ct);

        var recordIds = links.Where(l => l.RecordId is not null).Select(l => l.RecordId!.Value).Distinct().ToList();
        var records = await db.MetadataRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var companions = await db.MetadataCompanions.AsNoTracking()
            .Where(c => recordIds.Contains(c.RecordId) && c.CompanionRecordId != null)
            .Select(c => new { c.RecordId, c.Provider, CompanionRecordId = c.CompanionRecordId!.Value })
            .ToListAsync(ct);
        var companionsOf = companions.ToLookup(c => c.RecordId);
        var companionIds = companions.Select(c => c.CompanionRecordId).Distinct().ToList();
        var companionRecords = await db.MetadataRecords.AsNoTracking().Where(r => companionIds.Contains(r.Id))
            .Select(r => new CompanionRecord(r.Id, r.ExternalId, r.ExtraJson, r.LatestChapter, r.OriginVolumes))
            .ToDictionaryAsync(r => r.Id, ct);
        var aniListByMu = await AniListByMangaUpdatesIdAsync(ct);
        var maps = (await db.SeriesVolumeMaps.AsNoTracking().Where(m => recordIds.Contains(m.RecordId)).ToListAsync(ct)).ToLookup(m => m.RecordId);
        var wikipedia = await db.WikipediaLists.AsNoTracking().Where(w => recordIds.Contains(w.RecordId))
            .Select(w => new { w.RecordId, w.DetailsJson, w.CheckedAt })
            .ToDictionaryAsync(w => w.RecordId, ct);

        // Completion: the Completion tab's scope - folders with a Confirmed / Auto link to a record.
        var progressTargets = links
            .Where(l => l.Kind == folder && l.RecordId is { } rid && records.ContainsKey(rid)
                && l.State is (int)SeriesLinkState.Confirmed or (int)SeriesLinkState.Auto)
            .Select(l => new SeriesProgressTarget(l.NodeId, l.RecordId!.Value))
            .ToList();
        var progress = await new SeriesProgressLoader(db).LoadAsync(progressTargets, ct);

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var result = new List<ExportBuiltItem>(links.Count);
        foreach (var link in links.OrderBy(l => l.NodeId))
        {
            var record = link.RecordId is { } recordId ? records.GetValueOrDefault(recordId) : null;
            // 1.34.0: a "Collection about" folder carries its series' record as a label only - no companions, links, volumes, completion
            // or refresh, even when the same record is linked to a series folder elsewhere.
            var isSeries = SeriesLinkStates.IsSeries((SeriesLinkState)link.State);
            var seriesCompanions = record is null || !isSeries ? [] : companionsOf[record.Id].ToList();
            var mangadex = seriesCompanions.Where(c => c.Provider == MetadataProviderAllowlist.MangaDex)
                .Select(c => companionRecords.GetValueOrDefault(c.CompanionRecordId)).FirstOrDefault(r => r is not null);
            var aniList = seriesCompanions.Where(c => c.Provider == MetadataProviderAllowlist.AniList)
                .Select(c => companionRecords.GetValueOrDefault(c.CompanionRecordId)).FirstOrDefault(r => r is not null)
                ?? (isSeries && record is { Provider: MetadataProviderAllowlist.MangaUpdates } ? aniListByMu.GetValueOrDefault(record.ExternalId) : null);
            var details = isSeries && record is not null && wikipedia.TryGetValue(record.Id, out var w) ? w : null;

            var item = new ExportItemDto
            {
                NodeId = link.PublicId,
                NodeKind = link.Kind == folder ? ExportVocabulary.Folder : ExportVocabulary.Archive,
                CarriedFrom = null,
                Trail = Trail(link, folders),
                UpdatedAt = default,
                Link = new ExportLinkDto
                {
                    State = ExportVocabulary.LinkState((SeriesLinkState)link.State),
                    Method = ExportVocabulary.Method(link.Method),
                    Score = link.Score is { } s ? Math.Round(s, 4) : null,
                    UpdatedAt = ExportJson.Truncate(link.UpdatedAt),
                },
                Record = record is null ? null : Record(record),
                Companions = new ExportCompanionsDto
                {
                    Mangadex = mangadex?.ExternalId,
                    Anilist = aniList is not null && long.TryParse(aniList.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var alId)
                        ? new ExportAniListDto { Id = alId, Chapters = WholeOrNull(aniList.LatestChapter), Volumes = aniList.OriginVolumes }
                        : null,
                },
                OfficialLinks = OfficialLinks(mangadex?.ExtraJson),
                Volumes = record is null || !isSeries
                    ? null
                    : ExportVolumes.Project(maps[record.Id].ToList(), WikipediaVolumeService.ReadDetails(details?.DetailsJson), details?.CheckedAt, today),
                // 1.39.0: tracking off - no Completion block (null, as for any item without an answer) and the item says tracking "off".
                Completion = progress.TryGetValue(link.NodeId, out var entry) && !entry.Result.Facts.TrackingOff ? Completion(entry) : null,
                Tracking = progress.TryGetValue(link.NodeId, out var tracked) && tracked.Result.Facts.TrackingOff ? ExportVocabulary.TrackingOff : null,
                Refresh = record is null || !isSeries ? null : Refresh(record),
                Duplicates = progress.TryGetValue(link.NodeId, out var series) ? Duplicates(series.Rows) : null,
            };
            result.Add(new ExportBuiltItem(link.NodeId, link.PublicId, item));
        }
        return result;
    }

    /// <summary>On-disk names from the top-level folder down to the node itself (bounded walk; a broken chain ends the trail).</summary>
    private static List<string> Trail(LinkRow link, Dictionary<long, (long? ParentId, string Name)> folders)
    {
        var names = new List<string> { link.Name };
        var parent = link.ParentId;
        for (var depth = 0; parent is { } id && depth < SeriesInfoResolver.MaxWalkDepth && folders.TryGetValue(id, out var row); depth++)
        {
            names.Add(row.Name);
            parent = row.ParentId;
        }
        names.Reverse();
        return names;
    }

    private static ExportRecordDto Record(MetadataRecordEntity r)
    {
        var status = (MetadataOriginStatus?)r.OriginStatus;
        return new ExportRecordDto
        {
            Provider = r.Provider,
            ExternalId = r.ExternalId,
            SiteUrl = r.SiteUrl,
            Title = r.Title,
            AltTitles = MetadataJson.ReadList<string>(r.AltTitlesJson),
            Type = r.ProviderType,
            OriginStatus = ExportVocabulary.Status(status),
            OriginVolumes = r.OriginVolumes,
            LatestChapter = r.LatestChapter is { } latest && double.IsFinite(latest) && latest >= 0
                ? latest.ToString(CultureInfo.InvariantCulture)
                : null,
            TotalChapters = r.Provider == MetadataProviderAllowlist.MangaUpdates ? MangaUpdatesStatusParser.Parse(r.StatusText).Chapters : null,
            StatusText = r.StatusText,
            LicensedEn = r.LicensedEn,
            TranslationComplete = r.TranslationComplete,
            CompletedInOrigin = status switch
            {
                MetadataOriginStatus.Complete => true,
                MetadataOriginStatus.Ongoing or MetadataOriginStatus.Hiatus or MetadataOriginStatus.Cancelled => false,
                _ => null,
            },
            EnglishPublishers = MetadataJson.ReadList<MetadataJson.Publisher>(r.PublishersJson)
                .Where(p => string.Equals(p.Kind, "english", StringComparison.Ordinal))
                .Select(p => new ExportPublisherDto
                {
                    Name = p.Name,
                    Volumes = p.Volumes,
                    Chapters = p.Chapters,
                    Status = ExportVocabulary.Status(p.StatusValue),
                    Omnibus = p.Omnibus ?? false,
                })
                .ToList(),
            FetchedAt = ExportJson.Truncate(r.FetchedAt),
        };
    }

    /// <summary>Files per duplicate listed in the export (a number stated by more files is rare; the count stays in the report).</summary>
    public const int MaxDuplicateFiles = 20;

    /// <summary>
    /// 1.38.0: the duplicate numbers of a linked series folder with each file's node id and name - the same rules as the Missing report
    /// (<see cref="DuplicateUnits.GroupsIn"/>), from the rows the Completion answer was computed from; null when there are none.
    /// </summary>
    private static List<ExportDuplicateDto>? Duplicates(IReadOnlyList<GroupingRow> rows)
    {
        var groups = DuplicateUnits.GroupsIn(rows);
        if (groups.Count == 0)
            return null;
        return groups.Take(MissingUnits.MaxListed).Select(g => new ExportDuplicateDto
        {
            Kind = g.Unit.Kind == MissingUnitKind.Volume ? "Volume" : "Chapter",
            Number = VolumeGrouping.Canonical(g.Unit.Number),
            Files = g.Rows.Take(MaxDuplicateFiles)
                .Select(r => new ExportDuplicateFileDto { NodeId = r.Id, Name = r.Name, Folder = r.ContainerName })
                .ToList(),
        }).ToList();
    }

    private static ExportCompletionDto Completion(SeriesProgressEntry entry)
    {
        var dto = entry.Dto;
        return new ExportCompletionDto
        {
            Answer = ExportVocabulary.Answer(dto.Answer),
            Reason = ExportVocabulary.Reason(dto.AnswerReason),
            UpgradeAvailable = dto.UpgradeCount > 0,
            UpgradeVolumes = dto.UpgradeVolumes,
            ComputedAt = default,
            BasedOnScanAt = null,
            VolumeTotalOverride = entry.Result.Facts.VolumeOverride,
            Edition = entry.Result.Facts.Edition is { } edition ? DeclaredFactKeys.EditionSlug(edition) : null,
        };
    }

    private static ExportRefreshDto Refresh(MetadataRecordEntity r)
    {
        var interval = RefreshCadence.AgeFor(r.RefreshCadenceDays, r.OriginStatus);
        var fetched = ExportJson.Truncate(r.FetchedAt);
        return new ExportRefreshDto { LastFetchedAt = fetched, NextDueAt = fetched + interval, IntervalDays = (int)interval.TotalDays };
    }

    /// <summary>The official sources stored on the MangaDex record (1.33.0), in their stored order; [] when none (yet).</summary>
    public static IReadOnlyList<ExportOfficialLinkDto> OfficialLinks(string? mangaDexExtraJson) =>
        (CompanionLinkService.MangaDexExtra.Read(mangaDexExtraJson).Links ?? [])
            .Select(l => ExportVocabulary.OfficialLink(l.Key, l.Url))
            .OfType<ExportOfficialLinkDto>()
            .ToList();

    private static int? WholeOrNull(double? value) =>
        value is { } v && double.IsFinite(v) && v >= 0 && v < int.MaxValue ? (int)Math.Floor(v) : null;

    /// <summary>
    /// Stored AniList rows by the MangaUpdates id they were found for (an admin's lookup stores the row without a companion row).
    /// </summary>
    private async Task<Dictionary<string, CompanionRecord>> AniListByMangaUpdatesIdAsync(CancellationToken ct)
    {
        var rows = await db.MetadataRecords.AsNoTracking()
            .Where(r => r.Provider == MetadataProviderAllowlist.AniList && r.CrossIdsJson != null)
            .OrderBy(r => r.Id)
            .Select(r => new { Record = new CompanionRecord(r.Id, r.ExternalId, r.ExtraJson, r.LatestChapter, r.OriginVolumes), r.CrossIdsJson })
            .ToListAsync(ct);
        var result = new Dictionary<string, CompanionRecord>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            try
            {
                if (JsonSerializer.Deserialize<Dictionary<string, string>>(row.CrossIdsJson!)?.GetValueOrDefault(MetadataProviderAllowlist.MangaUpdates) is { } mu)
                    result.TryAdd(mu, row.Record);
            }
            catch (JsonException)
            {
                // A malformed cross reference names nothing.
            }
        }
        return result;
    }
}
