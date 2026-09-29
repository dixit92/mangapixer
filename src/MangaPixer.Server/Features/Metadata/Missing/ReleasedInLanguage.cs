namespace com.lifepixer.mangapixer.Server.Features.Metadata.Missing;

using System.Globalization;
using System.Text.Json;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// What a linked series has released in the PREFERRED language (1.29.0 owner rule: "missing" means released in that language,
/// never an original-language volume that is not translated). <see cref="Volumes"/>: the volume total, known today only for
/// English (MangaUpdates' English publishers); <see cref="Chapters"/>: the chapters the stored volume list names as released
/// in that language (read by the MangaDex companion), null when not read for it.
/// </summary>
public sealed record ReleaseInfo(string Language, int? Volumes, int? EnglishChapters, IReadOnlySet<decimal>? Chapters)
{
    /// <summary>Something is known about what is released (a volume total or the released chapter list).</summary>
    public bool Known => Volumes is not null || EnglishChapters is not null || Chapters is not null;

    /// <summary>The highest whole chapter the released list names, or null.</summary>
    public int? LastChapter => Chapters is { Count: > 0 } c ? (int)decimal.Floor(c.Max()) : null;
}

/// <summary>Reads <see cref="ReleaseInfo"/> from stored rows only (the admin's preferred cover language, the record, the volume map).</summary>
public static class ReleasedInLanguage
{
    public const string DefaultLanguage = "en";

    /// <summary>The preferred language ("Preferred cover language" in Metadata Manager; English when unset).</summary>
    public static async Task<string> PreferredAsync(MangaPixerDbContext db, CancellationToken ct)
    {
        var value = await db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => s.MetadataCoverLanguage).FirstOrDefaultAsync(ct);
        return Normalize(value);
    }

    public static string Normalize(string? language) =>
        string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim().ToLowerInvariant();

    public static bool IsEnglish(string language) => string.Equals(Normalize(language), DefaultLanguage, StringComparison.Ordinal);

    /// <summary>
    /// The release info of one series: English publisher totals only when the language is English; the released chapter list
    /// only when it was read for this language.
    /// </summary>
    public static ReleaseInfo For(string language, string? publishersJson, string? releasedLanguage, string? releasedChaptersJson)
    {
        language = Normalize(language);
        int? volumes = null, chapters = null;
        if (IsEnglish(language))
        {
            var english = MetadataJson.ReadList<MetadataJson.Publisher>(publishersJson)
                .Where(p => string.Equals(p.Kind, "english", StringComparison.Ordinal)).ToList();
            volumes = english.Max(p => p.Volumes) is { } v && v > 0 ? v : null;
            chapters = english.Max(p => p.Chapters) is { } c && c > 0 ? c : null;
        }
        var released = releasedLanguage is not null && string.Equals(Normalize(releasedLanguage), language, StringComparison.Ordinal)
            ? ParseChapters(releasedChaptersJson)
            : null;
        return new ReleaseInfo(language, volumes, chapters, released);
    }

    /// <summary><c>["1","2","4.1"]</c> -> the numbers; null for no list or a malformed one.</summary>
    public static IReadOnlySet<decimal>? ParseChapters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var set = new HashSet<decimal>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var text = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Number => item.GetRawText(),
                    _ => null,
                };
                if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && n >= 0)
                    set.Add(n);
            }
            return set;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
