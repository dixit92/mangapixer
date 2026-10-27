namespace com.lifepixer.mangapixer.Core.Metadata.Facets;

using System.Globalization;
using System.Text;
using com.lifepixer.mangapixer.Core.Metadata;

// 1.45.0 lane F: the inputs of the tag index as the pure Core code sees them (D1 "Information Panel and Faceted Tags" 2.1-2.4). The server
// loads them from the stored sources (records, ComicInfo, declared facts, provider authors, links, the tree) - no network, no path, no IO
// here. Names and titles are data: shown in the UI, never logged.

/// <summary>
/// A person credit as its source wrote it: a name, the source's role word (record <c>author</c> / <c>artist</c> / <c>other</c>; ComicInfo
/// <c>writer</c>, <c>penciller</c> ...; declared <c>author</c> / <c>writer</c> / <c>artist</c> or none) and the provider reference of the
/// person when the source names one (<c>mangaupdates:&lt;author id&gt;</c> - a MangaUpdates record credit's <c>author_id</c>, or the author an
/// admin picked for a declared creator).
/// </summary>
public sealed record FacetCredit(string Name, string? Role, string? ProviderRef = null);

/// <summary>A publisher credit of a record: the name and its kind (<c>original</c> / <c>english</c> / <c>other</c>).</summary>
public sealed record FacetPublisher(string Name, string Kind);

/// <summary>1.49.0 (lane T): a stored provider category (a crowd tag) and its net vote count (one number; no plus / minus split is stored).</summary>
public sealed record FacetCategory(string Name, int Votes);

/// <summary>A stored web record, as far as the index reads it (1.49.0: with its categories, strongest first).</summary>
public sealed record FacetRecord(
    long Id,
    string Title,
    MetadataOrigin? Origin,
    MetadataFormat? Format,
    bool? Webtoon,
    int? StartYear,
    IReadOnlyList<FacetCredit> Creators,
    IReadOnlyList<string> Genres,
    IReadOnlyList<FacetPublisher> Publishers,
    IReadOnlyList<string> Magazines,
    IReadOnlyList<FacetCategory>? Categories = null);

/// <summary>The CURRENT parsed ComicInfo of one archive (a row of the archive's content version in state parsed; 1.49.0: with its Tags).</summary>
public sealed record FacetComicInfo(
    long ArchiveId,
    string? Series,
    IReadOnlyList<FacetCredit> Creators,
    IReadOnlyList<string> Genres,
    string? Publisher,
    string? Imprint,
    int? Year,
    IReadOnlyList<string>? Tags = null);

/// <summary>One declared fact row (<c>declared_facts</c>): on a folder (<see cref="NodeId"/>) or the library (null).</summary>
public sealed record FacetDeclaredFact(long? NodeId, string Key, string Value, string? Role, int Position, string? ExternalRef = null);

/// <summary>One live catalog node of a library: the caller lists children in catalog (sort key) order.</summary>
public sealed record FacetNode(long Id, long? ParentId, bool IsFolder, string Name);

/// <summary>A node's own link row (<c>node_series_links</c>).</summary>
public sealed record FacetLink(SeriesLinkState State, long? RecordId);

/// <summary>Text rules of the index (pure): role words, the ComicInfo series form, value keys, scripts and provider references.</summary>
public static class FacetText
{
    /// <summary>The longest value key the index writes (the column holds 300).</summary>
    public const int MaxValueKeyLength = 280;

    /// <summary>The longest display text (the column holds 256).</summary>
    public const int MaxDisplayLength = 256;

    /// <summary>
    /// The indexed roles of a credit's role word (owner Q11): writer = MangaUpdates <c>author</c> / ComicInfo <c>writer</c>; artist =
    /// <c>artist</c> / ComicInfo <c>penciller</c> / <c>inker</c> / <c>coverartist</c>; a declared creator without a role is indexed with no
    /// role. Null = not indexed in v1 (colorist, letterer, editor, translator, the provider's <c>other</c>).
    /// </summary>
    public static FacetRoles? RolesOf(string? role, bool declared = false)
    {
        if (string.IsNullOrWhiteSpace(role))
            return declared ? FacetRoles.None : null;
        return role.Trim().ToLowerInvariant() switch
        {
            "author" or "writer" or "story" => FacetRoles.Writer,
            "artist" or "penciller" or "inker" or "coverartist" or "art" => FacetRoles.Artist,
            _ => null,
        };
    }

