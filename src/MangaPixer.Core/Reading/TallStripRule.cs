namespace com.lifepixer.mangapixer.Core.Reading;

/// <summary>
/// "Are these pages webtoon-shaped tall strips?" (shared since 1.41.0; the rule itself dates from 1.24.0). One rule for Identify's
/// local facts, automatic matching and the admin's reading-mode suggestions:
/// <list type="bullet">
/// <item>a page is tall when its height is at least <see cref="Ratio"/> times its width;</item>
/// <item>the sample is the first <see cref="SamplePages"/> MEASURED pages (width and height known, width above zero) in archive-id, then
/// page order;</item>
/// <item>fewer than <see cref="MinMeasuredPages"/> measured pages = unknown (null);</item>
/// <item>otherwise tall strips when at least half of the sample is tall.</item>
/// </list>
/// Pure; the database sample is <c>TallStripSampler</c> (Server).
/// </summary>
public static class TallStripRule
{
    /// <summary>Height-to-width ratio from which a page counts as a tall strip.</summary>
    public const double Ratio = 2.0;

    /// <summary>At most this many measured pages are looked at.</summary>
    public const int SamplePages = 400;

    /// <summary>Below this many measured pages the answer is unknown.</summary>
    public const int MinMeasuredPages = 20;

    /// <summary>Whether one page of <paramref name="width"/> x <paramref name="height"/> is a tall strip.</summary>
    public static bool IsTall(int width, int height) => height >= width * Ratio;

    /// <summary>
    /// The verdict for a sample of <paramref name="measured"/> pages of which <paramref name="tall"/> are tall: null below
    /// <see cref="MinMeasuredPages"/>, else whether at least half are tall.
    /// </summary>
    public static bool? Classify(int measured, int tall) =>
        measured < MinMeasuredPages ? null : tall * 2 >= measured;

    /// <summary>The verdict for already-sampled page sizes (at most <see cref="SamplePages"/>, measured ones only).</summary>
    public static bool? Classify(IReadOnlyCollection<(int Width, int Height)> sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return Classify(sample.Count, sample.Count(p => IsTall(p.Width, p.Height)));
    }
}
