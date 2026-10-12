namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;

using System.Globalization;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

/// <summary>
/// MangaDex as a SERIES RECORD (1.42.0, owner-approved): the record of a work MangaUpdates does not list. An <see cref="IMetadataProvider"/>
/// over the companion's HTTP code (<see cref="IMangaDexProvider"/>), so Identify, Link, Review accept, Refresh and the background refresh work
/// through the provider-agnostic paths (as the Grand Comics Database does). What may reach MangaDex is decided where it is called - each
/// pinned by a test: Identify only when the admin picks MangaDex (the text the admin confirms, or a pasted title address - then only its
/// id); automatic matching only in its MangaDex step (<c>AutoMatchLookup</c>: no candidate of any site reached the review floor, never for
/// comics or inside a "Collection about" folder, the setting on); refresh only by record id. A search answers with whole records (no GET).
/// Doujinshi are dropped HERE, on the server, when the search hides them - nothing extra is sent.
/// </summary>
public sealed class MangaDexSeriesProvider : IMetadataProvider
{
    private readonly IMangaDexProvider _mangaDex;

    public MangaDexSeriesProvider(IMangaDexProvider mangaDex)
    {
        _mangaDex = mangaDex;
    }

    public string Id => MangaDexProvider.ProviderId;

    public string DisplayName => MangaDexProvider.ProviderName;

    public MetadataProviderKind Kind => MetadataProviderKind.Online;

    public MetadataCapabilities Capabilities => MetadataCapabilities.SeriesSearch | MetadataCapabilities.SeriesGet | MetadataCapabilities.Covers;

    public bool TryParseReference(string input, out ProviderRef reference)
    {
        if (MangaDexProvider.TryParseReference(input, out var mangaId))
        {
            reference = new ProviderRef(Id, mangaId);
            return true;
        }
        reference = new ProviderRef(Id, string.Empty);
        return false;
    }

    public async Task<ProviderSearchPage> SearchSeriesAsync(ProviderSearchQuery query, CancellationToken ct)
    {
        var offset = (Math.Max(query.Page, 1) - 1) * MangaDexProvider.SearchLimit;
        var results = await _mangaDex.SearchRecordsAsync(query.Text, offset, ct);
        var hideDoujinshi = query.HideDoujinshiAndNovels && !query.AllowDoujinshi;
        var hits = results
            .Where(m => !(hideDoujinshi && m.Doujinshi))
            .Select(m =>
            {
                var record = MangaDexMapping.ToRecord(m);
                return new ProviderSearchHit
                {
                    ExternalId = record.ExternalId,
                    Title = record.Title,
                    Year = record.StartYear,
                    ImageRemoteUrl = m.MainCoverFile is { } file ? MangaDexProvider.ThumbnailUrl(m.Id, file) : null,
                    Record = record,
                };
            })
            .ToList();
        // MangaDex reports no usable total here: a full page may have a next one.
        var total = offset + results.Count + (results.Count >= MangaDexProvider.SearchLimit ? 1 : 0);
        return new ProviderSearchPage(hits, Math.Max(total, hits.Count));
    }

    public async Task<ProviderSeriesRecord?> GetSeriesAsync(string externalId, CancellationToken ct) =>
        await _mangaDex.GetRecordAsync(externalId, ct) is { } manga ? MangaDexMapping.ToRecord(manga) : null;
}

/// <summary>
/// The ONE mapping of a MangaDex record onto <c>metadata_records</c> (1.42.0), shared by the series record
/// (<see cref="MangaDexSeriesProvider"/>) and the companion (<see cref="CompanionLinkService.UpsertRecordAsync"/>), so neither writer wipes
/// the fields the other wrote. Pure.
/// </summary>
public static class MangaDexMapping
{
    /// <summary>The record as a provider record (the series-record path).</summary>
    public static ProviderSeriesRecord ToRecord(MangaDexManga m, string? companionOfMangaUpdatesId = null)
    {
        var status = StatusOf(m.Status);
        return new ProviderSeriesRecord
        {
            Provider = MangaDexProvider.ProviderId,
            ExternalId = m.Id,
            SourceKind = MetadataSourceKind.OnlineApi,
            Title = m.Title,
            AltTitles = m.AltTitles,
            Description = m.Description,
            Origin = OriginOf(m.OriginalLanguage),
            Format = m.Doujinshi ? MetadataFormat.Doujinshi : MetadataFormat.Comic,
            Webtoon = m.LongStrip ? true : null,
            ProviderType = null, // MangaDex has no type word (manga / manhwa ...): the origin says it.
            StartYear = m.Year,
            OriginStatus = status,
            OriginVolumes = VolumesOf(m.LastVolume),
            LatestChapter = null, // The chapters released in the preferred language set it (VolumeMapService); a GET never carries it.
            TotalChapters = status == MetadataOriginStatus.Complete ? ChaptersOf(m.LastChapter) : null,
            Creators = m.Creators.Select(c => new MetadataJson.Creator(c.Name, c.Role)).ToList(),
            Genres = m.Genres,
            Relations = m.Relations.Select(r => new MetadataJson.RelatedRecord(r.MangaId, RelationOf(r.Relation))).ToList(),
            CrossIds = CrossIdsOf(m, companionOfMangaUpdatesId),
            SiteUrl = MangaDexProvider.SiteUrl(m.Id),
            ImageRemoteUrl = m.MainCoverFile is { } file ? MangaDexProvider.CoverImageUrl(m.Id, file) : null,
            ExtraJson = ExtraOf(m),
        };
    }

