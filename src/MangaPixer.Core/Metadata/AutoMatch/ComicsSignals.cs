namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

using System.Numerics;

/// <summary>
/// The local signs that a work is a Western comic / graphic novel / BD album (1.32.0 step 0; owner 2026-09-30: comic-like
/// works are searched on the Grand Comics Database first, else MangaUpdates). Read from local data only - nothing is sent to
/// compute them. Lane A (comics signals) adds the detectors; lane B (GCD provider) routes on <see cref="ComicsSignal.RoutesToComics"/>.
/// </summary>
[Flags]
public enum ComicsSignalKind
{
    None = 0,

    /// <summary>An admin declared the folder Comic or Graphic novel (strong).</summary>
    DeclaredType = 1 << 0,

    /// <summary>An ancestor folder is a comics category word - Comics, BD, Fumetti... (strong).</summary>
    CategoryFolder = 1 << 1,

    /// <summary>ComicInfo <c>Web</c> / <c>Notes</c> names a Comic Vine, Metron or GCD record (strong).</summary>
    ComicsIdInComicInfo = 1 << 2,

    /// <summary>ComicInfo <c>Publisher</c> is a known Western comics publisher (strong).</summary>
    WesternPublisher = 1 << 3,

    /// <summary>Archive names numbered like comic issues: <c>#12</c>, <c>Issue 12</c>, <c>12 (of 6)</c>, <c>Annual 2</c> (weak).</summary>
    IssueNumbering = 1 << 4,

    /// <summary>Archive names numbered like BD / European albums: <c>Tome 3</c>, <c>T03</c>, <c>Band 3</c>, <c>Deel 3</c> (weak).</summary>
    AlbumNumbering = 1 << 5,

    /// <summary>A <c>(YYYY)</c> start year right after the series name, the comics taggers' convention (weak).</summary>
    StartYearAfterName = 1 << 6,

    /// <summary>Collected-edition format words: TPB, HC, OGN, Omnibus, Integrale... (weak).</summary>
    CollectedFormatWord = 1 << 7,
}

/// <summary>The comics signs found for one work.</summary>
public sealed record ComicsSignal(ComicsSignalKind Kinds)
{
    public static ComicsSignal None { get; } = new(ComicsSignalKind.None);

    public const ComicsSignalKind Strong = ComicsSignalKind.DeclaredType | ComicsSignalKind.CategoryFolder
        | ComicsSignalKind.ComicsIdInComicInfo | ComicsSignalKind.WesternPublisher;

    public const ComicsSignalKind Weak = ComicsSignalKind.IssueNumbering | ComicsSignalKind.AlbumNumbering
        | ComicsSignalKind.StartYearAfterName | ComicsSignalKind.CollectedFormatWord;

    /// <summary>
    /// Whether the work is searched on the comics site first. One strong sign is enough; weak signs route only two
    /// together - alone they are scoring evidence. (1.32.0 kickoff measurement on the owner's live library: the only
    /// folders with <c>#N</c>-numbered files were manga, so issue numbering alone must not send a manga name to GCD.)
    /// </summary>
    public bool RoutesToComics => (Kinds & Strong) != 0 || BitOperations.PopCount((uint)(Kinds & Weak)) >= 2;
}

/// <summary>What <see cref="ComicsSignals.Of"/> reads - display names and stored local metadata only, never paths.</summary>
public sealed record ComicsSignalInput(
    DeclaredType? DeclaredType,
    string? CategoryHint,
    IReadOnlyList<string> ArchiveNames,
    string? ComicInfoPublisher = null,
    IReadOnlyList<string>? ComicInfoWebUrls = null,
    string? ComicInfoNotes = null,
    int? MedianPageCount = null);

/// <summary>Detects <see cref="ComicsSignal"/>s. Pure and deterministic.</summary>
public static class ComicsSignals
{
    /// <summary>
    /// The comics signs of a work. Step 0 (stub): the declared type and the existing <c>comic</c> / <c>comics</c> category
    /// words only; lane A adds the other detectors (and the wider category word list).
    /// </summary>
    public static ComicsSignal Of(ComicsSignalInput input)
    {
        var kinds = ComicsSignalKind.None;
        if (input.DeclaredType is Metadata.DeclaredType.Comic or Metadata.DeclaredType.GraphicNovel)
            kinds |= ComicsSignalKind.DeclaredType;
        if (IsComicsCategory(input.CategoryHint))
            kinds |= ComicsSignalKind.CategoryFolder;
        return kinds == ComicsSignalKind.None ? ComicsSignal.None : new ComicsSignal(kinds);
    }

    /// <summary>Whether a category hint (an ancestor folder's whole name) names comics.</summary>
    public static bool IsComicsCategory(string? categoryHint) =>
        TitleNormalizer.ScoringForm(categoryHint) is "comic" or "comics";
}
