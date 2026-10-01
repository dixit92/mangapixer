namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

// The cover check AFTER an automatic link (1.31.0, owner 2026-09-30). PURE: hashes in, a verdict out; no database, no file,
// no clock. The server gathers the hashes (CoverCheckService) from covers already stored - never a request.

/// <summary>What the cover check concluded for one Auto link.</summary>
public enum CoverCheckVerdict
{
    /// <summary>Fewer volumes compared than the rule needs: nothing changes, checked again when more covers are stored.</summary>
    NotEnough = 0,

    /// <summary>A local cover is the same picture as a stored cover of the record: the link stays.</summary>
    Agrees = 1,

    /// <summary>Neither the same nor clearly different everywhere (a distance in between): the link stays.</summary>
    Unsure = 2,

    /// <summary>Every compared volume is clearly a different picture: the Auto link goes to Needs review.</summary>
    Differs = 3,
}

/// <summary>
/// One volume of the folder to compare: the hashes of its LOCAL cover images (page 1 of each archive that is this volume, and the
/// two halves of a spread page 1) and the hashes of the linked record's stored covers of the SAME volume in every language.
/// </summary>
public sealed record CoverCheckVolume(int Volume, IReadOnlyList<ulong> Local, IReadOnlyList<ulong> Web);

/// <summary>
/// The verdict, the volumes actually compared (after the credits-page guard), their smallest distances (ascending by volume -
/// numbers only, for the log line) and how many volumes the guard dropped.
/// </summary>
public sealed record CoverCheckResult(CoverCheckVerdict Verdict, int Compared, IReadOnlyList<int> Distances, int Dropped)
{
    public bool Demotes => Verdict == CoverCheckVerdict.Differs;
}

/// <summary>
/// The rule of the post-link cover check (1.31.0). Covers are NEGATIVE evidence only per volume and per language: 1.28.0's check
/// compared MangaUpdates' one series image (often the latest volume, often Japanese) with a local volume and held back right links.
/// Here the local volume N is compared with the record's stored volume N covers in ANY language.
/// <list type="number">
/// <item><b>Agrees</b> when any local image is the same picture (<see cref="CoverHash.SameMaxDistance"/>) as ANY stored cover of
/// the record - its own volume, another volume (a numbering offset), another language, the main cover.</item>
/// <item><b>Credits-page guard:</b> page-1 images of two different volumes that are the same picture as each other are not covers
/// (a scanlator credits or colour page repeated in every volume); both volumes are left out.</item>
/// <item><b>Differs</b> only when every compared volume is clearly different (smallest distance at least
/// <see cref="CoverHash.DifferentMinDistance"/>) and at least <see cref="MinSeriesVolumes"/> volumes were compared - one for a
/// one-shot (<see cref="MinOneShotVolumes"/>: its only cover is all the evidence there is).</item>
/// <item>Otherwise <b>Unsure</b> (a distance in between) or <b>NotEnough</b> (too few volumes): nothing changes.</item>
/// </list>
/// </summary>
public static class CoverCheckRule
{
    /// <summary>A folder of volumes is demoted only when at least this many volumes were compared.</summary>
    public const int MinSeriesVolumes = 2;

    /// <summary>A one-shot (one archive) is demoted on its one cover.</summary>
    public const int MinOneShotVolumes = 1;

    /// <summary>At most this many volumes are compared per check (the server picks them; bounds the worker hashing).</summary>
    public const int MaxVolumes = 6;

    /// <param name="volumes">The volumes to compare (a volume without a local image or without a stored web cover is ignored).</param>
    /// <param name="allWeb">Every stored cover hash of the record (all volumes, all languages, the main cover).</param>
    /// <param name="oneShot">The work is a single archive.</param>
    public static CoverCheckResult Decide(IReadOnlyList<CoverCheckVolume> volumes, IReadOnlyCollection<ulong> allWeb, bool oneShot)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(allWeb);
        var usable = volumes.Where(v => v.Local.Count > 0 && v.Web.Count > 0).OrderBy(v => v.Volume).ToList();

        // 1. The same picture anywhere in the record: the right series.
        foreach (var local in volumes.SelectMany(v => v.Local))
        {
            if (allWeb.Any(w => CoverHash.Distance(local, w) <= CoverHash.SameMaxDistance))
                return new CoverCheckResult(CoverCheckVerdict.Agrees, usable.Count, usable.Select(MinDistance).ToList(), 0);
        }

        // 2. Credits-page guard: two volumes whose page 1 is the same picture.
        var dropped = new HashSet<int>();
        for (var i = 0; i < usable.Count; i++)
        {
            for (var j = i + 1; j < usable.Count; j++)
            {
                if (usable[i].Volume != usable[j].Volume && SamePicture(usable[i].Local, usable[j].Local))
                {
                    dropped.Add(i);
                    dropped.Add(j);
                }
            }
        }
        var compared = usable.Where((_, i) => !dropped.Contains(i)).ToList();
        var distances = compared.Select(MinDistance).ToList();

        // 3. Clearly different everywhere, on enough volumes.
        var needed = oneShot ? MinOneShotVolumes : MinSeriesVolumes;
        if (compared.Count < needed)
            return new CoverCheckResult(CoverCheckVerdict.NotEnough, compared.Count, distances, dropped.Count);
        var verdict = distances.All(d => d >= CoverHash.DifferentMinDistance) ? CoverCheckVerdict.Differs : CoverCheckVerdict.Unsure;
        return new CoverCheckResult(verdict, compared.Count, distances, dropped.Count);
    }

    private static int MinDistance(CoverCheckVolume v) =>
        v.Local.SelectMany(l => v.Web.Select(w => CoverHash.Distance(l, w))).DefaultIfEmpty(64).Min();

    private static bool SamePicture(IReadOnlyList<ulong> a, IReadOnlyList<ulong> b) =>
        a.Any(x => b.Any(y => CoverHash.Distance(x, y) <= CoverHash.SameMaxDistance));
}
