namespace com.lifepixer.mangapixer.Core.Reading;

using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>How sure a reading-mode suggestion is (1.41.0). Serialized by name.</summary>
public enum ReadingModeConfidence
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>Which local signal a reading-mode suggestion rests on (1.41.0). Serialized by name.</summary>
public enum ReadingModeSignal
{
    /// <summary>The pages are webtoon-shaped tall strips (<see cref="TallStripRule"/>).</summary>
    TallPages = 0,

    /// <summary>Most archives' ComicInfo says <c>Manga = YesAndRightToLeft</c>.</summary>
    ComicInfo = 1,

    /// <summary>The linked series record (a hint): webtoon / Korean or Chinese origin, or Japanese origin.</summary>
    LinkedRecord = 2,
}

/// <summary>The local evidence of one folder (or one linked series and its subfolders).</summary>
/// <param name="MeasuredPages">Measured pages in the tall-strip sample.</param>
/// <param name="TallPages">Of those, tall strips.</param>
/// <param name="ComicInfoRightToLeft">Archives whose parsed ComicInfo says <c>Manga = YesAndRightToLeft</c>.</param>
/// <param name="ComicInfoLeftToRight">Archives whose parsed ComicInfo says <c>Manga = No</c>.</param>
/// <param name="ComicInfoStated">Archives whose parsed ComicInfo states <c>Manga</c> at all (No, Yes or YesAndRightToLeft).</param>
/// <param name="LinkedOrigin">The linked series record's origin, when the folder is a linked series (Confirmed / Auto).</param>
/// <param name="LinkedWebtoon">The linked series record's webtoon flag.</param>
public sealed record ReadingModeEvidence(
    int MeasuredPages,
    int TallPages,
    int ComicInfoRightToLeft,
    int ComicInfoLeftToRight,
    int ComicInfoStated,
    MetadataOrigin? LinkedOrigin,
    bool? LinkedWebtoon);

/// <summary>A suggested reader mode with its confidence and the signal it rests on.</summary>
public sealed record ReadingModeVerdict(ReaderMode Mode, ReadingModeConfidence Confidence, ReadingModeSignal Signal);

/// <summary>
/// Reading-mode suggestions (1.41.0, admin): which mode a folder's LOCAL evidence points to, and how sure that is. Pure. Signals:
/// <list type="bullet">
/// <item>pages (P): <see cref="TallStripRule"/> over the sample - tall / not tall / unknown;</item>
/// <item>ComicInfo (C): right-to-left when <c>YesAndRightToLeft</c> is more than half of the archives that state <c>Manga</c>;
/// left-to-right when <c>No</c> is; <c>Yes</c> alone says manga, not a direction;</item>
/// <item>linked record (R, a hint): <c>Webtoon = true</c>, or (webtoon not denied) Korean / Chinese origin = vertical; Japanese origin
/// (not marked a webtoon) = right-to-left.</item>
/// </list>
/// The first signal that speaks picks the mode: P tall = Vertical (webtoon); else C right-to-left = Right to left; else R. Base
/// confidence: P High when at least <see cref="HighTallShare"/> of the sample is tall, else Medium; C Medium; R Low. Every OTHER signal
/// that agrees raises it one level (at most High), every one that disagrees lowers it one level; below Low there is no suggestion.
/// </summary>
public static class ReadingModeSuggestionRules
{
    /// <summary>Share of tall pages from which the page signal alone is High (below it, but tall by the rule: Medium).</summary>
    public const double HighTallShare = 0.8;

    private enum Leaning
    {
        None,
        Vertical,
        RightToLeft,
        LeftToRight,
    }

