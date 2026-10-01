namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// Declared facts as matching evidence (1.28.0), folded into a planned query:
/// <list type="bullet">
/// <item>a declared type is a STRONG HINT (1.30.0, owner: it replaced the 1.28.0 search filter): the scorer raises a record
/// whose type and origin fit it and lowers one that contradicts it (<see cref="DeclaredFactsComparer.TypeSignal"/>,
/// <see cref="MatchScorer.DeclaredTypeAgree"/> / <see cref="MatchScorer.DeclaredTypeMismatch"/>), never vetoes. It takes the
/// place of a category folder word (an explicit declaration wins);</item>
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
        if (declared.TypeValue is { } type)
            context = context with { DeclaredType = type };
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
}