    /// <summary>
    /// The series form of a ComicInfo <c>Series</c> (the <c>ci:</c> work key's series part): compatibility-normalized, case-folded, inner
    /// white space collapsed, at most <see cref="FacetWorkKey.MaxSeriesFormLength"/> characters; null when nothing is left.
    /// </summary>
    public static string? SeriesForm(string? series)
    {
        var folded = Fold(series, FacetWorkKey.MaxSeriesFormLength);
        return folded is not null && FacetWorkKey.IsValidSeriesForm(folded) ? folded : null;
    }

    /// <summary>The case-folded key text of a value (genre, publisher, magazine): NFKC, lower case, one space between words.</summary>
    public static string? Fold(string? text, int max = MaxValueKeyLength - 2)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(normalized.Length);
        var space = false;
        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        if (sb.Length == 0)
            return null;
        var result = sb.ToString();
        if (result.Length > max)
            result = result[..max].TrimEnd();
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// 1.49.0 (lane T): the key text of a tag (bucket <see cref="FacetBucket.Tags"/>, after the <c>tg:</c> prefix): <see cref="Fold"/> with
    /// <c>_</c> and <c>-</c> read as spaces, so "Time Travel", "time-travel" and "TIME_TRAVEL" are one value; null when nothing is left.
    /// </summary>
    public static string? TagKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var spaced = text.Replace('_', ' ').Replace('-', ' ').Replace('\u2010', ' ').Replace('\u2011', ' ');
        return Fold(spaced);
    }

    /// <summary>Display text: trimmed, inner white space collapsed, at most <see cref="MaxDisplayLength"/> characters; null when blank.</summary>
    public static string? Display(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (collapsed.Any(char.IsControl))
            collapsed = new string(collapsed.Where(c => !char.IsControl(c)).ToArray());
        if (collapsed.Length > MaxDisplayLength)
            collapsed = collapsed[..MaxDisplayLength].TrimEnd();
        return collapsed.Length == 0 ? null : collapsed;
    }

    /// <summary>A provider reference <c>&lt;provider&gt;:&lt;id&gt;</c> (lower-case provider slug, trimmed id); null when either is blank.</summary>
    public static string? ProviderRef(string? provider, string? externalId)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(externalId))
            return null;
        var p = provider.Trim().ToLowerInvariant();
        var id = externalId.Trim();
        return p.Contains(':', StringComparison.Ordinal) || id.Length > 64 || id.Any(char.IsWhiteSpace) ? null : p + ":" + id;
    }

    /// <summary>The parts of a provider reference, or null for anything that is not one.</summary>
    public static (string Provider, string ExternalId)? ParseProviderRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        var colon = reference.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == reference.Length - 1)
            return null;
        var provider = reference[..colon];
        var id = reference[(colon + 1)..];
        return ProviderRef(provider, id) is { } canonical && string.Equals(canonical, reference, StringComparison.Ordinal)
            ? (provider, id)
            : null;
    }

    /// <summary>The script a spelling is written in: Hangul, else CJK (Han / kana), else Latin when every letter is Latin, else other.</summary>
    public static CreatorNameScript Script(string name)
    {
        var hasLetter = false;
        var allLatin = true;
        var cjk = false;
        foreach (var rune in name.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune))
                continue;
            hasLetter = true;
            var v = rune.Value;
            if (v is (>= 0xAC00 and <= 0xD7AF) or (>= 0x1100 and <= 0x11FF) or (>= 0x3130 and <= 0x318F))
                return CreatorNameScript.Hangul;
            if (v is (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF)
                or (>= 0x20000 and <= 0x2FA1F) or (>= 0xFF66 and <= 0xFF9F))
                cjk = true;
            else if (v > 0x024F && v is not (>= 0x1E00 and <= 0x1EFF))
                allLatin = false;
        }
        if (cjk)
            return CreatorNameScript.Cjk;
        return hasLetter && allLatin ? CreatorNameScript.Latin : CreatorNameScript.Other;
    }

    /// <summary>A plausible year (1000-9999) or null.</summary>
    public static int? Year(int? year) => year is >= 1000 and <= 9999 ? year : null;

    internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
