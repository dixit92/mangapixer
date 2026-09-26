namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers;

/// <summary>
/// The set of registered <see cref="IMetadataProvider"/>s (1.24.0). Empty in lane
/// B1 - no provider implementation ships and nothing is ever called; lane B2
/// registers MangaUpdates (<c>services.AddSingleton&lt;IMetadataProvider, ...&gt;()</c>)
/// and resolves providers only from inside its MetadataGateway. Surfaces use the
/// registry for attribution names only.
/// </summary>
public sealed class MetadataProviderRegistry
{
    private readonly Dictionary<string, IMetadataProvider> _providers;

    public MetadataProviderRegistry(IEnumerable<IMetadataProvider> providers)
    {
        _providers = new Dictionary<string, IMetadataProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
            _providers[provider.Id] = provider;
    }

    public IReadOnlyCollection<IMetadataProvider> All => _providers.Values;

    /// <summary>The provider with <paramref name="id"/>, or null when none is registered.</summary>
    public IMetadataProvider? Find(string id) => _providers.GetValueOrDefault(id);

    /// <summary>Attribution name: the provider's display name, or its id when not registered.</summary>
    public string DisplayNameFor(string id) => Find(id)?.DisplayName ?? id;
}
