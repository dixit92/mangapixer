namespace com.lifepixer.mangapixer.Server.Features.Metadata.Authors;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The local relink of a declared artist (1.45.0, owner 2026-10-19: "Yes and attempt to relink"): when an admin renames the declared
/// artist and its MangaUpdates reference is dropped, the new name gets the MangaUpdates author id of the ONE provider-backed creator
/// this server already knows by that name - a stored MangaUpdates author record (its main name or one of its other names, in
/// <c>metadata_authors</c>) or a creator of the tag index with a MangaUpdates provider id (<c>creator_names</c> /
/// <c>creator_provider_ids</c>). Names compare by <see cref="DeclaredFactsComparer.NameKey"/>. Two or more ids for the name, or none: no
/// reference. Stored data only - never a request of any kind.
/// </summary>
public static class LocalAuthorRelink
{
    /// <summary>The audit result of an artist declaration that was relinked locally (the artist-folder / declared-facts audit row).</summary>
    public const string AuditResult = "local_relink";

    /// <summary>The MangaUpdates author id the name names unambiguously on this server, or null.</summary>
    public static async Task<string?> FindAsync(MangaPixerDbContext db, string name, CancellationToken ct)
    {
        var key = DeclaredFactsComparer.NameKey(name);
        if (key.Length == 0)
            return null;
        var ids = new HashSet<string>(StringComparer.Ordinal);

        var provider = MangaUpdatesProvider.ProviderId;
        var authors = await db.MetadataAuthors.AsNoTracking()
            .Where(a => a.Provider == provider && a.Name != null)
            .Select(a => new { a.ExternalId, a.Name, a.OtherNamesJson })
            .ToListAsync(ct);
        foreach (var author in authors)
        {
            if (!AuthorAliasLookupService.IsAuthorId(author.ExternalId))
                continue;
            if (DeclaredFactsComparer.NameKey(author.Name!) == key
                || MetadataJson.ReadList<string>(author.OtherNamesJson).Any(n => DeclaredFactsComparer.NameKey(n) == key))
            {
                ids.Add(author.ExternalId);
            }
        }

        var creators = await db.CreatorNames.AsNoTracking().Where(n => n.NameKey == key).Select(n => n.CreatorId).Distinct().ToListAsync(ct);
        if (creators.Count > 0)
        {
            // A merged creator speaks for the creator it was merged into as well.
            var merged = await db.Creators.AsNoTracking().Where(c => creators.Contains(c.Id) && c.MergedIntoId != null)
                .Select(c => c.MergedIntoId!.Value).ToListAsync(ct);
            var all = creators.Concat(merged).Distinct().ToList();
            var providerIds = await db.CreatorProviderIds.AsNoTracking().Where(p => all.Contains(p.CreatorId) && p.Provider == provider)
                .Select(p => p.ExternalId).ToListAsync(ct);
            ids.UnionWith(providerIds.Where(AuthorAliasLookupService.IsAuthorId));
        }
        return ids.Count == 1 ? ids.Single() : null;
    }
}