    /// <summary>The suggestion for <paramref name="evidence"/>, or null when nothing points anywhere (or the signals cancel out).</summary>
    public static ReadingModeVerdict? Suggest(ReadingModeEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var pages = TallStripRule.Classify(evidence.MeasuredPages, evidence.TallPages);
        var comicInfo = ComicInfoLeaning(evidence);
        var record = RecordLeaning(evidence.LinkedOrigin, evidence.LinkedWebtoon);

        ReaderMode mode;
        ReadingModeSignal signal;
        int level;
        if (pages == true)
        {
            mode = ReaderMode.VerticalWebtoon;
            signal = ReadingModeSignal.TallPages;
            level = evidence.TallPages >= evidence.MeasuredPages * HighTallShare
                ? (int)ReadingModeConfidence.High
                : (int)ReadingModeConfidence.Medium;
        }
        else if (comicInfo == Leaning.RightToLeft)
        {
            mode = ReaderMode.PagedRtl;
            signal = ReadingModeSignal.ComicInfo;
            level = (int)ReadingModeConfidence.Medium;
        }
        else if (record is Leaning.Vertical or Leaning.RightToLeft)
        {
            mode = record == Leaning.Vertical ? ReaderMode.VerticalWebtoon : ReaderMode.PagedRtl;
            signal = ReadingModeSignal.LinkedRecord;
            level = (int)ReadingModeConfidence.Low;
        }
        else
        {
            return null;
        }

        if (signal != ReadingModeSignal.TallPages)
            level += PagesVote(mode, pages);
        if (signal != ReadingModeSignal.ComicInfo)
            level += Vote(mode, comicInfo);
        if (signal != ReadingModeSignal.LinkedRecord)
            level += Vote(mode, record);

        if (level < (int)ReadingModeConfidence.Low)
            return null;
        return new ReadingModeVerdict(mode, (ReadingModeConfidence)Math.Min(level, (int)ReadingModeConfidence.High), signal);
    }

    /// <summary>
    /// The linked record's leaning: Vertical for a webtoon, or a Korean / Chinese work not marked "not a webtoon"; right-to-left for a
    /// Japanese work not marked a webtoon; nothing otherwise.
    /// </summary>
    private static Leaning RecordLeaning(MetadataOrigin? origin, bool? webtoon)
    {
        if (webtoon == true)
            return Leaning.Vertical;
        if (webtoon != false && origin is MetadataOrigin.Korea or MetadataOrigin.ChinaTaiwan)
            return Leaning.Vertical;
        if (origin == MetadataOrigin.Japan)
            return Leaning.RightToLeft;
        return Leaning.None;
    }

    private static Leaning ComicInfoLeaning(ReadingModeEvidence e)
    {
        if (e.ComicInfoStated <= 0)
            return Leaning.None;
        if (e.ComicInfoRightToLeft * 2 > e.ComicInfoStated)
            return Leaning.RightToLeft;
        if (e.ComicInfoLeftToRight * 2 > e.ComicInfoStated)
            return Leaning.LeftToRight;
        return Leaning.None;
    }

    /// <summary>Tall pages agree with Vertical; measured pages that are not tall disagree with it; for Right to left they are neutral.</summary>
    private static int PagesVote(ReaderMode mode, bool? pages) => (mode, pages) switch
    {
        (ReaderMode.VerticalWebtoon, true) => 1,
        (ReaderMode.VerticalWebtoon, false) => -1,
        (ReaderMode.PagedRtl, true) => -1,
        _ => 0,
    };

    private static int Vote(ReaderMode mode, Leaning leaning) => (mode, leaning) switch
    {
        (_, Leaning.None) => 0,
        (ReaderMode.VerticalWebtoon, Leaning.Vertical) => 1,
        (ReaderMode.VerticalWebtoon, Leaning.RightToLeft) => -1,
        (ReaderMode.VerticalWebtoon, Leaning.LeftToRight) => 0,
        (ReaderMode.PagedRtl, Leaning.RightToLeft) => 1,
        (ReaderMode.PagedRtl, Leaning.Vertical or Leaning.LeftToRight) => -1,
        _ => 0,
    };
}