    /// <summary>
    /// The cross ids: the MangaUpdates series MangaDex itself links (<c>links.mu</c>, decoded as the cross-link rule does) - or, for a
    /// companion, the series it is the companion of - and the AniList id.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CrossIdsOf(MangaDexManga m, string? companionOfMangaUpdatesId = null)
    {
        var cross = new Dictionary<string, string>(StringComparer.Ordinal);
        if ((companionOfMangaUpdatesId ?? MangaDexCrossLink.MangaUpdatesIdOf(m.MangaUpdatesLink)) is { } mu)
            cross[MetadataProviderAllowlist.MangaUpdates] = mu;
        if (m.AniListId is { } al)
            cross[MetadataProviderAllowlist.AniList] = al;
        return cross;
    }

    /// <summary>What the record row keeps in <c>ExtraJson</c>: the original language, the main cover, the official links, (1.42.0) the last volume / chapter.</summary>
    public static string ExtraOf(MangaDexManga m) =>
        JsonSerializer.Serialize(new CompanionLinkService.MangaDexExtra(m.OriginalLanguage, m.MainCoverId, m.MainCoverFile)
        {
            Links = m.OfficialLinks.Select(l => new CompanionLinkService.MangaDexExtraLink(l.Key, l.Url)).ToList(),
            LastVolume = m.LastVolume,
            LastChapter = m.LastChapter,
        });

    public static MetadataOriginStatus? StatusOf(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "ongoing" => MetadataOriginStatus.Ongoing,
        "completed" => MetadataOriginStatus.Complete,
        "hiatus" => MetadataOriginStatus.Hiatus,
        "cancelled" => MetadataOriginStatus.Cancelled,
        _ => null,
    };

    public static MetadataOrigin? OriginOf(string? language) => language switch
    {
        null => null,
        "ja" => MetadataOrigin.Japan,
        "ko" => MetadataOrigin.Korea,
        "zh" or "zh-hk" or "zh-ro" => MetadataOrigin.ChinaTaiwan,
        "en" => MetadataOrigin.EnglishOriginal,
        "fr" => MetadataOrigin.French,
        "es" or "es-la" => MetadataOrigin.Spanish,
        "de" => MetadataOrigin.German,
        "it" => MetadataOrigin.Italian,
        "nl" => MetadataOrigin.Dutch,
        _ => MetadataOrigin.Other,
    };

    /// <summary>MangaDex's <c>lastVolume</c> as a volume total (a positive whole number), else null.</summary>
    public static int? VolumesOf(string? lastVolume) =>
        int.TryParse(lastVolume, NumberStyles.None, CultureInfo.InvariantCulture, out var last) && last is > 0 and < 10_000 ? last : null;

    /// <summary>MangaDex's <c>lastChapter</c> (the final chapter of a finished series) as a whole chapter total, else null.</summary>
    public static int? ChaptersOf(string? lastChapter) =>
        double.TryParse(lastChapter, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var last) && last is > 0 and < 100_000
            ? (int)Math.Floor(last)
            : null;

    /// <summary>MangaDex's relation word in the vocabulary the series families read (<c>spin_off</c> -> <c>spin-off</c> ...).</summary>
    public static string RelationOf(string relation) => relation.Trim().ToLowerInvariant() switch
    {
        "main_story" => "main story",
        "side_story" => "side story",
        "spin_off" => "spin-off",
        "adapted_from" => "adapted from",
        "alternate_story" => "alternate story",
        "alternate_version" or "colored" or "monochrome" => "alternate version",
        var other => other.Replace('_', ' '),
    };
}
