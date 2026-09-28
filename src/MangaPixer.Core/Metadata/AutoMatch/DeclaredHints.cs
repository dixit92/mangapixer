namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// Declared facts as matching evidence (1.28.0): what an admin stated about a folder or library (lane D's
/// <see cref="DeclaredFacts"/>), folded into a planned query. Positive only, like the evidence it reuses:
/// <list type="bullet">
/// <item>a declared type manga / manhwa / manhua / webtoon is the query's category hint (the record of that origin
/// gets <see cref="MatchScorer.OriginAgree"/>; nothing counts against another origin). An explicit declaration wins
/// over a category folder name. Comic, graphic novel and novel give no hint (no origin to agree with);</item>
/// <item>declared creators join the creator hints (a record by one of them gets <see cref="MatchScorer.CreatorHintAgree"/>,
/// and its title part of an <c>Author - Title</c> name is no longer capped); they never become author TAGS, so they
/// never veto a record.</item>
/// </list>
/// Pure; nothing here is sent anywhere.
/// </summary>
public static class DeclaredHints
{
    public static MatchQuery Apply(MatchQuery query, DeclaredFacts? declared)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (declared is null || declared.IsEmpty)
            return query;
        var context = query.Context;
        if (CategoryOf(declared.TypeValue) is { } category)
            context = context with { CategoryHint = category };
        var names = declared.Creators.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (names.Count > 0)
        {
            var hints = (context.CreatorHints ?? []).ToList();
            foreach (var name in names)
                if (!hints.Any(h => AutoMatchText.NamesEqual(h, name)))
                    hints.Add(name);
            context = context with { CreatorHints = hints };
        }
        return ReferenceEquals(context, query.Context) ? query : query with { Context = context };
    }

    /// <summary>The category hint word of a declared type, or null when the type names no origin.</summary>
    public static string? CategoryOf(DeclaredType? type) => type switch
    {
        DeclaredType.Manga => "manga",
        DeclaredType.Manhwa => "manhwa",
        DeclaredType.Manhua => "manhua",
        DeclaredType.Webtoon => "webtoon",
        _ => null,
    };
}
