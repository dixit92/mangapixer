namespace com.lifepixer.mangapixer.Core.Metadata;

using System.Numerics;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>A web cover the automatic layer may use: its kind, stored id and 64-bit hash (null = not hashed).</summary>
/// <param name="Source">WebVolume, WebMain or Poster.</param>
/// <param name="VolumeCoverId">The <c>volume_covers</c> row (WebVolume / WebMain); null for the poster.</param>
/// <param name="Hash">The stored perceptual hash.</param>
/// <param name="OriginFallback">The preferred language had no cover; this is the origin-language one (re-checked later).</param>
public sealed record WebCoverCandidate(AutoCoverSource Source, long? VolumeCoverId, ulong? Hash, bool OriginFallback = false);

/// <summary>What the automatic layer decided for one node (a <c>node_auto_covers</c> row).</summary>
/// <param name="ArchiveNodeId">Source LocalVolume1: the archive whose resolved cover the folder shows.</param>
public sealed record CoverDecision(AutoCoverSource Source, AutoCoverReason Reason, CoverCropSide? CropSide = null,
    WebCoverCandidate? Web = null, long? ArchiveNodeId = null)
{
    /// <summary>Keep the file cover (stored so the node is not re-decided).</summary>
    public static CoverDecision File(AutoCoverReason reason) => new(AutoCoverSource.File, reason);

    /// <summary>A series folder shows its local volume 1 archive's resolved cover.</summary>
    public static CoverDecision LocalVolume1(long archiveNodeId) =>
        new(AutoCoverSource.LocalVolume1, AutoCoverReason.SeriesLocalVolume1, ArchiveNodeId: archiveNodeId);

    public static CoverDecision Crop(CoverCropSide side, AutoCoverReason reason) => new(AutoCoverSource.Crop, reason, side);

    public static CoverDecision FromWeb(WebCoverCandidate web, AutoCoverReason reason) => new(web.Source, reason, Web: web);

    /// <summary>The origin-language cover was used because the preferred one is missing: check again later.</summary>
    public bool NeedsRecheck => Web is { OriginFallback: true };
}

/// <summary>Reading direction of a book, for which half of a jacket spread is its front.</summary>
public enum CoverDirection
{
    LeftToRight = 0,
    RightToLeft = 1,
}

/// <summary>
/// The automatic cover rules of the 1.29.0 cover layer (design P2.2, the source matrix; section 6.3). Pure: every input
/// is a hash, a size or a flag the server already read, so each rule is unit-tested on drawn images or stored hashes
/// (no third-party art). Hash bounds are 1.28.0's <see cref="CoverHash"/>: Same &lt;= 10, uncertain 11-19, Different &gt;= 20.
/// </summary>
public static class CoverRules
{
    /// <summary>Page 1 is a jacket spread when width / height is at least this (single covers ~0.62-0.75, spreads ~1.25-1.5).</summary>
    public const double SpreadMinAspect = 1.2;

    /// <summary>True when page 1's size says it is a spread (unknown sizes are not).</summary>
    public static bool IsSpread(int? width, int? height) =>
        width is > 0 && height is > 0 && (double)width.Value / height.Value >= SpreadMinAspect;

    /// <summary>
    /// The FRONT half of a spread: an unfolded jacket reads back / spine / front for a left-to-right book (front on the
    /// right) and front / spine / back for a right-to-left book (front on the left).
    /// </summary>
    public static CoverCropSide FrontSide(CoverDirection direction) =>
        direction == CoverDirection.RightToLeft ? CoverCropSide.Left : CoverCropSide.Right;

    public static CoverCropSide Other(CoverCropSide side) => side == CoverCropSide.Left ? CoverCropSide.Right : CoverCropSide.Left;

