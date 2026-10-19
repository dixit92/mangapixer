namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

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
    };

    private static readonly Dictionary<string, FacetBucket> ByName = BuildNames();

    /// <summary>The buckets typed field syntax accepts (owner Q12): creator, genre, publisher, magazine, year, type.</summary>
    public static readonly IReadOnlySet<FacetBucket> Typed = new HashSet<FacetBucket>
    {
        FacetBucket.Creators, FacetBucket.Genres, FacetBucket.Publisher, FacetBucket.Magazine, FacetBucket.Year, FacetBucket.Type,
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

/// <summary>One typed field of a search (<c>creator:"Naoki Urasawa"</c>): the bucket and the value as typed (unquoted, trimmed).</summary>
public sealed record TypedFacetField(FacetBucket Bucket, string Value);

/// <summary>A search text split into typed fields and the remaining free text.</summary>
public sealed record ParsedFacetText(IReadOnlyList<TypedFacetField> Fields, string FreeText);

/// <summary>
/// The typed field syntax of the search box (1.45.0, owner Q12): <c>creator:</c> <c>genre:</c> <c>publisher:</c> <c>magazine:</c>
/// <c>year:</c> <c>type:</c> followed by a word or a double-quoted value (<c>creator:"Naoki Urasawa"</c>; a space after the colon is
/// allowed). Field names are case-insensitive. Anything else - an unknown prefix such as <c>Re:Zero</c>, a field with no value, an
/// unterminated quote's text - stays free text, so a plain search never changes meaning by accident. Pure; no I/O.
/// </summary>
public static class FacetSearchSyntax
{
    /// <summary>The most typed fields one search keeps (the rest stay free text).</summary>
    public const int MaxFields = 10;

    /// <summary>The longest value a typed field keeps.</summary>
    public const int MaxValueLength = 200;

    public static ParsedFacetText Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ParsedFacetText([], "");

        var fields = new List<TypedFacetField>();
        var free = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            var colon = -1;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '"')
            {
                if (text[i] == ':' && colon < 0)
                    colon = i;
                i++;
            }

            if (colon > start && fields.Count < MaxFields
                && FacetBucketTokens.TryParse(text[start..colon], out var bucket) && FacetBucketTokens.Typed.Contains(bucket))
            {
                // The value: the rest of this word, or (when the word ends at the colon) a quoted value or the next word.
                string? value;
                var end = i;
                if (colon + 1 < i)
                {
                    value = text[(colon + 1)..i];
                }
                else
                {
                    var j = i;
                    while (j < text.Length && text[j] == ' ')
                        j++;
                    (value, end) = ReadValue(text, j);
                }

                value = value?.Trim();
                if (!string.IsNullOrEmpty(value) && value.Length <= MaxValueLength)
                {
                    fields.Add(new TypedFacetField(bucket, value));
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

        return new ParsedFacetText(fields, string.Join(' ', free).Trim());
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

    /// <summary>Renders fields back as syntax (for logs of shapes and tests): <c>creator:"x y"</c>.</summary>
    public static string Render(TypedFacetField field)
    {
        var sb = new StringBuilder(FacetBucketTokens.Token(field.Bucket)).Append(':');
        return field.Value.Any(char.IsWhiteSpace)
            ? sb.Append('"').Append(field.Value).Append('"').ToString()
            : sb.Append(field.Value).ToString();
    }
}
