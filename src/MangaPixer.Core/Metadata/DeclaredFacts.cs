namespace com.lifepixer.mangapixer.Core.Metadata;

using System.Globalization;
using System.Text;

// Declared facts (1.28.0): facts an ADMIN states about a folder or a whole
// library - the type / format of the works below it and their creators -
// inherited by everything below, the nearest declaration winning per key.
// They are explicit settings, never inferred from library or folder names.
// Stored generically (key, value, optional role) in `declared_facts`, so later
// keys (genres, custom tags) need no migration; v1 exposes type + creators.

/// <summary>The declared type / format of the works below a folder or library.</summary>
public enum DeclaredType
{
    Manga = 0,
    Manhwa = 1,
    Manhua = 2,
    Webtoon = 3,
    Comic = 4,
    GraphicNovel = 5,
    Novel = 6,
}

/// <summary>What a declared type says about one candidate record (<see cref="DeclaredFactsComparer.TypeSignal"/>).</summary>
public enum DeclaredTypeSignal
{
    /// <summary>Nothing: no origin / format to compare, or an origin the type does not name.</summary>
    None = 0,

    /// <summary>The record's origin is the one the type implies.</summary>
    Agree = 1,

    /// <summary>The record contradicts the type (<see cref="DeclaredFactsComparer.TypeConflicts"/>).</summary>
    Mismatch = 2,
}

/// <summary>Where an effective declared fact comes from, relative to the node asked about.</summary>
public enum DeclaredFactSource
{
    /// <summary>Declared on the node itself.</summary>
    Own = 0,

    /// <summary>Declared on an ancestor folder.</summary>
    Inherited = 1,

    /// <summary>Declared on the library.</summary>
    Library = 2,
}

/// <summary>A declared creator: a name and an optional role (<see cref="DeclaredFactKeys.CreatorRoles"/>).</summary>
public sealed record DeclaredCreator(string Name, string? Role);

/// <summary>
/// The effective declared facts of one folder (nearest wins per key): <paramref name="Type"/> is the stored
/// slug (<see cref="DeclaredFactKeys.TypeSlug"/>, e.g. <c>manhwa</c>) or null; <paramref name="Creators"/> is the
/// nearest non-empty creator list (a closer list replaces a farther one - lists are never merged).
/// The sources say where each key came from (null when the key is not declared anywhere above).
/// </summary>
public sealed record DeclaredFacts(
    string? Type,
    IReadOnlyList<DeclaredCreator> Creators,
    DeclaredFactSource? TypeSource = null,
    DeclaredFactSource? CreatorsSource = null)
{
    public static readonly DeclaredFacts Empty = new(null, []);

    /// <summary>The declared type as the enum, or null (also for a slug this version does not know).</summary>
    public DeclaredType? TypeValue => DeclaredFactKeys.ParseType(Type);

    public bool IsEmpty => Type is null && Creators.Count == 0;
}

/// <summary>Keys, value vocabularies and limits of the <c>declared_facts</c> table.</summary>
public static class DeclaredFactKeys
{
    /// <summary>Single-valued: one row per scope, value = a type slug.</summary>
    public const string Type = "type";

    /// <summary>Multi-valued: one row per creator, value = the name, role optional.</summary>
    public const string Creator = "creator";

    public const int MaxKeyLength = 32;
    public const int MaxValueLength = 200;
    public const int MaxRoleLength = 32;
    public const int MaxCreators = 20;

    /// <summary>Roles an admin may give a declared creator (the series-info role vocabulary's main roles).</summary>
    public static readonly IReadOnlyList<string> CreatorRoles = ["author", "writer", "artist"];

    private static readonly Dictionary<DeclaredType, string> Slugs = new()
    {
        [DeclaredType.Manga] = "manga",
        [DeclaredType.Manhwa] = "manhwa",
        [DeclaredType.Manhua] = "manhua",
        [DeclaredType.Webtoon] = "webtoon",
        [DeclaredType.Comic] = "comic",
        [DeclaredType.GraphicNovel] = "graphic-novel",
        [DeclaredType.Novel] = "novel",
    };

    /// <summary>The stored value of a type (stable lower-case slug; never the enum's int).</summary>
    public static string TypeSlug(DeclaredType type) => Slugs[type];

    /// <summary>
    /// The type with its country of origin, as the web client shows it (1.30.0, owner): <c>Manga (Japan)</c>, <c>Manhwa (Korea)</c>,
    /// <c>Manhua (China)</c>, <c>Webtoon (any country)</c>, <c>Comic (Western)</c>, <c>Graphic novel (Western)</c>, <c>Novel (any country)</c>.
    /// </summary>
    public static string TypeLabel(DeclaredType type) => type switch
    {
        DeclaredType.Manga => "Manga (Japan)",
        DeclaredType.Manhwa => "Manhwa (Korea)",
        DeclaredType.Manhua => "Manhua (China)",
        DeclaredType.Webtoon => "Webtoon (any country)",
        DeclaredType.Comic => "Comic (Western)",
        DeclaredType.GraphicNovel => "Graphic novel (Western)",
        DeclaredType.Novel => "Novel (any country)",
        _ => type.ToString(),
    };