    /// <summary>Hamming distance of two 64-bit hashes.</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>Same (&lt;= 10), Different (&gt;= 20) or no signal; unknown hashes give no signal.</summary>
    public static CoverVerdict Compare(ulong? a, ulong? b)
    {
        if (a is not { } x || b is not { } y)
            return CoverVerdict.NoSignal;
        var d = Distance(x, y);
        return d <= CoverHash.SameMaxDistance ? CoverVerdict.Same
            : d >= CoverHash.DifferentMinDistance ? CoverVerdict.Different
            : CoverVerdict.NoSignal;
    }

    /// <summary>
    /// A volume archive of a linked series, or an unlinked volume-like archive (web = null). L = the front-half crop
    /// when page 1 is a spread, else the file. No web volume N cover -> L (never the poster). L Same as W -> L; spread
    /// and the OTHER half Same as W -> the other half; uncertain -> L; Different -> W - unless W is the original-language
    /// fallback (1.30.0, owner): an English edition's cover differs from the Japanese one by design, so L is kept.
    /// </summary>
    /// <param name="spread">Page 1 is spread-shaped.</param>
    /// <param name="front">The front half by the book's direction.</param>
    /// <param name="localHash">Hash of L (crop or file); null when it could not be computed.</param>
    /// <param name="otherHalfHash">Spread only: hash of the other half.</param>
    /// <param name="web">The web volume N cover (preferred language, else origin), or null.</param>
    public static CoverDecision DecideVolume(bool spread, CoverCropSide front, ulong? localHash, ulong? otherHalfHash,
        WebCoverCandidate? web)
    {
        var local = spread ? CoverDecision.Crop(front, AutoCoverReason.Spread) : CoverDecision.File(AutoCoverReason.None);
        if (web is null)
            return spread ? local : CoverDecision.File(AutoCoverReason.NoWebCover);

        switch (Compare(localHash, web.Hash))
        {
            case CoverVerdict.Same:
                return local with { Reason = AutoCoverReason.FileMatchesWeb };
            case CoverVerdict.Different:
                if (spread && Compare(otherHalfHash, web.Hash) == CoverVerdict.Same)
                    return CoverDecision.Crop(Other(front), AutoCoverReason.SpreadOtherSide);
                // Only a cover in the preferred language can show that page 1 is not the cover.
                return web.OriginFallback
                    ? local with { Reason = AutoCoverReason.OtherLanguageKept }
                    : CoverDecision.FromWeb(web, AutoCoverReason.LocalNotCover);
            default:
                if (spread && Compare(otherHalfHash, web.Hash) == CoverVerdict.Same)
                    return CoverDecision.Crop(Other(front), AutoCoverReason.SpreadOtherSide);
                // Uncertain (or a hash is missing): the local cover is kept - another edition's logo on the same art.
                return local with { Reason = AutoCoverReason.UncertainKept };
        }
    }

    /// <summary>
    /// A linked one-shot (owner, 2026-09-29): the web cover by default - MangaDex volume 1 > MangaDex main > the stored
    /// MangaUpdates poster (the first given); the file only when page 1 already matches that cover (Same). Uncertain
    /// takes the web cover. No spread crop.
    /// </summary>
    public static CoverDecision DecideOneShot(ulong? fileHash, IEnumerable<WebCoverCandidate?> chain)
    {
        var web = chain.FirstOrDefault(c => c is not null);
        if (web is null)
            return CoverDecision.File(AutoCoverReason.NoWebCover);
        return Compare(fileHash, web.Hash) == CoverVerdict.Same
            ? CoverDecision.File(AutoCoverReason.FileMatchesWeb)
            : CoverDecision.FromWeb(web, AutoCoverReason.OneShotDefault);
    }

