import { TrackerActionBlock, TrackerActionDto, TrackerBatchAction, TrackerSelectableSeriesDto, TrackerSeriesRowDto } from '../../core/api/api-types';
import { actionBlockText, trackerName } from './tracker-texts';

/**
 * The Tracking list's Select mode (1.43.2): what the action bar needs to know of each selected series - which of the batch actions
 * apply to it - whether the row is loaded (a list row) or came from "Select all in this chip" (the server's ids route). Pure: no
 * request, no state.
 */
export interface SelectFlags {
  fromTracker: TrackerActionDto;
  toTracker: TrackerActionDto;
  /** "Sync now" reaches the tracker: a known id and sync not turned off. */
  canSync: boolean;
  dontSync: boolean;
  /** 1.44.0: the tracker's own chapters complete more volumes than it lists (the row offers to raise them). */
  volumesFromChapters: boolean;
}

/** A loaded row's flags (the same rule as the row's own "Sync now" item). */
export function flagsOfRow(r: TrackerSeriesRowDto): SelectFlags {
  return {
    fromTracker: r.fromTracker,
    toTracker: r.toTracker,
    canSync: !r.dontSync && r.bucket !== 'NoId' && !!r.remoteId,
    dontSync: r.dontSync,
    volumesFromChapters: r.volumesFromChapters != null && !r.dontSync,
  };
}

export function flagsOfSelectable(s: TrackerSelectableSeriesDto): SelectFlags {
  return { fromTracker: s.fromTracker, toTracker: s.toTracker, canSync: s.canSync, dontSync: s.dontSync,
    volumesFromChapters: s.volumesFromChapters != null && !s.dontSync };
}

/** Whether a batch action applies to a series (the server checks again on its own stored state). */
export function applies(action: TrackerBatchAction, f: SelectFlags): boolean {
  switch (action) {
    case 'FromTracker': return f.fromTracker.possible;
    case 'ToTracker': return f.toTracker.possible;
    case 'SyncNow': return f.canSync;
    case 'DontSync': return !f.dontSync;
    case 'SyncAgain': return f.dontSync;
    case 'VolumesFromChapters': return f.volumesFromChapters;
    default: return false;
  }
}

/** Whether an action contacts the tracker (then it needs an account that can sync, and it is sent in small chunks). */
export function reachesTracker(action: TrackerBatchAction): boolean {
  return action === 'FromTracker' || action === 'ToTracker' || action === 'SyncNow' || action === 'VolumesFromChapters';
}

/** The most frequent block among the series an action does not apply to. */
function commonBlock(flags: readonly SelectFlags[], pick: (f: SelectFlags) => TrackerActionDto): TrackerActionBlock | null {
  const counts = new Map<TrackerActionBlock, number>();
  for (const f of flags) {
    const a = pick(f);
    if (!a.possible) counts.set(a.block, (counts.get(a.block) ?? 0) + 1);
  }
  let best: TrackerActionBlock | null = null;
  let most = 0;
  for (const [block, n] of counts) {
    if (n > most) { best = block; most = n; }
  }
  return best;
}

/**
 * Why an action applies to none of the selected series (a visible line under the bar - a touch screen has no hover); null while it
 * applies to at least one.
 */
export function noneReason(action: TrackerBatchAction, flags: readonly SelectFlags[], canSync: boolean, slug: string): string | null {
  if (flags.length === 0) return null;
  if (flags.some((f) => applies(action, f)) && (canSync || !reachesTracker(action))) return null;
  if (reachesTracker(action) && !canSync) return 'Your account cannot sync right now';
  const name = trackerName(slug);
  switch (action) {
    case 'FromTracker':
    case 'ToTracker': {
      const block = commonBlock(flags, (f) => (action === 'FromTracker' ? f.fromTracker : f.toTracker));
      const why = block ? actionBlockText(block, action === 'FromTracker' ? 'from' : 'to', slug) : null;
      return why ? `None of the selected (${why})` : 'None of the selected';
    }
    case 'SyncNow':
      return flags.every((f) => f.dontSync) ? 'None of the selected syncs (you turned it off)' : `None of the selected has a ${name} id`;
    case 'DontSync':
      return 'Every selected series is already not synced';
    case 'SyncAgain':
      return 'None of the selected is turned off';
    case 'VolumesFromChapters':
      return `None of the selected (${name}'s volumes already match its chapters)`;
    default:
      return null;
  }
}
