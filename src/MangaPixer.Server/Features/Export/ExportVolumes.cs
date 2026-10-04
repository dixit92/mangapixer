namespace com.lifepixer.mangapixer.Server.Features.Export;

using System.Globalization;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes.Wikipedia;
using com.lifepixer.mangapixer.Server.Persistence.Entities;

/// <summary>
/// The export's per-volume list of a series (1.33.0): the exact list every reader sees (<see cref="SeriesProgressLoader.ExactList"/> -
/// MangaDex completed by Wikipedia) with the English release date and ISBN of the stored Wikipedia details. Unit numbers stay the
/// stored strings. Pure over stored rows (unit-tested).
/// </summary>
public static partial class ExportVolumes
{
    [GeneratedRegex(@"^\d{4}(-\d{2}(-\d{2})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PartialDatePattern();

    /// <summary>
    /// The volume list, or null when the series has no stored list and no dated volume. <paramref name="today"/> (the rebuild's date,
    /// UTC) decides <c>released</c> against <c>announced</c>.
    /// </summary>
    public static ExportVolumesDto? Project(
        IReadOnlyList<SeriesVolumeMapEntity> maps, IReadOnlyList<WikipediaVolumeService.StoredDetail> details, DateTimeOffset? detailsCheckedAt,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(details);
        // 1.34.0: a near-empty MangaDex map is no list (as in ExactList), so it never credits a volume either.
        var mangadexMap = Metadata.Volumes.VolumeMapService.UsableMangaDexMap(maps);
        var wikipediaMap = maps.FirstOrDefault(m => m.Source == (int)VolumeMapSource.WikipediaList && m.State == (int)VolumeMapState.Ok);
        var fromMangaDex = new Dictionary<decimal, HashSet<string>>();
        foreach (var entry in VolumeMapJson.Read(mangadexMap?.VolumesJson))
        {
            if (VolumeMapJson.Parse(entry.Volume) is { } number)
                fromMangaDex[number] = entry.Chapters.ToHashSet(StringComparer.Ordinal);
        }
        var detailByVolume = new Dictionary<decimal, WikipediaVolumeService.StoredDetail>();
        foreach (var detail in details)
        {
            if (VolumeMapJson.Parse(detail.Volume) is { } number && (Date(detail.Date) is not null || !string.IsNullOrWhiteSpace(detail.Isbn)))
                detailByVolume.TryAdd(number, detail);
        }

        var rows = new SortedDictionary<decimal, ExportVolumeDto>();
        var usedMangaDex = false;
        var usedWikipedia = false;
        foreach (var volume in SeriesProgressLoader.ExactList(maps).Volumes)
        {
            if (VolumeMapJson.Parse(volume.Volume) is not { } number || rows.ContainsKey(number))
                continue;
            var sources = new List<string>();
            if (fromMangaDex.TryGetValue(number, out var mdChapters))
            {
                sources.Add(ExportVocabulary.MangaDex);
                usedMangaDex = true;
            }
            var detail = detailByVolume.GetValueOrDefault(number);
            if (mdChapters is null || volume.Chapters.Any(c => !mdChapters.Contains(c)) || detail is not null)
            {
                sources.Add(ExportVocabulary.Wikipedia);
                usedWikipedia = true;
            }
            rows[number] = Row(volume.Volume, Range(volume.Chapters), detail, sources, today);
        }
        foreach (var (number, detail) in detailByVolume.Where(d => !rows.ContainsKey(d.Key)))
        {
            rows[number] = Row(detail.Volume.Trim(), null, detail, [ExportVocabulary.Wikipedia], today);
            usedWikipedia = true;
        }
        if (rows.Count == 0)
            return null;

        DateTimeOffset? fetchedAt = null;
        if (usedMangaDex && mangadexMap is not null)
            fetchedAt = mangadexMap.FetchedAt;
        if (usedWikipedia && (wikipediaMap?.FetchedAt ?? detailsCheckedAt) is { } wikipediaAt && (fetchedAt is null || wikipediaAt > fetchedAt))
            fetchedAt = wikipediaAt;
        return new ExportVolumesDto
        {
            Source = usedMangaDex && usedWikipedia ? ExportVocabulary.Merged : usedMangaDex ? ExportVocabulary.MangaDex : ExportVocabulary.Wikipedia,
            FetchedAt = fetchedAt,
            Items = rows.Values.ToList(),
        };
    }

    private static ExportVolumeDto Row(
        string volume, ExportChapterRangeDto? chapters, WikipediaVolumeService.StoredDetail? detail, List<string> sources, DateOnly today)
    {
        var date = Date(detail?.Date);
        return new ExportVolumeDto
        {
            Volume = volume,
            Title = null,
            Chapters = chapters,
            EnglishDate = date,
            EnglishDateKind = date is null ? null : IsReleased(date, today) ? ExportVocabulary.Released : ExportVocabulary.Announced,
            Isbn = string.IsNullOrWhiteSpace(detail?.Isbn) ? null : detail.Isbn.Trim(),
            Sources = sources,
        };
    }

    /// <summary>The lowest and highest chapter of a volume (the stored strings), or null without chapters.</summary>
    public static ExportChapterRangeDto? Range(IReadOnlyList<string> chapters)
    {
        var numbered = chapters.Select(c => (Text: c, Number: VolumeMapJson.Parse(c))).Where(c => c.Number is not null).ToList();
        if (numbered.Count == 0)
            return null;
        var from = numbered.MinBy(c => c.Number!.Value);
        var to = numbered.MaxBy(c => c.Number!.Value);
        return new ExportChapterRangeDto { From = from.Text, To = to.Text };
    }

    private static string? Date(string? text)
    {
        var value = text?.Trim();
        return value is not null && PartialDatePattern().IsMatch(value) && LastDay(value) is not null ? value : null;
    }

    /// <summary>
    /// A (partial) date is released when its LAST day is not after <paramref name="today"/>: <c>2024-05</c> counts as released only from
    /// 31 May 2024 (an announced month is not a release). False for a date that is not a calendar date.
    /// </summary>
    public static bool IsReleased(string date, DateOnly today) => LastDay(date) is { } last && last <= today;

    private static DateOnly? LastDay(string date)
    {
        var parts = date.Split('-');
        if (parts.Length is < 1 or > 3 || parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return null;
        var year = int.Parse(parts[0], CultureInfo.InvariantCulture);
        if (year is < 1 or > 9999)
            return null;
        if (parts.Length == 1)
            return new DateOnly(year, 12, 31);
        var month = int.Parse(parts[1], CultureInfo.InvariantCulture);
        if (month is < 1 or > 12)
            return null;
        var days = DateTime.DaysInMonth(year, month);
        var day = parts.Length == 3 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : days;
        return day >= 1 && day <= days ? new DateOnly(year, month, day) : null;
    }
}
