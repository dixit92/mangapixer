namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Reads the <see cref="ComicsEvidence"/> of a comics work (1.32.0, lane B) from local data: the <c>(YYYY)</c> of the work's own name,
/// the ComicInfo <c>LanguageISO</c> and <c>Publisher</c> most of its files agree on (the preferred language when ComicInfo names
/// none), and its shape from the files' format words and median page count. Three small queries; nothing is sent - only the start
/// year may leave the server, as the Grand Comics Database search's year. Logs nothing.
/// </summary>
public static class ComicsEvidenceReader
{
    /// <summary>A ComicInfo value counts when at least this share of the work's parsed files agree on it (the resolver's majority rule).</summary>
    public const double MajorityShare = SeriesInfoResolver.MajorityShare;

    public static async Task<ComicsEvidence> ReadAsync(
        MangaPixerDbContext db, string? workName, IReadOnlyList<string> archiveNames, IReadOnlyList<long> archiveIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(archiveNames);
        ArgumentNullException.ThrowIfNull(archiveIds);
        var startYear = TitleNormalizer.Normalize(workName ?? string.Empty).YearHint;

        string? language = null, publisher = null;
        int? median = null;
        if (archiveIds.Count > 0)
        {
            var rows = await db.EmbeddedMetadata.AsNoTracking()
                .Where(e => archiveIds.Contains(e.NodeId) && e.State == 1)
                .Select(e => new { e.LanguageIso, e.Publisher })
                .ToListAsync(ct);
            language = Majority(rows.Select(r => ComicsEvidenceRules.LanguageCode(r.LanguageIso)), rows.Count);
            publisher = Majority(rows.Select(r => string.IsNullOrWhiteSpace(r.Publisher) ? null : r.Publisher.Trim()), rows.Count);
            var pages = await db.ArchiveItems.AsNoTracking()
                .Where(a => archiveIds.Contains(a.NodeId) && a.PageCount != null)
                .Select(a => a.PageCount!.Value)
                .ToListAsync(ct);
            median = ComicsEvidenceRules.Median(pages);
        }
        language ??= ComicsEvidenceRules.LanguageCode(await Missing.ReleasedInLanguage.PreferredAsync(db, ct));
        return new ComicsEvidence(startYear, language, ComicsEvidenceRules.ShapeOf(archiveNames, median), publisher);
    }

    private static string? Majority(IEnumerable<string?> values, int total)
    {
        if (total == 0)
            return null;
        var top = values.Where(v => v is not null)
            .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return top is not null && top.Count() >= total * MajorityShare ? top.Key : null;
    }
}
