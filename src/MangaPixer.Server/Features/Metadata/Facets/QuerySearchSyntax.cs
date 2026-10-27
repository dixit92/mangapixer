namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using System.Globalization;
using System.Text;
using com.lifepixer.mangapixer.Core.Metadata.Facets;

/// <summary>The bucket names of the search syntax (<c>f=creator:fv_..</c>, <c>creator:"..."</c>); lower-case, singular.</summary>
public static class FacetBucketTokens
{
    private static readonly Dictionary<FacetBucket, string> Tokens = new()
    {
        [FacetBucket.Creators] = "creator",
        [FacetBucket.Series] = "series",
        [FacetBucket.Magazine] = "magazine",
        [FacetBucket.Publisher] = "publisher",
        [FacetBucket.Year] = "year",
        [FacetBucket.Genres] = "genre",
        [FacetBucket.Type] = "type",
        [FacetBucket.About] = "about",
        [FacetBucket.Custom] = "custom",
        [FacetBucket.Tags] = "tag",
    };

    private static readonly Dictionary<string, FacetBucket> ByName = BuildNames();

    /// <summary>
    /// The buckets typed field syntax accepts (owner Q12): creator, genre, publisher, magazine, year, type; 1.49.0 (lane T, S4): tag and
    /// series (a series field matches the work's title, alternative titles and folder names - the Series bucket has no values).
    /// </summary>
    public static readonly IReadOnlySet<FacetBucket> Typed = new HashSet<FacetBucket>
    {
        FacetBucket.Creators, FacetBucket.Genres, FacetBucket.Publisher, FacetBucket.Magazine, FacetBucket.Year, FacetBucket.Type,
        FacetBucket.Tags, FacetBucket.Series,
    };

    public static string Token(FacetBucket bucket) => Tokens.TryGetValue(bucket, out var token) ? token : bucket.ToString().ToLowerInvariant();

    /// <summary>A bucket by its token, its plural or its enum name (case-insensitive); false for anything else.</summary>
    public static bool TryParse(string? name, out FacetBucket bucket)
    {
        bucket = default;
        return name is not null && ByName.TryGetValue(name, out bucket);
    }

    private static Dictionary<string, FacetBucket> BuildNames()
    {
        var names = new Dictionary<string, FacetBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var (bucket, token) in Tokens)
        {
            names[token] = bucket;
            names[token + "s"] = bucket;
            names[bucket.ToString()] = bucket;
        }
        names["serie"] = FacetBucket.Series;
        return names;
    }
}

/// <summary>
/// One typed field of a search (<c>creator:"Naoki Urasawa"</c>): the bucket and the value as typed (unquoted, trimmed). 1.49.0 (lane T):
/// <see cref="Negated"/> for a leading <c>-</c> (<c>-genre:horror</c> = without), <see cref="Exact"/> when the value was quoted (only an
/// exact value matches; an unquoted value may also match a value that has all of its words, and may list alternatives with <c>|</c>).
/// </summary>
public sealed record TypedFacetField(FacetBucket Bucket, string Value, bool Negated = false, bool Exact = false)
{
    /// <summary>The most alternatives one typed field keeps (<c>genre:horror|comedy</c>).</summary>
    public const int MaxAlternatives = 20;

    /// <summary>The values this field accepts (any of them): the quoted value, or the unquoted value split at <c>|</c>.</summary>
    public IReadOnlyList<string> Alternatives => Exact
        ? [Value]
        : Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxAlternatives).ToList();
}

/// <summary>
/// A search text split into typed fields and the remaining free text. 1.49.0 (lane T, S4): <see cref="ReadState"/> from <c>is:read</c> /
/// <c>is:unread</c> / <c>is:reading</c> and <see cref="Library"/> from <c>library:</c> - scope words that apply to ANY search (the plain
/// search too); they are neither fields nor free text.
/// </summary>
public sealed record ParsedFacetText(IReadOnlyList<TypedFacetField> Fields, string FreeText, string? ReadState = null, string? Library = null)
{
    /// <summary>Whether a scope word (<c>is:</c>, <c>library:</c>) was typed.</summary>
    public bool HasScope => ReadState is not null || Library is not null;
}

/// <summary>
/// The typed field syntax of the search box (1.45.0, owner Q12): <c>creator:</c> <c>genre:</c> <c>publisher:</c> <c>magazine:</c>
/// <c>year:</c> <c>type:</c> followed by a word or a double-quoted value (<c>creator:"Naoki Urasawa"</c>; a space after the colon is
/// allowed). Field names are case-insensitive. Anything else - an unknown prefix such as <c>Re:Zero</c>, a field with no value, an
/// unterminated quote's text - stays free text, so a plain search never changes meaning by accident. Pure; no I/O.
/// <para>
/// 1.49.0 (lane T, S4): also <c>tag:</c> and <c>series:</c>; a leading <c>-</c> negates a field (<c>-genre:horror</c>); <c>year:</c>
/// takes a range (<c>year:2010..2015</c>, <c>year:..1999</c>, <c>year:2020..</c>); an unquoted value lists alternatives with <c>|</c>
/// (<c>genre:horror|mystery</c>); and two scope words - <c>is:read</c> / <c>is:unread</c> / <c>is:reading</c> and <c>library:&lt;name&gt;</c>
/// - which the search applies as its read filter and library. An unknown <c>is:</c> word stays free text.
/// </para>
/// </summary>
public static class FacetSearchSyntax
{
    /// <summary>The most typed fields one search keeps (the rest stay free text).</summary>
    public const int MaxFields = 10;

    /// <summary>The longest value a typed field keeps.</summary>
    public const int MaxValueLength = 200;

