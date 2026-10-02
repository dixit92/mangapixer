namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Gathers a work's local comics signs into <see cref="MatchContext.Comics"/> before any lookup (1.32.0). Local data only -
/// display names, the declaration, stored ComicInfo and page counts - nothing is sent to compute it. One query reads, for the
/// work's archives (<paramref name="archiveIds"/> of <see cref="ApplyAsync"/>): their display names, page counts and parsed
/// ComicInfo <c>Publisher</c> / <c>Imprint</c> / <c>Web</c> / <c>Notes</c> / <c>Manga</c>. How they are combined:
/// <list type="bullet">
/// <item>publisher and imprint: the MAJORITY value (at least half of the archives that name one) - one stray file of a
/// manga volume run tagged by a Western reprint must not route the folder;</item>
/// <item>web links and notes: ANY archive - an id names the record whichever file carries it (taggers often tag only some);</item>
/// <item>ComicInfo <c>Manga</c>: more than half of the archives with ComicInfo say Yes / YesAndRightToLeft;</item>
/// <item>page counts: the median of the analysed archives -> <see cref="ComicsSignal.PageShape"/>.</item>
/// </list>
/// The folder's own name (a <c>Title (YYYY)</c> start year, a <c>[cv-123]</c> tag) is read for folder-level works only; an
/// archive-level work (in a collection folder) is named by its archives.
/// </summary>
public static class ComicsSignalReader
{
    /// <summary>At most this many notes / web-link values are parsed per work (ids sit in the first tagged files).</summary>
    private const int MaxTaggedRows = 50;

    public static async Task<MatchQuery> ApplyAsync(
        MangaPixerDbContext db, MatchQuery query, FolderShape shape, IReadOnlyList<long> archiveIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(archiveIds);
        var input = await ReadAsync(db, query, shape, archiveIds, ct);
        var signal = ComicsSignals.Of(input);
        return signal.Kinds == ComicsSignalKind.None
            ? query
            : query with { Context = query.Context with { Comics = signal } };
    }

    /// <summary>The detector input of one work (one database query). Public for tests.</summary>
    public static async Task<ComicsSignalInput> ReadAsync(
        MangaPixerDbContext db, MatchQuery query, FolderShape shape, IReadOnlyList<long> archiveIds, CancellationToken ct)
    {
        var folderLevel = query.Context.Class is not (WorkClass.CollectionLeaf or WorkClass.ArtistCollection or WorkClass.Mixed);
        var rows = archiveIds.Count == 0
            ? []
            : await (
                from n in db.CatalogNodes.AsNoTracking()
                where archiveIds.Contains(n.Id)
                join e in db.EmbeddedMetadata.AsNoTracking().Where(e => e.State == 1) on n.Id equals e.NodeId into ci
                from e in ci.DefaultIfEmpty()
                select new Row(
                    n.DisplayName,
                    n.ArchiveItem != null ? n.ArchiveItem.PageCount : null,
                    e != null,
                    e != null ? e.Publisher : null,
                    e != null ? e.Imprint : null,
                    e != null ? e.WebUrlsJson : null,
                    e != null ? e.Notes : null,
                    e != null ? e.MangaDirection : null))
                .ToListAsync(ct);

        var names = rows.Count > 0 ? rows.Select(r => r.Name).ToList() : shape.ArchiveNames;
        var tagged = rows.Where(r => r.HasComicInfo).ToList();
        var webUrls = tagged.Where(r => r.WebUrlsJson is not null).Take(MaxTaggedRows)
            .SelectMany(r => MetadataJson.ReadList<string>(r.WebUrlsJson)).ToList();
        var notes = tagged.Where(r => !string.IsNullOrWhiteSpace(r.Notes)).Take(MaxTaggedRows).Select(r => r.Notes!).ToList();
        var saysManga = tagged.Count > 0 && tagged.Count(r => r.MangaDirection is 1 or 2) * 2 > tagged.Count;

        return new ComicsSignalInput(
            query.Context.DeclaredType,
            shape.CategoryHint,
            names,
            Majority(tagged.Select(r => r.Publisher)),
            webUrls,
            notes.Count > 0 ? string.Join('\n', notes) : null,
            ComicsSignals.MedianOf(rows.Select(r => r.PageCount)),
            folderLevel ? shape.DisplayName : null,
            Majority(tagged.Select(r => r.Imprint)),
            saysManga);
    }

    /// <summary>The value at least half of the non-empty values share (case-insensitive), or null.</summary>
    private static string? Majority(IEnumerable<string?> values)
    {
        var named = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        var top = named.GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return top is not null && top.Count() * 2 >= named.Count ? top.Key : null;
    }

    private sealed record Row(
        string Name, int? PageCount, bool HasComicInfo, string? Publisher, string? Imprint, string? WebUrlsJson, string? Notes,
        int? MangaDirection);
}
