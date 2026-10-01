import {
  MatchLevel,
  MetadataFlagReason,
  MetadataFlagState,
  MetadataFolderContent,
  MetadataMatchRunDto,
  MetadataMatchRunTrigger,
  MetadataReviewCandidateDto,
  MetadataReviewSummaryDto,
  MetadataReviewTab,
  WorkClass,
} from '../../../core/api/api-types';
import { formatLabel } from '../series-info-labels';

/** Display helpers for the admin metadata page (stage 2): tabs, reasons, runs, flags. Pure; unit-tested. */

/**
 * The Metadata Manager page's own tabs (`/admin/metadata`, renamed from "Series
 * metadata", owner decision 1, 1.27.0). Shared with the summary tile that sits above
 * them (`MetadataSummaryTileComponent`) so its in-page stats can switch to one without a
 * circular import between the two components.
 */
export type AdminMetadataTab = 'settings' | 'review' | 'flags' | 'runs' | 'missing' | 'official';
/** In tab order; 1.30.0 appends Official releases after Missing (the indexes of the others never move). */
export const ADMIN_METADATA_TABS: readonly AdminMetadataTab[] = ['settings', 'review', 'flags', 'runs', 'missing', 'official'];

export interface ReviewTabDef {
  tab: MetadataReviewTab;
  label: string;
  /** The summary field holding this tab's count. */
  count: keyof MetadataReviewSummaryDto;
  /** One line under the tab bar explaining what the tab lists. */
  hint: string;
}

export const REVIEW_TABS: readonly ReviewTabDef[] = [
  { tab: 'NeedsReview', label: 'Needs review', count: 'needsReview',
    hint: 'Close calls the matcher did not link on its own. Pick the right candidate, identify, or mark Don\'t match.' },
  { tab: 'AutoLinked', label: 'Auto-linked', count: 'autoLinked',
    hint: 'Linked automatically and already shown to readers, newest first. Confirm, unlink or change them.' },
  { tab: 'Unmatched', label: 'Unmatched', count: 'unmatched',
    hint: 'No good candidate, mixed folders and loose archives. Identify them by hand or mark Don\'t match.' },
  { tab: 'Flags', label: 'Flags', count: 'openFlags',
    hint: 'Series a reader reported as wrong.' },
  { tab: 'DontMatch', label: 'Don\'t match', count: 'dontMatch',
    hint: 'Folders and archives marked "not one series". Nothing inside them is matched automatically.' },
  { tab: 'Confirmed', label: 'Confirmed', count: 'confirmed',
    hint: 'Links an admin made or confirmed. Automatic matching never changes these.' },
  { tab: 'MissingFolders', label: 'Missing folders', count: 'missingFolders',
    hint: 'Links and declared facts left on folders that were renamed or moved where MangaPixer could not follow them. Re-attach or delete.' },
];

export function reviewTabDef(tab: MetadataReviewTab): ReviewTabDef {
  return REVIEW_TABS.find((t) => t.tab === tab) ?? REVIEW_TABS[0];
}

/** Why an item is in review: chip label + tooltip. Unknown codes show as-is (a newer server). */
const REASONS: Record<string, { label: string; tip: string }> = {
  close_second: { label: 'Close second', tip: 'The runner-up scored almost as high as the top candidate.' },
  count: { label: 'Count', tip: 'Your item count does not fit the record\'s volumes or chapters.' },
  year: { label: 'Year', tip: 'The years in your files do not fit the record\'s start year.' },
  type: { label: 'Type', tip: 'The record\'s type (novel, artbook, ...) does not fit this folder.' },
  related_pair: { label: 'Related series', tip: 'The candidates are related series (a sequel, spin-off or adaptation).' },
  one_shot: { label: 'One-shot', tip: 'A one-shot needs a near-exact title to link automatically.' },
  author: { label: 'Author', tip: 'An author in your files differs from the record\'s authors.' },
  number: { label: 'Numbered', tip: 'A number in the folder name does not match the record.' },
  review_only: { label: 'Review only', tip: 'This folder shape is never linked automatically (a collection or an archive group).' },
  // The declared type as evidence (1.30.0): a strong hint both ways, never a block.
  declared_type: { label: 'Fits declared type', tip: 'The record comes from the country the declared type names (for example Manhwa: Korea). Counts in its favour.' },
  not_declared_type: { label: 'Not declared type', tip: 'The record is not the type declared for this folder. Counts against it, but never blocks a link.' },
  reach: { label: 'Reach', tip: 'What the folder holds does not fit this series: its volumes or chapters go far past the record, or its volume numbers disagree with the series\' volume list.' },
  // Series families (1.30.0, owner): a main series and its spin-offs are easy to mix up; never linked on a subtitle alone.
  subtitle_family: { label: 'Spin-off or main story?', tip: 'Only the subtitle in the folder name tells this series apart from another one of the same family (its main story or a spin-off). Check which one the folder holds.' },
  series_family: { label: 'Series family', tip: 'Another series of the same family also matched this name (its main story, a spin-off, side story, prequel or sequel). Check which one the folder holds.' },
};

export function reasonLabel(code: string): string {
  return REASONS[code]?.label ?? code.replace(/_/g, ' ');
}

export function reasonTip(code: string): string {
  return REASONS[code]?.tip ?? '';
}

export const WORK_CLASS_LABELS: Record<WorkClass, string> = {
  Excluded: 'Excluded',
  Series: 'Series',
  SeriesWithUnits: 'Series with volume folders',
  OneShot: 'One-shot',
  CollectionLeaf: 'Collection',
  ArtistCollection: 'Artist folder',
  FranchiseContainer: 'Franchise folder',
  CollectionContainer: 'Collection folder',
  Wrapper: 'Wrapper folder',
  Mixed: 'Mixed',
  UnitSub: 'Volume folder',
  Ambiguous: 'Unclear shape',
};