    /// <summary>The <c>is:</c> words (the read filter values of browse's Show filter).</summary>
    public static readonly IReadOnlySet<string> ReadStateWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read", "unread", "reading" };

    public static ParsedFacetText Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ParsedFacetText([], "");

        var fields = new List<TypedFacetField>();
        var free = new List<string>();
        string? readState = null;
        string? library = null;
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            var negated = text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]);
            var nameStart = negated ? i + 1 : i;
            var colon = -1;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '"')
            {
                if (text[i] == ':' && colon < 0)
                    colon = i;
                i++;
            }

            if (colon > nameStart)
            {
                var name = text[nameStart..colon];
                var (value, end, quoted) = FieldValue(text, colon, i);
                value = value?.Trim();
                var usable = !string.IsNullOrEmpty(value) && value.Length <= MaxValueLength;

                if (usable && !negated && readState is null && string.Equals(name, "is", StringComparison.OrdinalIgnoreCase)
                    && ReadStateWords.Contains(value!))
                {
                    readState = value!.ToLowerInvariant();
                    i = end;
                    continue;
                }
                if (usable && !negated && library is null && string.Equals(name, "library", StringComparison.OrdinalIgnoreCase))
                {
                    library = value;
                    i = end;
                    continue;
                }
                if (usable && fields.Count < MaxFields && FacetBucketTokens.TryParse(name, out var bucket) && FacetBucketTokens.Typed.Contains(bucket))
                {
                    fields.Add(new TypedFacetField(bucket, value!, negated, quoted));
                    i = end;
                    continue;
                }
            }

            // Not a field: the token (and a quoted part glued to it) is free text, kept as typed.
            if (i < text.Length && text[i] == '"')
            {
                var (_, end) = ReadValue(text, i);
                i = end;
            }
            free.Add(text[start..i]);
        }

        return new ParsedFacetText(fields, string.Join(' ', free).Trim(), readState, library);
    }

    /// <summary>A field's value after its colon: the rest of this word, or (when the word ends at the colon) a quoted value or the next word.</summary>
    private static (string? Value, int End, bool Quoted) FieldValue(string text, int colon, int wordEnd)
    {
        if (colon + 1 < wordEnd)
            return (text[(colon + 1)..wordEnd], wordEnd, false);
        var j = wordEnd;
        while (j < text.Length && text[j] == ' ')
            j++;
        var quoted = j < text.Length && text[j] == '"';
        var (value, end) = ReadValue(text, j);
        return (value, end, quoted);
    }

    /// <summary>A quoted value (up to the closing quote; an unterminated quote runs to the end) or a bare word, from <paramref name="at"/>.</summary>
    private static (string? Value, int End) ReadValue(string text, int at)
    {
        if (at >= text.Length)
            return (null, at);
        if (text[at] == '"')
        {
            var close = text.IndexOf('"', at + 1);
            return close < 0
                ? (text[(at + 1)..], text.Length)
                : (text[(at + 1)..close], close + 1);
        }
        var end = at;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;
        return (text[at..end], end);
    }

    /// <summary>
    /// 1.49.0 (lane T, S4): a <c>year:</c> value as an inclusive range - <c>2010</c>, <c>2010..2015</c>, <c>..1999</c>, <c>2020..</c>
    /// (the bounds swapped when given the wrong way round); null for anything else.
    /// </summary>
    public static (int From, int To)? YearRange(string value)
    {
        var dots = value.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0)
            return Year(value) is { } single ? (single, single) : null;
        var left = value[..dots].Trim();
        var right = value[(dots + 2)..].Trim();
        if (left.Length == 0 && right.Length == 0)
            return null;
        int? from = left.Length == 0 ? 1000 : Year(left);
        int? to = right.Length == 0 ? 9999 : Year(right);
        if (from is not { } a || to is not { } b)
            return null;
        return a <= b ? (a, b) : (b, a);

        static int? Year(string s) =>
            s.Length == 4 && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y >= 1000 ? y : null;
    }

    /// <summary>Renders fields back as syntax (for logs of shapes and tests): <c>creator:"x y"</c>, <c>-genre:horror</c>.</summary>
    public static string Render(TypedFacetField field)
    {
        var sb = new StringBuilder();
        if (field.Negated)
            sb.Append('-');
        sb.Append(FacetBucketTokens.Token(field.Bucket)).Append(':');
        return field.Exact || field.Value.Any(char.IsWhiteSpace)
            ? sb.Append('"').Append(field.Value).Append('"').ToString()
            : sb.Append(field.Value).ToString();
    }
}

/// <summary>
/// 1.49.0 (lane T, S5): the words of the tag view's free text - split at white space, a double-quoted part kept as one phrase, a leading
/// <c>-</c> = without. A small internal helper (lane S writes its own word normaliser for the plain search; the integrator folds the two at
/// merge if they converge). Pure.
/// </summary>
public static class TagViewText
{
    /// <summary>The most words the free text keeps (each is one condition).</summary>
    public const int MaxWords = 8;

    public sealed record Word(string Text, bool Negated);

    public static IReadOnlyList<Word> Words(string? text)
    {
        var words = new List<Word>();
        if (string.IsNullOrWhiteSpace(text))
            return words;
        var i = 0;
        while (i < text.Length && words.Count < MaxWords)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            var negated = text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]);
            if (negated)
                i++;
            string word;
            if (text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                word = close < 0 ? text[(i + 1)..] : text[(i + 1)..close];
                i = close < 0 ? text.Length : close + 1;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                    i++;
                word = text[start..i];
            }
            word = word.Trim();
            if (word.Length > 0 && word.Trim('"').Length > 0 && word != "-")
                words.Add(new Word(word, negated));
        }
        return words;
    }
}
