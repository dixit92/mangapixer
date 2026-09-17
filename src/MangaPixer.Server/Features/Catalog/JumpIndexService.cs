using System.Globalization;
using System.Text;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace com.lifepixer.mangapixer.Server.Features.Catalog;

/// <summary>
/// Per-library jump index for multilingual collation-aware jump navigation.
///
/// Builds a coarse A–Z/script rail from the existing persisted <c>SortKey</c>s of
/// the top-level children of a library (the same set the browse endpoint serves
/// at <c>parentId=null</c>). Bucketing is by the first collation element of the
/// display name, using Unicode/ICU-aware script detection (.NET on Linux uses
/// ICU by default). Buckets are returned in a fixed, UI-sensible rail order with
/// a <c>FirstCursor</c> that is a valid keyset cursor for the name sort of browse.
///
/// This service is kept separate from <c>CatalogBrowseService</c>, which owns the
/// main catalog-browsing surface. It reuses the same persisted <c>SortKey</c> ordering and
/// the same raw-<c>SortKey</c> cursor scheme the name sort already honours, so a
/// bucket's <c>FirstCursor</c> can be passed straight to the browse endpoint with
/// <c>sort=name</c> and land on the bucket's first node.
/// </summary>
public sealed class JumpIndexService
{
    private readonly MangaPixerDbContext _db;

    public JumpIndexService(MangaPixerDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Computes the jump index for the top-level children of a library.
    /// Authorization is enforced: an inaccessible library yields an empty index.
    /// </summary>
    public async Task<JumpIndexDto> GetJumpIndexAsync(
        long userId,
        long libraryId,
        CancellationToken ct = default)
    {
        // Authorization — same rule as browse: accessible libraries only.
        var accessibleLibs = await GetAccessibleLibraryIdsAsync(userId, ct);
        if (!accessibleLibs.Contains(libraryId))
        {
            return new JumpIndexDto { LibraryId = "", Buckets = [] };
        }

        // Fetch the same set browse serves at parentId=null: top-level children,
        // excluding tombstoned, ordered by SortKey (ordinal). We only need the
        // display name (for bucketing) and the SortKey (for the cursor). Folders
        // and archives are both included.
        //
        // IMPORTANT: the SortKey prefix separates folders ("0") from archives
        // ("1"), so all folders sort before all archives. Nodes with the same
        // first letter can therefore be split across the folder/archive
        // boundary. We group by label (not by contiguous SortKey runs) so all
        // "A" nodes — folders and archives alike — land in one bucket. The
        // cursor for a bucket is the SortKey of the node immediately before the
        // bucket's first node in SortKey order, so the browse endpoint's
        // exclusive `SortKey > cursor` filter lands on that first node.
        var rows = await _db.CatalogNodes
            .Where(n => n.LibraryId == libraryId)
            .Where(n => n.ParentId == null)
            .Where(n => n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .OrderBy(n => n.SortKey)
            .Select(n => new { n.DisplayName, n.SortKey })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            var libPublicId = await _db.Libraries
                .Where(l => l.Id == libraryId)
                .Select(l => l.PublicId)
                .FirstOrDefaultAsync(ct);
            return new JumpIndexDto { LibraryId = libPublicId ?? "", Buckets = [] };
        }

        // First pass: group by label, tracking the first SortKey for each label
        // (and the SortKey of the node immediately before it) and the total count.
        // Because rows are in SortKey order, the first time we see a label is the
        // earliest position of any node with that label.
        var groups = new Dictionary<string, (int Count, string? FirstSortKey, string? PrevSortKey)>();
        string? prevSortKey = null;

        foreach (var row in rows)
        {
            var label = BucketLabelFor(row.DisplayName);

            if (!groups.TryGetValue(label, out var g))
            {
                // First occurrence of this label — record the cursor position
                // (the SortKey of the node just before this one) and start counting.
                groups[label] = (1, row.SortKey, prevSortKey);
            }
            else
            {
                groups[label] = (g.Count + 1, g.FirstSortKey, g.PrevSortKey);
            }

            prevSortKey = row.SortKey;
        }

        // Build buckets from the grouped data, ordered by the fixed rail rank.
        var buckets = groups
            .Select(kv => new JumpIndexBucketDto
            {
                Label = kv.Key,
                Count = kv.Value.Count,
                // The cursor is the SortKey of the node immediately before the
                // bucket's first node, so `SortKey > cursor` lands on the first
                // node. Null for the very first bucket (start of the listing).
                FirstCursor = kv.Value.PrevSortKey,
            })
            .OrderBy(b => RailRank(b.Label))
            .ToList();

        var libId = await _db.Libraries
            .Where(l => l.Id == libraryId)
            .Select(l => l.PublicId)
            .FirstOrDefaultAsync(ct);

        return new JumpIndexDto { LibraryId = libId ?? "", Buckets = buckets };
    }

    /// <summary>
    /// Maps a display name to its bucket label by the first collation element.
    /// Uses ICU-aware script detection on Linux (.NET uses ICU by default there).
    /// Latin letters are upper-cased and collapsed to a single A–Z label; digits
    /// map to "#"; recognised script blocks map to a script-group label; anything
    /// else (symbols, unassigned, mixed) maps to "Other".
    /// </summary>
    internal static string BucketLabelFor(string displayName)
    {
        if (string.IsNullOrEmpty(displayName))
            return "Other";

        // Skip common leading non-letter noise (quotes, brackets, punctuation)
        // so "The Apple" still buckets under "A" and "[Archive]" under "A".
        // We only skip a small, well-defined set of leading symbols; once we hit
        // a letter or digit we stop. This keeps bucketing predictable.
        var i = 0;
        while (i < displayName.Length)
        {
            var ch = displayName[i];
            if (char.IsLetterOrDigit(ch))
                break;
            // Skip ASCII punctuation/whitespace noise but stop at anything that
            // is a real script character we don't recognise (handled below).
            if (IsSkippableLead(ch))
            {
                i++;
                continue;
            }
            break;
        }

        if (i >= displayName.Length)
            return "Other";

        var first = displayName[i];

        // Latin letters → A–Z (culture-invariant upper to avoid locale surprises;
        // the bucket label is a display glyph, not a sort key).
        if (IsLatinLetter(first))
            return char.ToUpperInvariant(first).ToString();

        // Digits → "#"
        if (char.IsDigit(first))
            return "#";

        // Script groups — detect by Unicode category / block of the first
        // significant character. We use the rune's general category and a small
        // block-range check so this is deterministic and ICU-independent in
        // *classification* (the rail is stable across ICU/NLS), while the
        // underlying SortKey ordering (ordinal) is what the cursor honours.
        var rune = new Rune(first);
        return rune.Utf16SequenceLength == 1 ? ScriptBucket(rune) : ScriptBucket(rune);
    }

    private static string ScriptBucket(Rune rune)
    {
        var c = rune.Value;

        // Hiragana (U+3040–U+309F) and Katakana (U+30A0–U+30FF + half-width
        // U+FF65–U+FF9F) → "Kana".
        if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0xFF65 && c <= 0xFF9F))
            return "Kana";