    public static DeclaredType? ParseType(string? slug)
    {
        if (slug is null)
            return null;
        foreach (var (type, value) in Slugs)
            if (string.Equals(value, slug, StringComparison.Ordinal))
                return type;
        return null;
    }

    /// <summary>Trims and collapses inner whitespace; null for blank, too long or control characters.</summary>
    public static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var collapsed = string.Join(' ', name.Normalize(NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (collapsed.Length == 0 || collapsed.Length > MaxValueLength || collapsed.Any(char.IsControl))
            return null;
        return collapsed;
    }
}

/// <summary>
/// Compares a declaration with a linked record (owner, 2026-09-27: when they disagree the Info panel shows BOTH
/// with a clear conflict indication). Pure; a declaration never changes or vetoes the record here.
/// </summary>
public static class DeclaredFactsComparer
{
    /// <summary>
    /// Whether a declared type contradicts what the record says about its origin and format. Unknown record
    /// values never conflict. Manga / manhwa / manhua expect Japan / Korea / China-Taiwan; comic and graphic
    /// novel contradict those three origins; novel contradicts a comic-like format and every other type
    /// contradicts a novel. Webtoon only contradicts a novel (webtoons come from every origin).
    /// </summary>
    public static bool TypeConflicts(DeclaredType declared, MetadataOrigin? origin, MetadataFormat? format)
    {
        if (declared == DeclaredType.Novel)
            return format is MetadataFormat.Comic or MetadataFormat.Doujinshi or MetadataFormat.Artbook;
        if (format == MetadataFormat.Novel)
            return true;
        if (origin is not { } o)
            return false;
        return declared switch
        {
            DeclaredType.Manga => o != MetadataOrigin.Japan,
            DeclaredType.Manhwa => o != MetadataOrigin.Korea,
            DeclaredType.Manhua => o != MetadataOrigin.ChinaTaiwan,
            DeclaredType.Comic or DeclaredType.GraphicNovel =>
                o is MetadataOrigin.Japan or MetadataOrigin.Korea or MetadataOrigin.ChinaTaiwan,
            _ => false,
        };
    }

    /// <summary>
    /// What a declared type says about a candidate record as MATCHING evidence (1.30.0, owner: a strong hint, never a filter
    /// or a veto). <see cref="DeclaredTypeSignal.Mismatch"/> is exactly <see cref="TypeConflicts"/> (the Info panel's conflict
    /// badge); <see cref="DeclaredTypeSignal.Agree"/> needs the implied origin: manga -> Japan, manhwa -> Korea, manhua ->
    /// China / Taiwan, webtoon -> a webtoon record or a Korean / Chinese one, comic and graphic novel -> a Western origin
    /// (English-original, French, Spanish, German, Nordic). Other origins (Thai, Filipino ...) and unknown values say nothing.
    /// A declared novel gives no matching evidence: automatic searches leave novels out, so every candidate would get the
    /// same signal.
    /// </summary>
    public static DeclaredTypeSignal TypeSignal(DeclaredType declared, MetadataOrigin? origin, MetadataFormat? format, bool? webtoon)
    {
        if (declared == DeclaredType.Novel)
            return DeclaredTypeSignal.None;
        if (TypeConflicts(declared, origin, format))
            return DeclaredTypeSignal.Mismatch;
        var agrees = declared switch
        {
            DeclaredType.Manga => origin == MetadataOrigin.Japan,
            DeclaredType.Manhwa => origin == MetadataOrigin.Korea,
            DeclaredType.Manhua => origin == MetadataOrigin.ChinaTaiwan,
            DeclaredType.Webtoon => webtoon == true || origin is MetadataOrigin.Korea or MetadataOrigin.ChinaTaiwan,
            DeclaredType.Comic or DeclaredType.GraphicNovel => origin is MetadataOrigin.EnglishOriginal or MetadataOrigin.French
                or MetadataOrigin.Spanish or MetadataOrigin.German or MetadataOrigin.Nordic or MetadataOrigin.Italian
                or MetadataOrigin.Dutch,
            _ => false,
        };
        return agrees ? DeclaredTypeSignal.Agree : DeclaredTypeSignal.None;
    }

    /// <summary>
    /// True when both sides name creators and not one declared name matches a record name. Names match on
    /// their folded word set, so word order, case, accents and punctuation do not matter
    /// (<c>ODA Eiichiro</c> = <c>Eiichiro Oda</c>); a spelling variant (<c>Eiichirou</c>) is a different name.
    /// </summary>
    public static bool CreatorsConflict(IReadOnlyCollection<string> declared, IReadOnlyCollection<string> record)
    {
        if (declared.Count == 0 || record.Count == 0)
            return false;
        var keys = record.Select(NameKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        return keys.Count > 0 && !declared.Select(NameKey).Any(keys.Contains);
    }

    /// <summary>Folded, order-free key of a person's name: lower-case words without accents or punctuation, sorted.</summary>
    public static string NameKey(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(words, StringComparer.Ordinal);
        return string.Join(' ', words);
    }
}
