import { FolderMatchArtistDto, FolderMatchKind, FolderMatchRecordDto, FolderMatchRowDto, SeriesLinkState } from '../../../core/api/api-types';
import { ARTIST_ROLE_OPTIONS } from '../artist-folder/artist-folder-labels';

/** "Match folders by name" (1.38.0): the texts of the dialog, kept pure for the tests. */

/** The most web searches one run sends (owner, 2026-10-09). */
export const MAX_WEB_SEARCHES = 50;

/** At least this long between two web searches (paced 1 per second, sequential). */
export const WEB_SEARCH_PACE_MS = 1000;

const STATE_LABELS: Record<SeriesLinkState, string> = {
  Confirmed: 'linked (confirmed)',
  Auto: 'linked (automatic)',
  NeedsReview: 'waiting in Needs review',
  DontMatch: "Don't match",
  CollectionAbout: 'Collection about',
  ArtistFolder: 'Artist folder',
};

export function roleLabel(role: string): string {
  return ARTIST_ROLE_OPTIONS.find((o) => o.value === role)?.label ?? role;
}

export function artistChoiceLabel(a: FolderMatchArtistDto): string {
  const as = a.matchedName !== a.name ? ` (as ${a.matchedName})` : '';
  const site = a.provider === 'gcd' ? ', Grand Comics Database' : '';
  return `${a.name}${as} - ${roleLabel(a.role)}, ${plural(a.recordCount, 'series', 'series')}${site}`;
}

export function recordChoiceLabel(r: FolderMatchRecordDto): string {
  const details = [r.year ? String(r.year) : null, r.providerType ?? null, r.linkedAsSeries ? 'linked here' : null]
    .filter((x): x is string => !!x);
  const alt = r.matchedTitle !== r.title ? ` (as ${r.matchedTitle})` : '';
  return `${r.title}${alt}${details.length ? ' - ' + details.join(', ') : ''}`;
}

/** The one-line status of a preview row. */
export function statusText(row: FolderMatchRowDto, kind: FolderMatchKind): string {
  const matches = kind === 'Artists' ? (row.artists ?? []).length : (row.records ?? []).length;
  switch (row.status) {
    case 'Proposed':
      return kind === 'Artists' ? 'Artist folder:' : 'Collection about:';
    case 'Ambiguous':
      return `${matches} matches - pick one, or skip this folder`;
    case 'NoMatch':
      return kind === 'Artists' ? 'No known artist has this name' : 'No stored series has this title';
    case 'Decided':
      return `Already ${row.currentState ? STATE_LABELS[row.currentState] : 'decided'} - ticking it replaces that`;
    case 'NotAFolder':
      return 'Not a folder - skipped';
    case 'NotFound':
      return 'Not found - skipped';
  }
}

const RESULT_TEXT: Record<string, string> = {
  ok: 'Marked',
  not_found: 'Not found',
  not_a_folder: 'Only a folder can be marked',
  duplicate: 'Listed twice',
  creator_name_invalid: "The artist's name is not valid",
  creator_role_invalid: 'Unknown role',
  creators_too_many: 'The folder already declares the most creators',
  record_not_stored: 'The series record is no longer stored',
  invalid_request: 'Not valid',
};

export function resultText(code: string, message?: string): string {
  return RESULT_TEXT[code] ?? message ?? code;
}

/** The snackbar after the dialog closed. */
export function appliedMessage(kind: FolderMatchKind, marked: number, failed: number, queued: number): string {
  const what = kind === 'Artists' ? 'artist folder' : 'collection';
  let text = `${plural(marked, what, what + 's')} marked`;
  if (failed) text += `, ${failed} not`;
  if (queued) text += ` - ${plural(queued, 'work', 'works')} inside will be matched`;
  return text;
}

export function plural(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`;
}

/** Web-search errors that stop the run (switched off, budget, busy, backoff, the provider failing); others fail one row. */
export function stopsTheRun(status: number | undefined): boolean {
  return status === undefined || status === 0 || status === 409 || status === 429 || status >= 500;
}