export function workClassLabel(value: WorkClass | null | undefined): string {
  return value ? WORK_CLASS_LABELS[value] ?? value : '';
}

export const MATCH_LEVEL_LABELS: Record<MatchLevel, string> = {
  None: 'Not matched',
  Folder: 'Folder match',
  Archive: 'Archive match',
  ReviewOnly: 'Review only',
};

/** "Manga · 2014 · 14 vols" for a stored candidate. */
export function reviewCandidateLine(c: Pick<MetadataReviewCandidateDto, 'providerType' | 'format' | 'year' | 'volumes'>): string {
  const parts: string[] = [];
  const type = c.providerType || formatLabel(c.format);
  if (type) parts.push(type);
  if (c.year) parts.push(String(c.year));
  if (c.volumes) parts.push(`${c.volumes} vol${c.volumes === 1 ? '' : 's'}`);
  return parts.join(' · ');
}

/**
 * A 0-1 score as a whole percent (owner decision, 1.27.0: percent everywhere a score is
 * shown, matching the Identify dialog). `null` for a missing or invalid score.
 */
export function scorePercent(value: number | null | undefined): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? Math.round(Math.max(0, Math.min(1, value)) * 100) : null;
}

/** "92%" for a review row's score, or '' when there is none. */
export function scorePercentLabel(value: number | null | undefined): string {
  const p = scorePercent(value);
  return p === null ? '' : `${p}%`;
}

/**
 * A review row shows the matcher's ADJUSTED overall score (title match plus item count,
 * year, type and origin evidence) - a different number from the Identify dialog's
 * title-only match score (owner decision, 1.27.0). The tooltip spells out both so an
 * admin is never left guessing which is which.
 */
export function overallScoreTip(c: { titleScore?: number | null; adjustedScore?: number | null }): string {
  const title = scorePercentLabel(c.titleScore) || 'unknown';
  const overall = scorePercentLabel(c.adjustedScore) || 'unknown';
  return `Overall ${overall}: title match ${title}, adjusted for item count, year, type and origin evidence.`;
}

export const FLAG_REASON_LABELS: Record<MetadataFlagReason, string> = {
  WrongSeries: 'Wrong series',
  WrongDetails: 'Wrong details',
  NotOneSeries: 'Not one series',
  Other: 'Something else',
};

/** The reader-facing wording in the flag dialog. */
export const FLAG_REASON_OPTIONS: readonly { value: MetadataFlagReason; label: string; hint: string }[] = [
  { value: 'WrongSeries', label: 'This is a different series', hint: 'The title, cover or description belongs to another series.' },
  { value: 'WrongDetails', label: 'Right series, wrong details', hint: 'For example the wrong edition, a novel instead of the comic, or a sequel.' },
  { value: 'NotOneSeries', label: 'This folder is not one series', hint: 'An anthology, an artist\'s folder or a mix of series.' },
  { value: 'Other', label: 'Something else', hint: 'Tell the admin in the note.' },
];

export const FLAG_STATE_LABELS: Record<MetadataFlagState, string> = {
  Open: 'Open',
  Relinked: 'Re-identified',
  Unlinked: 'Unlinked',
  DontMatch: 'Marked Don\'t match',
  Dismissed: 'Dismissed',
};

export const FLAG_NOTE_MAX = 500;

export const FOLDER_CONTENT_LABELS: Record<MetadataFolderContent, string> = {
  Auto: 'Auto',
  DoujinshiAndAdultOneShots: 'Doujinshi & adult one-shots',
  NotDoujinshi: 'Not doujinshi',
};

export const FOLDER_CONTENT_HINTS: Record<MetadataFolderContent, string> = {
  Auto: 'Automatic matching leaves doujinshi out of its searches.',
  DoujinshiAndAdultOneShots: 'Automatic matching also searches doujinshi in this folder and below.',
  NotDoujinshi: 'Never search doujinshi here, even below a doujinshi folder.',
};

export const RUN_TRIGGER_LABELS: Record<MetadataMatchRunTrigger, string> = {
  Scan: 'After a scan',
  Bulk: 'Match library now',
  Retry: 'Retry unmatched',
  Rerun: 'Re-run matching',
};

/** 0-100 progress of a run (processed of queued). */
export function runProgress(run: Pick<MetadataMatchRunDto, 'processed' | 'queued'>): number {
  if (!run.queued) return 0;
  return Math.min(100, Math.round((run.processed / run.queued) * 100));
}

/** Why automatic work is waiting, in words. */
const WAITING: Record<string, string> = {
  automatic_off: 'Automatic matching is off.',
  metadata_disabled: 'Fetching from the web is off.',
  metadata_network_disabled: 'Web lookups are disabled by the server configuration.',
  budget_exhausted: 'Today\'s request budget is spent. Automatic work continues after 00:00 UTC, or raise the budget.',
  provider_backoff: 'MangaUpdates asked MangaPixer to slow down.',
  provider_not_allowed: 'MangaUpdates is off the provider allowlist. Add it back in Settings to continue.',
};

export function waitingLabel(code: string | null | undefined): string {
  if (!code) return '';
  return WAITING[code] ?? `Waiting (${code}).`;
}

/** "about 3 days" for an estimate. */
export function daysLabel(days: number): string {
  if (!Number.isFinite(days) || days <= 0) return 'no requests needed';
  if (days <= 1) return 'within a day';
  const whole = Math.ceil(days);
  return `about ${whole} days`;
}

export function plural(n: number, noun: string, pluralNoun = `${noun}s`): string {
  return `${n.toLocaleString('en-US')} ${n === 1 ? noun : pluralNoun}`;
}
