namespace com.lifepixer.mangapixer.Core.Metadata.Reach;

using com.lifepixer.mangapixer.Core.Api;

// The one answer of the Completion tab (1.32.0, owner-approved wording): "which series can I move to a library of finished series".
// PURE, applied by SeriesProgress.Evaluate to every result, so the tab, the completion mark, the Missing report and the Volumes view
// read the same answer.

/// <summary>
/// Turns a <see cref="ProgressResult"/> into its <see cref="SeriesAnswer"/>. "Finished" always needs the original run to have ended
/// (complete or cancelled where it comes from); "missing" keeps its 1.29.0 meaning (released in the preferred language). Rules, in order:
/// <list type="number">
/// <item>numbering restarts -> Can't tell; no numbers -> Can't tell, except a one-shot (ended, one volume, at least one archive), which
/// the folder holds whole;</item>
/// <item>a Complete collection -> Finished - you have it all;</item>
/// <item>ended, and the finished edition is not all here or something released is missing -> Finished - missing some;</item>
/// <item>nothing known about releases in the language -> Can't tell;</item>
/// <item>something released is missing (the series still runs) -> Missing some;</item>
/// <item>a folder of volumes of a running series with no volume total in the language -> Can't tell (nothing was compared);</item>
/// <item>otherwise -> Everything released so far (running, on hiatus, status unknown, or ended with the language edition still
/// coming / dropped).</item>
/// </list>
/// </summary>
public static class SeriesAnswers
{
    /// <summary>
    /// The folder collects VOLUMES: it holds volume files and no chapter file outside them (chapter files a volume file here already
    /// holds do not count).
    /// </summary>
    public static bool CollectsVolumes(ReachResult reach)
    {
        ArgumentNullException.ThrowIfNull(reach);
        return reach.VolumeFiles.Count > 0 && !reach.ChapterFiles.Any(c => c > 0 && !reach.Overlap.Contains(c));
    }

    /// <summary>The result with its answer; <paramref name="archives"/> is the number of archives in the series scope.</summary>
    public static ProgressResult Apply(ProgressResult r, int archives)
    {
        ArgumentNullException.ThrowIfNull(r);
        var f = r.Facts;
        if (r.Restarts)
            return With(r, SeriesAnswer.CantTell, SeriesAnswerReason.NumberingRestarts);
        if (!r.Reach.HasNumbers)
        {
            // A one-shot: one volume, the run ended, its file carries no number (extras beside it do not matter).
            if (f.OriginEnded && f.OriginVolumes == 1 && archives > 0)
            {
                return r with
                {
                    Completion = SeriesCompletion.CompleteCollection,
                    CompletionBasis = CompletionBasis.OriginRun,
                    CompletionTarget = 1,
                    CompletionHeld = 1,
                    CompletionInChapters = false,
                    Answer = SeriesAnswer.HaveItAll,
                    AnswerReason = SeriesAnswerReason.OneShot,
                };
            }
            return With(r, SeriesAnswer.CantTell, SeriesAnswerReason.NoNumbers);
        }
        if (r.Completion == SeriesCompletion.CompleteCollection)
            return With(r, SeriesAnswer.HaveItAll, SeriesAnswerReason.None);
        if (f.OriginEnded && (r.Completion == SeriesCompletion.FinishedNotHeld || r.AnythingMissing))
            return With(r, SeriesAnswer.FinishedMissing, SeriesAnswerReason.None);
        if (!f.ReleaseKnown)
            return With(r, SeriesAnswer.CantTell, SeriesAnswerReason.NothingKnownReleased);

        var running = f.OriginStatus switch
        {
            MetadataOriginStatus.Ongoing => SeriesAnswerReason.Running,
            MetadataOriginStatus.Hiatus => SeriesAnswerReason.OnHiatus,
            _ => SeriesAnswerReason.StatusUnknown,
        };
        if (r.AnythingMissing)
            return With(r, SeriesAnswer.MissingSome, running);
        if (!f.OriginEnded && CollectsVolumes(r.Reach) && r.VolumeTotal is null)
            return With(r, SeriesAnswer.CantTell, SeriesAnswerReason.NoVolumeTotal);
        if (f.OriginEnded)
        {
            return With(r, SeriesAnswer.UpToDate, f.OfficialStatus == MetadataOriginStatus.Cancelled && f.OfficialVolumes is > 0
                ? SeriesAnswerReason.LanguageEditionDropped
                : SeriesAnswerReason.WaitingForLanguage);
        }
        return With(r, SeriesAnswer.UpToDate, running);
    }

    private static ProgressResult With(ProgressResult r, SeriesAnswer answer, SeriesAnswerReason reason) =>
        r with { Answer = answer, AnswerReason = reason };
}
