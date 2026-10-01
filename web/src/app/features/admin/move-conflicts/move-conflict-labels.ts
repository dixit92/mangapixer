import { MoveConflictDto, MoveConflictKind, MoveConflictSideDto, MoveConflictState, ReaderMode, SeriesLinkState } from '../../../core/api/api-types';

/** Labels of the Move conflicts page (1.31.0). Pure. */
export const MOVE_CONFLICT_KIND_LABELS: Record<MoveConflictKind, string> = {
  Progress: 'Reading position',
  ReaderSettings: 'Reader settings',
  SeriesLink: 'Series link',
};

export const MOVE_CONFLICT_STATE_LABELS: Record<MoveConflictState, string> = {
  Open: 'Open',
  Overwritten: 'Old state used',
  Kept: 'New state kept',
};

const READER_MODE_LABELS: Record<ReaderMode, string> = {
  PagedLtr: 'Left to right',
  PagedRtl: 'Right to left',
  DoubleSpread: 'Double page',
  VerticalWebtoon: 'Webtoon',
};

const LINK_STATE_LABELS: Record<SeriesLinkState, string> = {
  Confirmed: 'confirmed',
  Auto: 'automatic',
  NeedsReview: 'in review',
  DontMatch: "Don't match",
};

/** One side of a conflict in words, e.g. "Page 12 of 40", "Finished", "Right to left", "Some Title (confirmed)". */
export function sideText(kind: MoveConflictKind, side: MoveConflictSideDto): string {
  switch (kind) {
    case 'Progress': {
      if (!side.present || side.progress === 'Unread') return 'Not started';
      if (side.progress === 'Completed') return 'Finished';
      return side.pageCount ? `Page ${side.page} of ${side.pageCount}` : `Page ${side.page}`;
    }
    case 'ReaderSettings': {
      if (!side.present) return 'Default settings';
      const mode = side.readerMode ? READER_MODE_LABELS[side.readerMode] : null;
      if (mode && side.otherReaderSettings) return `${mode} + other settings`;
      return mode ?? (side.otherReaderSettings ? 'Custom settings' : 'Default settings');
    }
    case 'SeriesLink': {
      if (!side.present || !side.linkState) return 'Not linked';
      if (side.linkState === 'DontMatch') return "Don't match";
      return `${side.recordTitle ?? 'Unknown record'} (${LINK_STATE_LABELS[side.linkState]})`;
    }
  }
}

/** Who the conflict is about: the user, or "Admin" for a series link. */
export function whoText(c: MoveConflictDto): string {
  return c.kind === 'SeriesLink' ? 'Admin (series link)' : (c.userName ?? 'A removed user');
}

/** "Moved from Ongoing" (+ the old name when the item was renamed on the way). */
export function fromText(c: MoveConflictDto): string {
  return c.fromTitle && c.fromTitle !== c.title ? `Moved from ${c.fromLibraryName} (was ${c.fromTitle})` : `Moved from ${c.fromLibraryName}`;
}

/** The result line after a bulk or single resolve. */
export function resolvedSentence(resolved: number, skipped: number): string {
  const head = resolved === 1 ? '1 conflict resolved.' : `${resolved} conflicts resolved.`;
  return skipped > 0 ? `${head} ${skipped} already resolved or gone.` : head;
}
