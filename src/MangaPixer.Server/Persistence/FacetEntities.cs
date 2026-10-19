namespace com.lifepixer.mangapixer.Server.Persistence.Entities;

// 1.45.0 tag index and canonical creators (seam I0, migration AddFacetIndex; D1 "Information Panel and Faceted Tags" 2.2 / 2.3).
// Configured in Persistence/FacetModel.cs. The vocabulary (buckets, sources, roles, kinds, the work key) is
// Core/Metadata/Facets/FacetVocabulary.cs. Everything here is DERIVED and rebuilt from the stored sources (records, ComicInfo,
// declared facts, provider authors, links, the tree) except creator_overrides, which is admin input. Ids are surrogate and STABLE:
// a rebuild upserts, it never truncates. No path anywhere; names are the provider / ComicInfo / declared spellings (shown in the UI,
// never logged).

/// <summary>
/// A canonical creator (<c>creators</c>): one person or circle, whichever spellings and provider ids it is known by. A merged creator
/// keeps its row with <see cref="MergedIntoId"/> set - the redirect, so bookmarked views and links keep working.
/// </summary>
public sealed class CreatorEntity
{
    public long Id { get; set; }

    /// <summary><c>cr_</c> + base36 (<c>FacetPublicIds.NewCreatorId</c>); unique.</summary>
    public string PublicId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary><c>CreatorDisplayNameSource</c>: 0 provider main name, 1 most used spelling, 2 admin.</summary>
    public int DisplayNameSource { get; set; }

    /// <summary>The creator this one was merged into (an admin merge); null for a live creator.</summary>
    public long? MergedIntoId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One spelling of a creator (<c>creator_names</c>); unique per (creator, name), looked up by <see cref="NameKey"/>.</summary>
public sealed class CreatorNameEntity
{
    public long Id { get; set; }
    public long CreatorId { get; set; }

    /// <summary>The spelling as its source wrote it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>DeclaredFactsComparer.NameKey(Name)</c>.</summary>
    public string NameKey { get; set; } = string.Empty;

    /// <summary><c>CreatorNameScript</c>: 0 latin, 1 cjk, 2 hangul, 3 other (computed).</summary>
    public int Script { get; set; }

    /// <summary><c>CreatorNameSource</c>: 0 record credit, 1 provider author record, 2 ComicInfo, 3 declared, 4 admin.</summary>
    public int Source { get; set; }
}

/// <summary>A provider's id of a creator (<c>creator_provider_ids</c>), e.g. the MangaUpdates <c>author_id</c>; one creator per id.</summary>
public sealed class CreatorProviderIdEntity
{
    public long CreatorId { get; set; }

    /// <summary>Provider slug (max 32): <c>mangaupdates</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's id as text (max 64). The key with <see cref="Provider"/>.</summary>
    public string ExternalId { get; set; } = string.Empty;
}

/// <summary>
/// An admin decision about creators (<c>creator_overrides</c>): INPUT, never derived, never purged by "Delete fetched web data"; the
/// creator rules apply these first. Audited as <c>metadata.creator_merge</c> / <c>metadata.creator_split</c> (ids only).
/// </summary>
public sealed class CreatorOverrideEntity
{
    public long Id { get; set; }

    /// <summary><c>CreatorOverrideKind</c>: 0 merge, 1 split-pin, 2 display name.</summary>
    public int Kind { get; set; }

    /// <summary>What the decision is about: a provider ref <c>mangaupdates:&lt;id&gt;</c> or a name key (max 300).</summary>
    public string Subject { get; set; } = string.Empty;

    public long TargetCreatorId { get; set; }

    /// <summary>The admin's display name (kind display name only; max 256). Not in the D1 list - the decision needs its text (I0).</summary>
    public string? Value { get; set; }

    /// <summary>The admin who decided (internal user id, as audit rows carry it); null when the account is gone.</summary>
    public long? ActorUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A work of the tag index (<c>facet_works</c>): the counting unit (D1 2.1). <see cref="WorkKey"/> is <c>FacetWorkKey</c>
/// (<c>rec:</c> / <c>ci:</c> / <c>node:</c>) - global, so one linked series in two libraries is one work with two locations.
/// </summary>
public sealed class FacetWorkEntity
{
    public long Id { get; set; }

    /// <summary>The work key (unique; <c>FacetWorkKey.MaxLength</c>).</summary>
    public string WorkKey { get; set; } = string.Empty;

    /// <summary><c>FacetWorkKind</c>: 0 record, 1 ComicInfo, 2 node.</summary>
    public int Kind { get; set; }

    /// <summary>The linked record of a <c>rec:</c> work; the work goes with the record.</summary>
    public long? RecordId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string SortKey { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A tag value (<c>facet_values</c>): one chip - unique per (bucket, value key). A creator value is <c>ValueKey = c:&lt;creator id&gt;</c>
/// with <see cref="CreatorId"/> set.
/// </summary>
public sealed class FacetValueEntity
{
    public long Id { get; set; }

    /// <summary><c>fv_</c> + base36 (<c>FacetPublicIds.NewValueId</c>); unique - what URLs carry (<c>f=&lt;bucket&gt;:&lt;id&gt;</c>).</summary>
    public string PublicId { get; set; } = string.Empty;

    /// <summary><c>FacetBucket</c>.</summary>
    public int Bucket { get; set; }

    public string ValueKey { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
    public long? CreatorId { get; set; }
}

/// <summary>
/// One value at one LOCATION of a work (<c>facet_entries</c>): entries are per location, so a value only one location's ComicInfo
/// carries is visible only with that location - nothing about a hidden location leaks through a visible one (D1 2.3 / 2.5).
/// </summary>
public sealed class FacetEntryEntity
{
    public long Id { get; set; }
    public long WorkId { get; set; }

    /// <summary>The folder or archive where the work lives (a node of <see cref="LibraryId"/>).</summary>
    public long LocationNodeId { get; set; }

    public long LibraryId { get; set; }
    public long ValueId { get; set; }

    /// <summary><c>FacetSources</c> flags: web 1, ComicInfo 2, declared 4, admin 8.</summary>
    public int Sources { get; set; }

    /// <summary><c>FacetRoles</c> flags (creator values): writer 1, artist 2.</summary>
    public int Roles { get; set; }

    /// <summary>A declared value that disagrees with the record (shown amber, with the existing Conflict wording).</summary>
    public bool Conflict { get; set; }
}

/// <summary>The index state of one library (<c>facet_index_libraries</c>): the inputs key it was built from and the last build's counts.</summary>
public sealed class FacetIndexLibraryEntity
{
    public long LibraryId { get; set; }

    /// <summary>A hash of the library's index inputs (catalog revision, link / record / ComicInfo / declared / author / override stamps).</summary>
    public string InputsKey { get; set; } = string.Empty;

    public DateTimeOffset BuiltAt { get; set; }
    public int Works { get; set; }
    public int Entries { get; set; }
    public long ElapsedMs { get; set; }
}