    /// <summary>
    /// A linked series folder (volumes, chapters or mixed). With a local volume 1 archive: THAT archive's resolved cover (1.30.0,
    /// owner soak test) - its own decision already compared it with the web volume 1 cover (Same / Different / uncertain / a
    /// cover only in the original language), so the folder shows exactly what volume 1's card shows, never the first file by
    /// name (chapter 1's page 1 in a <c>Series/Chapters/</c> layout). Without a local volume 1: web volume 1 > web main >
    /// stored poster > file.
    /// </summary>
    /// <param name="localVolume1">The live volume 1 archive below the folder, or null.</param>
    /// <param name="chapterFolder">The folder holds chapters and no volume archive (only the reason differs).</param>
    public static CoverDecision DecideSeriesFolder(long? localVolume1, bool chapterFolder,
        WebCoverCandidate? webVolume1, WebCoverCandidate? webMain, WebCoverCandidate? poster)
    {
        if (localVolume1 is { } archive)
            return CoverDecision.LocalVolume1(archive);

        var reason = chapterFolder ? AutoCoverReason.ChapterFolderDefault : AutoCoverReason.SeriesVolume1;
        if (webVolume1 is not null)
            return CoverDecision.FromWeb(webVolume1, reason);
        if (webMain is not null)
            return CoverDecision.FromWeb(webMain, reason);
        if (poster is not null)
            return CoverDecision.FromWeb(poster, reason);
        return CoverDecision.File(AutoCoverReason.NoWebCover);
    }

    /// <summary>A linked webtoon folder (tall strips): web main > stored poster > file. No cover matching.</summary>
    public static CoverDecision DecideWebtoon(WebCoverCandidate? webMain, WebCoverCandidate? poster)
    {
        if (webMain is not null)
            return CoverDecision.FromWeb(webMain, AutoCoverReason.WebtoonDefault);
        if (poster is not null)
            return CoverDecision.FromWeb(poster, AutoCoverReason.WebtoonDefault);
        return CoverDecision.File(AutoCoverReason.NoWebCover);
    }

    /// <summary>1.34.0: a "Collection about" folder: the stored poster of the series it is about, else the file default. No cover matching.</summary>
    public static CoverDecision DecideCollection(WebCoverCandidate? poster) =>
        poster is null ? CoverDecision.File(AutoCoverReason.NoWebCover) : CoverDecision.FromWeb(poster, AutoCoverReason.CollectionPoster);

    /// <summary>
    /// A Season / Part subfolder of a linked series: the web cover of the volume its first chapter belongs to (from the
    /// file name / ComicInfo or the exact volume list), else its first archive's resolved cover (the file default).
    /// </summary>
    public static CoverDecision DecideSubfolder(WebCoverCandidate? webOfFirstVolume) =>
        webOfFirstVolume is null
            ? CoverDecision.File(AutoCoverReason.NoWebCover)
            : CoverDecision.FromWeb(webOfFirstVolume, AutoCoverReason.SubfolderFirstVolume);

    /// <summary>
    /// The web cover of the language to use: the preferred language when stored, else the origin language (flagged for
    /// the re-check), else null. <paramref name="stored"/> holds (locale, candidate) pairs of ONE volume / kind.
    /// </summary>
    public static WebCoverCandidate? PickLanguage(IReadOnlyCollection<(string Locale, WebCoverCandidate Cover)> stored,
        string preferredLocale, IReadOnlyCollection<string> originLocales)
    {
        foreach (var (locale, cover) in stored)
            if (string.Equals(locale, preferredLocale, StringComparison.OrdinalIgnoreCase))
                return cover;
        foreach (var origin in originLocales)
            foreach (var (locale, cover) in stored)
                if (string.Equals(locale, origin, StringComparison.OrdinalIgnoreCase))
                    return cover with { OriginFallback = true };
        return null;
    }

    /// <summary>MangaDex locale codes of a record's origin (the covers the background pass lists besides the preferred ones).</summary>
    public static IReadOnlyList<string> OriginLocales(MetadataOrigin? origin) => origin switch
    {
        MetadataOrigin.Japan => ["ja"],
        MetadataOrigin.Korea => ["ko"],
        MetadataOrigin.ChinaTaiwan => ["zh", "zh-hk"],
        MetadataOrigin.EnglishOriginal => ["en"],
        null => ["ja", "ko", "zh", "zh-hk"],
        _ => [],
    };
}
