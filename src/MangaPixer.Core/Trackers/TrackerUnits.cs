namespace com.lifepixer.mangapixer.Core.Trackers;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>The units of one archive as tracker sync reads them, and whether it is the series' one-shot (see <see cref="TrackerUnits"/>).</summary>
public readonly record struct TrackerUnit(UnitNumbers Units, bool OneShot);

/// <summary>
/// The ONE place tracker sync reads the units of a series' archives (1.43.0; owner bug on the review instance, 2026-10-15): the units of
/// the Volumes view (<see cref="VolumeGrouping.WithSeriesHeads"/> + <see cref="VolumeGrouping.UnitsOf"/>), plus the ONE-SHOT rule - an
/// archive whose name states no number and that is the ONLY archive of the series' scope (an archive linked as its own series, or a linked
/// folder holding exactly one archive) counts as chapter 1 and volume 1. Unnumbered archives next to other archives (extras, <c>000.cbz</c>)
/// stay uncounted. Every tracker reader goes through it: the progress MangaPixer sends (<see cref="TrackerProgress"/>), "Set progress from
/// &lt;tracker&gt;" and the card / folder ring (<see cref="TrackerReadOn"/>). The Volumes view and the Missing report do not (unchanged).
/// </summary>
public static class TrackerUnits
{
    /// <summary>What a one-shot counts as: chapter 1 of volume 1.</summary>
    public static readonly UnitNumbers OneShotUnits = new(1, null, 1, null, false);

    /// <summary>The units of <paramref name="rows"/> (the series' scope: every row, folders included, in order), one per row.</summary>
    public static IReadOnlyList<TrackerUnit> Of(IReadOnlyList<GroupingRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var withHeads = VolumeGrouping.WithSeriesHeads(rows);
        var units = withHeads.Select(VolumeGrouping.UnitsOf).ToList();
        var archives = Enumerable.Range(0, rows.Count).Where(i => rows[i].Kind == GroupingRowKind.Archive).ToList();
        var oneShot = archives.Count == 1 && units[archives[0]].IsEmpty ? archives[0] : -1;
        return units.Select((u, i) => i == oneShot ? new TrackerUnit(OneShotUnits, true) : new TrackerUnit(u, false)).ToList();
    }
}
