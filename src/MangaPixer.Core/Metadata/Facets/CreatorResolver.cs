namespace com.lifepixer.mangapixer.Core.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// Which canonical creator a credit belongs to: an existing creator (<see cref="Id"/> &gt; 0), or one to create - the creator of a provider
/// reference not seen before (<see cref="NewProviderRef"/>) or the name-only creator of a name key not seen before (<see cref="NewNameKey"/>).
/// </summary>
public readonly record struct CreatorHandle(long Id, string? NewProviderRef, string? NewNameKey)
{
    public static CreatorHandle Existing(long id) => new(id, null, null);

    public static CreatorHandle ForNewProvider(string reference) => new(0, reference, null);

    public static CreatorHandle ForNewName(string nameKey) => new(0, null, nameKey);

    public bool IsNew => Id <= 0;

    public override string ToString() => Id > 0 ? "creator " + FacetText.Invariant(Id) : NewProviderRef is not null ? "new provider creator" : "new name-only creator";
}

/// <summary>An admin decision about creators as the resolver reads it (<c>creator_overrides</c>).</summary>
public sealed record CreatorOverride(long Id, CreatorOverrideKind Kind, string Subject, long TargetCreatorId, string? Value = null);

/// <summary>
/// What the resolver knows before it resolves a credit: the stored creators (provider reference -&gt; creator, name key -&gt; name-only creator,
/// merges), the admin overrides, and every spelling each provider-backed person is known by - the record credits that carry its reference
/// and the stored provider author record's main and other names (<c>metadata_authors</c>).
/// </summary>
public sealed record CreatorRegistry(
    IReadOnlyDictionary<string, long> ProviderCreators,
    IReadOnlyDictionary<string, long> NameOnlyCreators,
    IReadOnlyDictionary<long, long> MergedInto,
    IReadOnlyList<CreatorOverride> Overrides,
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> ProviderNames)
{
    public static CreatorRegistry Empty { get; } = new(
        new Dictionary<string, long>(), new Dictionary<string, long>(), new Dictionary<long, long>(), [],
        new Dictionary<string, IReadOnlyCollection<string>>());
}

/// <summary>
/// Creator resolution (1.45.0 lane F, D1 2.2; owner Q7). Deterministic and order-independent: a credit's creator depends only on the credit,
/// its context (the provider references credited on the same work) and the registry - never on which other credits were seen first.
/// Order of the rules, admin overrides first:
/// <list type="number">
/// <item><b>Split pins</b> - a provider reference or a name key an admin pinned to a creator (a split) goes there; resolution never undoes
/// it (the newest pin per subject wins).</item>
/// <item><b>R1 provider id</b> - a credit with a provider reference belongs to the creator holding it (a new creator on first sight). Two
/// different references are never merged automatically.</item>
/// <item><b>R2 same work</b> - a credit without a reference whose name keys (<see cref="DeclaredFactsComparer.NameKeys"/>) meet the
/// spellings of exactly one creator credited WITH a reference on the same work.</item>
/// <item><b>R3 unique strong name</b> - otherwise a name of two or more words
/// (<see cref="AutoMatchText.IsAuthorLike(string?, bool)"/> with two tokens) whose keys meet exactly one provider-backed creator's
/// spellings anywhere.</item>
/// <item><b>R4 name-only</b> - otherwise the one name-only creator of the name's key (<see cref="DeclaredFactsComparer.NameKey"/>).</item>
/// </list>
/// A merged creator redirects: every handle is followed through <see cref="CreatorRegistry.MergedInto"/> to the live creator.
/// </summary>
public sealed class CreatorResolver
{
    /// <summary>The longest merge chain followed (a cycle cannot loop).</summary>
    public const int MaxMergeHops = 32;

    private readonly CreatorRegistry _registry;
    private readonly Dictionary<string, long> _pinsByRef = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _pinsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _refsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _keysByRef = new(StringComparer.Ordinal);

    public CreatorResolver(CreatorRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        foreach (var pin in registry.Overrides.Where(o => o.Kind == CreatorOverrideKind.SplitPin).OrderBy(o => o.Id))
        {
            if (FacetText.ParseProviderRef(pin.Subject) is not null)
                _pinsByRef[pin.Subject] = pin.TargetCreatorId;
            else if (pin.Subject.Length > 0)
                _pinsByKey[pin.Subject] = pin.TargetCreatorId;
        }
        foreach (var (reference, names) in registry.ProviderNames)
        {
            var keys = names.SelectMany(DeclaredFactsComparer.NameKeys).ToHashSet(StringComparer.Ordinal);
            _keysByRef[reference] = keys;
            foreach (var key in keys)
            {
                if (!_refsByKey.TryGetValue(key, out var refs))
                    _refsByKey[key] = refs = new HashSet<string>(StringComparer.Ordinal);
                refs.Add(reference);
            }
        }
    }

