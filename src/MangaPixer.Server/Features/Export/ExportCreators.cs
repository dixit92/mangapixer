namespace com.lifepixer.mangapixer.Server.Features.Export;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;

/// <summary>
/// The creators of an export record (1.47.0, MangaList's <c>creator:</c> search): the record's stored credits grouped into one entry per
/// person, with the person's other names from the stored MangaUpdates author record. Pure over stored rows - never a request.
/// </summary>
public static class ExportCreators
{
    /// <summary>The page's list of optional record fields this server exports (<see cref="ExportMetadataPageDto.RecordFields"/>).</summary>
    public static readonly IReadOnlyList<string> RecordFields = ["creators"];

    /// <summary>Other names per creator in the export.</summary>
    public const int MaxOtherNames = 50;

    private const string AuthorRole = "author";
    private const string ArtistRole = "artist";

    private sealed class Person(string name, string? authorId)
    {
        public string Name { get; } = name;
        public string? AuthorId { get; } = authorId;
        public bool Author { get; set; }
        public bool Artist { get; set; }
    }

    /// <summary>The MangaUpdates author ids a record's credits name (what <see cref="Group"/> will look up); [] for other providers.</summary>
    public static IEnumerable<string> AuthorIdsOf(string provider, string? creatorsJson)
    {
        if (!string.Equals(provider, MangaUpdatesProvider.ProviderId, StringComparison.Ordinal))
            return [];
        return MetadataJson.ReadList<MetadataJson.Creator>(creatorsJson)
            .Where(c => IsCreditedRole(c.Role))
            .Select(c => AuthorIdOf(c.ProviderId))
            .OfType<long>()
            .Select(id => id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// One entry per person: the credits grouped by the provider's person id, else by name (ignoring case); <c>role</c> is
    /// <c>Author</c>, <c>Artist</c> or <c>Author, Artist</c>; <c>other</c> credits are dropped; the order is each person's first credit in
    /// the record's list. Null when no one is left (the key is then left out of the record).
    /// </summary>
    public static IReadOnlyList<ExportCreatorDto>? Group(
        string provider, string? creatorsJson, IReadOnlyDictionary<string, AuthorAliases> authors)
    {
        var isMangaUpdates = string.Equals(provider, MangaUpdatesProvider.ProviderId, StringComparison.Ordinal);
        var people = new List<Person>();
        var byKey = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);
        foreach (var credit in MetadataJson.ReadList<MetadataJson.Creator>(creatorsJson))
        {
            if (!IsCreditedRole(credit.Role) || credit.Name?.Trim() is not { Length: > 0 } name)
                continue;
            var authorId = isMangaUpdates ? AuthorIdOf(credit.ProviderId) : null;
            var key = authorId is { } id
                ? "id:" + id.ToString(CultureInfo.InvariantCulture)
                : (credit.ProviderId is { Length: > 0 } other ? "ref:" + other : "name:" + name);
            if (!byKey.TryGetValue(key, out var person))
            {
                person = new Person(name, authorId?.ToString(CultureInfo.InvariantCulture));
                byKey.Add(key, person);
                people.Add(person);
            }
            if (string.Equals(credit.Role.Trim(), AuthorRole, StringComparison.OrdinalIgnoreCase))
                person.Author = true;
            else
                person.Artist = true;
        }
        if (people.Count == 0)
            return null;
        return people.Select(p => new ExportCreatorDto
        {
            Name = p.Name,
            Role = p.Author && p.Artist ? "Author, Artist" : p.Author ? "Author" : "Artist",
            MuAuthorId = p.AuthorId is null ? null : long.Parse(p.AuthorId, CultureInfo.InvariantCulture),
            OtherNames = OtherNamesOf(p, authors),
        }).ToList();
    }

    private static IReadOnlyList<string> OtherNamesOf(Person person, IReadOnlyDictionary<string, AuthorAliases> authors)
    {
        if (person.AuthorId is null || !authors.TryGetValue(person.AuthorId, out var author))
            return [];
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { person.Name };
        // The author record's own name first (it is skipped when it is the credited spelling), then the stored other names in their order.
        foreach (var name in author.OtherNames.Prepend(author.Name))
        {
            if (names.Count >= MaxOtherNames)
                break;
            if (name?.Trim() is { Length: > 0 } trimmed && seen.Add(trimmed))
                names.Add(trimmed);
        }
        return names;
    }

    private static bool IsCreditedRole(string? role) =>
        role?.Trim() is { } r && (string.Equals(r, AuthorRole, StringComparison.OrdinalIgnoreCase) || string.Equals(r, ArtistRole, StringComparison.OrdinalIgnoreCase));

    private static long? AuthorIdOf(string? providerId) =>
        long.TryParse(providerId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}