        // Hangul Jamo / Syllables / Compatibility Jamo → "Hangul".
        if ((c >= 0x1100 && c <= 0x11FF) || (c >= 0xAC00 && c <= 0xD7AF) || (c >= 0x3130 && c <= 0x318F))
            return "Hangul";

        // CJK Unified Ideographs + extensions A/B/C/D/E/F + radicals → "CJK".
        if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) ||
            (c >= 0x20000 && c <= 0x2FFFF) || (c >= 0x2E80 && c <= 0x2EFF) ||
            (c >= 0x2F00 && c <= 0x2FDF))
            return "CJK";

        // Cyrillic (U+0400–U+04FF + supplement U+0500–U+052F).
        if (c >= 0x0400 && c <= 0x052F)
            return "Cyrillic";

        // Greek (U+0370–U+03FF + extended U+1F00–U+1FFF).
        if ((c >= 0x0370 && c <= 0x03FF) || (c >= 0x1F00 && c <= 0x1FFF))
            return "Greek";

        // Arabic (U+0600–U+06FF + supplement U+0750–U+077F).
        if ((c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F))
            return "Arabic";

        // Hebrew (U+0590–U+05FF).
        if (c >= 0x0590 && c <= 0x05FF)
            return "Hebrew";

        // Thai (U+0E00–U+0E7F).
        if (c >= 0x0E00 && c <= 0x0E7F)
            return "Thai";

        // Anything else (symbols, unassigned, other scripts) → "Other".
        return "Other";
    }

    private static bool IsLatinLetter(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    private static bool IsSkippableLead(char c) =>
        c is ' ' or '\t' or '"' or '\'' or '(' or ')' or '[' or ']' or '{' or '}' or '-' or '_' or '.' or ',' or '!' or '?' or '*' or '#' or '@' or '~';

    /// <summary>
    /// Fixed rail rank for a bucket label. Lower sorts first. "#" (numeric/symbol)
    /// leads the rail so numeric-leading titles get their own leading bucket, then
    /// Latin A–Z in their natural order, then script groups in a stable, UI-sensible
    /// order, with "Other" last.
    ///
    /// The rail is a coarse A–Z navigation aid, not a 1:1 mirror of the persisted
    /// <c>SortKey</c> listing. Since 1.15.0 <c>SortKey.EncodeName</c> opens a digit run
    /// with a marker inside the ASCII digit range, so digit-leading names sort ahead of
    /// letter-leading names within a kind — the same place plain ordinal comparison
    /// puts them, and the same place the leading "#" bucket sits on the rail. The rail
    /// is still not a mirror of the listing: it groups a letter's folders and archives
    /// into one bucket, while the listing puts every folder before every archive.
    /// The bucket's <c>FirstCursor</c> honours <c>SortKey</c> order regardless: it is
    /// the key of the node immediately before the bucket's first node, so the browse
    /// endpoint's exclusive <c>SortKey > cursor</c> filter lands on that node wherever
    /// it falls in the listing.
    /// </summary>
    internal static int RailRank(string label)
    {
        // "#" (numeric/symbol) first — a leading numeric/symbol bucket by
        // convention (as in a typical A–Z index), which since 1.15.0 also matches
        // the persisted listing: digits precede letters within each kind. The cursor
        // is still the SortKey of the node just before the bucket's first node, so
        // browse lands on it wherever it falls.
        if (label == "#")
            return 0;

        // Latin A–Z → ranks 1..26.
        if (label.Length == 1)
        {
            var c = label[0];
            if (c >= 'A' && c <= 'Z')
                return c - 'A' + 1;
        }

        // Script groups in a fixed, stable order.
        return label switch
        {
            "Kana" => 27,
            "Hangul" => 28,
            "CJK" => 29,
            "Cyrillic" => 30,
            "Greek" => 31,
            "Arabic" => 32,
            "Hebrew" => 33,
            "Thai" => 34,
            _ => 99, // "Other" and anything unexpected last
        };
    }

    private async Task<List<long>> GetAccessibleLibraryIdsAsync(long userId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
            return [];

        if (user.IsAdmin)
            return await _db.Libraries.Select(l => l.Id).ToListAsync(ct);

        return await _db.LibraryGrants
            .Where(g => g.UserId == userId)
            .Select(g => g.LibraryId)
            .ToListAsync(ct);
    }
}
