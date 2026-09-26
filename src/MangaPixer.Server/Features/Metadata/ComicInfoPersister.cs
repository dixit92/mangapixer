namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Upserts an archive's <c>embedded_metadata</c> row from a worker
/// <see cref="ComicInfoOutcome"/> (1.24.0), stamped with the content version it
/// was read from. Every outcome is stored (absent, malformed, ...), so an archive
/// is read once per content version. Tracks the change on the given context
/// WITHOUT saving: the analysis path saves it together with the pages, the
/// backfill saves per item.
/// </summary>
public static class ComicInfoPersister
{
    public static async Task StageAsync(
        MangaPixerDbContext db,
        long nodeId,
        long contentVersion,
        ComicInfoOutcome outcome,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var row = await db.EmbeddedMetadata.FirstOrDefaultAsync(e => e.NodeId == nodeId, ct);
        if (row is null)
        {
            row = new EmbeddedMetadataEntity { NodeId = nodeId };
            db.EmbeddedMetadata.Add(row);
        }

        Apply(row, contentVersion, outcome, now);
    }

    /// <summary>Overwrites every field of <paramref name="row"/> from the outcome.</summary>
    public static void Apply(EmbeddedMetadataEntity row, long contentVersion, ComicInfoOutcome outcome, DateTimeOffset now)
    {
        var state = ComicInfoStatus.ToState(outcome.Status);
        var p = state == 1 ? outcome.Payload : null;

        row.Schema = 0;
        row.ContentVersion = contentVersion;
        row.State = p is null && state == 1 ? 2 : state; // "parsed" without a payload is malformed
        row.ReadAt = now;

        row.Series = Cap(p?.Series, 512);
        row.Title = Cap(p?.Title, 512);
        row.AlternateSeries = Cap(p?.AlternateSeries, 512);
        row.SeriesGroup = Cap(p?.SeriesGroup, 512);
        row.StoryArc = Cap(p?.StoryArc, 512);
        row.Number = Cap(p?.Number, 32);
        row.Format = Cap(p?.Format, 32);
        row.AgeRating = Cap(p?.AgeRating, 32);
        row.LanguageIso = Cap(p?.LanguageIso, 32);
        row.Volume = p?.Volume;
        row.Count = p?.Count;
        row.Year = p?.Year;
        row.Month = p?.Month;
        row.Summary = Cap(p?.Summary, ComicInfoPayload.MaxSummaryText);
        row.CreatorsJson = MetadataJson.WriteList(p?.Creators
            .Take(ComicInfoPayload.MaxListItems)
            .Select(c => new MetadataJson.Creator(Cap(c.Name, 256) ?? string.Empty, Cap(c.Role, 32) ?? "other"))
            .Where(c => c.Name.Length > 0)
            .ToList());
        row.Publisher = Cap(p?.Publisher, 256);
        row.Imprint = Cap(p?.Imprint, 256);
        row.GenresJson = MetadataJson.WriteList(CapList(p?.Genres));
        row.TagsJson = MetadataJson.WriteList(CapList(p?.Tags));
        row.WebUrlsJson = MetadataJson.WriteList(p?.WebUrls
            .Where(u => u.Length <= 512 && (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            .Take(ComicInfoPayload.MaxWebUrls)
            .ToList());
        row.MangaDirection = p?.MangaDirection is >= 0 and <= 2 ? p.MangaDirection : null;
        row.Gtin = Cap(p?.Gtin, 32);
        row.Notes = Cap(p?.Notes, 2048);
    }

    // Defence in depth: the worker already caps every value; the server re-caps
    // to the column sizes so a misbehaving worker cannot overflow a column.
    private static string? Cap(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    private static List<string>? CapList(IReadOnlyList<string>? items) =>
        items?.Select(i => Cap(i, 256)).Where(i => i is not null).Select(i => i!)
            .Take(ComicInfoPayload.MaxListItems).ToList();
}
