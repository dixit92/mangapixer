namespace com.lifepixer.mangapixer.Core.Reading;

/// <summary>
/// "Near the end counts as finished" (1.27.0). One rule decides whether a saved page
/// position is at the end of an archive; it drives BOTH the sticky read-mark set on
/// completion and the open position of a read archive, so a read archive that reopens
/// at page 1 always carries its read-mark and vice versa.
///
/// A position counts as at the end when at most <c>n</c> pages follow it, where
/// <c>n = min(5, max(1, floor(pageCount / 20)))</c> - one page, or 5% of the pages
/// rounded DOWN, capped at five pages so a long archive never skips real content.
/// Examples: 19/20 and 195/200 are at the end, 190/200 is not. The first page of an
/// archive with two or more pages is never at the end (opening a two-page archive
/// does not finish it); a one-page archive is finished by opening it.
///
/// Positions are zero-based page indices. In the double-page view the saved index of
/// a spread is its LAST page, so a spread counts once any of its pages is past the line.
/// </summary>
public static class NearEndRule
{
    /// <summary>The most pages that may follow a position still counted as the end.</summary>
    public const int MaxPagesAfter = 5;

    /// <summary>
    /// How many pages may follow a position that still counts as the end, for an
    /// archive of <paramref name="pageCount"/> pages. Zero for an unknown or empty
    /// archive and for one or two pages (only the last page counts there).
    /// </summary>
    public static int PagesAfterAllowed(int pageCount)
    {
        if (pageCount <= 0)
            return 0;
        var n = Math.Min(MaxPagesAfter, Math.Max(1, pageCount / 20));
        // Never reach back to the first page of a multi-page archive.
        return Math.Max(0, Math.Min(n, pageCount - 2));
    }

    /// <summary>
    /// The lowest zero-based page index that counts as the end, or null when the page
    /// count is unknown (the end cannot be resolved).
    /// </summary>
    public static int? FirstEndIndex(int? pageCount)
        => pageCount is int pc && pc > 0 ? pc - 1 - PagesAfterAllowed(pc) : null;

    /// <summary>
    /// True when the zero-based <paramref name="pageIndex"/> counts as the end of an
    /// archive of <paramref name="pageCount"/> pages. False when the page count is
    /// unknown or zero. An index past the last page (defensive) counts as the end.
    /// </summary>
    public static bool IsAtEnd(int pageIndex, int? pageCount)
        => FirstEndIndex(pageCount) is int first && pageIndex >= first;
}