    public CreatorRegistry Registry => _registry;

    /// <summary>
    /// The creator of one credit, or null when the credit has no usable name and no reference. <paramref name="contextRefs"/>: the provider
    /// references credited on the same work (the linked record's credits, the declared creators' picked authors) - R2's scope.
    /// </summary>
    public CreatorHandle? Resolve(FacetCredit credit, IReadOnlyCollection<string>? contextRefs = null)
    {
        ArgumentNullException.ThrowIfNull(credit);
        if (credit.ProviderRef is { } reference && FacetText.ParseProviderRef(reference) is not null)
            return ForRef(reference);

        var name = DeclaredFactKeys.CleanName(credit.Name);
        if (name is null)
            return null;
        var primary = DeclaredFactsComparer.NameKey(name);
        if (primary.Length == 0)
            return null;
        if (_pinsByKey.TryGetValue(primary, out var pinned))
            return Final(CreatorHandle.Existing(pinned));

        var keys = DeclaredFactsComparer.NameKeys(name).ToList();

        // R2: a creator credited with a reference on the same work, by any of its spellings.
        if (contextRefs is { Count: > 0 })
        {
            var sameWork = Distinct(contextRefs.Where(r => _keysByRef.TryGetValue(r, out var refKeys) && keys.Any(refKeys.Contains)));
            if (sameWork.Count == 1)
                return sameWork[0];
        }

        // R3: exactly one provider-backed creator anywhere, for a name of two or more words.
        if (AutoMatchText.IsAuthorLike(name, requireTwoTokens: true))
        {
            var anywhere = Distinct(keys.SelectMany(k => _refsByKey.TryGetValue(k, out var refs) ? refs : (IEnumerable<string>)[]));
            if (anywhere.Count == 1)
                return anywhere[0];
        }

        // R4: the name-only creator of the key.
        return _registry.NameOnlyCreators.TryGetValue(primary, out var nameOnly)
            ? Final(CreatorHandle.Existing(nameOnly))
            : CreatorHandle.ForNewName(primary);
    }

    /// <summary>The creator of a provider reference: its split pin, else its stored creator, else a new one - followed through merges.</summary>
    public CreatorHandle ForRef(string reference)
    {
        if (_pinsByRef.TryGetValue(reference, out var pinned))
            return Final(CreatorHandle.Existing(pinned));
        return _registry.ProviderCreators.TryGetValue(reference, out var id)
            ? Final(CreatorHandle.Existing(id))
            : CreatorHandle.ForNewProvider(reference);
    }

    /// <summary>The live creator a handle redirects to (merges followed; a new handle is itself).</summary>
    public CreatorHandle Final(CreatorHandle handle)
    {
        if (handle.IsNew)
            return handle;
        var id = handle.Id;
        for (var hop = 0; hop < MaxMergeHops && _registry.MergedInto.TryGetValue(id, out var next) && next != id; hop++)
            id = next;
        return CreatorHandle.Existing(id);
    }

    /// <summary>The live creator an id redirects to.</summary>
    public long FinalId(long creatorId) => Final(CreatorHandle.Existing(creatorId)).Id;

    private List<CreatorHandle> Distinct(IEnumerable<string> references) =>
        references.Distinct(StringComparer.Ordinal).Select(ForRef).Distinct().ToList();
}

/// <summary>A spelling and how often the creator's credits use it (<paramref name="FirstSeen"/>: lower = older, the tie-break).</summary>
public sealed record CreatorSpellingUse(string Name, int Uses, long FirstSeen);

/// <summary>The display-name rule of a creator (D1 2.2): admin &gt; the provider author record's main name &gt; the most used spelling (tie: oldest).</summary>
public static class CreatorDisplayName
{
    public static (string Name, CreatorDisplayNameSource Source)? Choose(
        string? adminName, IEnumerable<string?> providerMainNames, IEnumerable<CreatorSpellingUse> spellings)
    {
        if (FacetText.Display(adminName) is { } admin)
            return (admin, CreatorDisplayNameSource.Admin);
        if (providerMainNames.Select(FacetText.Display).FirstOrDefault(n => n is not null) is { } provider)
            return (provider, CreatorDisplayNameSource.Provider);
        var best = spellings
            .Where(s => FacetText.Display(s.Name) is not null)
            .OrderByDescending(s => s.Uses)
            .ThenBy(s => s.FirstSeen)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return best is null ? null : (FacetText.Display(best.Name)!, CreatorDisplayNameSource.MostUsed);
    }
}
