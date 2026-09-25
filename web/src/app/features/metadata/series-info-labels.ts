import {
  MetadataFormat,
  MetadataOrigin,
  MetadataOriginStatus,
  MetadataPrecedence,
  MetadataPrecedenceSource,
  SeriesInfoDto,
} from '../../core/api/api-types';

/** Display labels for the series-metadata vocabulary (1.24.0). Pure; unit-tested. */

const ORIGIN_LABELS: Record<MetadataOrigin, string> = {
  Japan: 'Japan',
  Korea: 'Korea',
  ChinaTaiwan: 'China / Taiwan',
  EnglishOriginal: 'English original',
  Philippines: 'Philippines',
  Indonesia: 'Indonesia',
  Thailand: 'Thailand',
  Vietnam: 'Vietnam',
  Malaysia: 'Malaysia',
  Nordic: 'Nordic',
  French: 'French',
  Spanish: 'Spanish',
  German: 'German',
  Other: 'Other origin',
};

const FORMAT_LABELS: Record<MetadataFormat, string> = {
  Comic: 'Comic',
  Novel: 'Novel',
  Artbook: 'Artbook',
  Doujinshi: 'Doujinshi',
  Audio: 'Audio drama',
};

const STATUS_LABELS: Record<MetadataOriginStatus, string> = {
  Unknown: '',
  Ongoing: 'Ongoing',
  Complete: 'Complete',
  Hiatus: 'On hiatus',
  Cancelled: 'Cancelled',
};

export const PRECEDENCE_LABELS: Record<MetadataPrecedence, string> = {
  WebFirst: 'Web first',
  ComicInfoFirst: 'ComicInfo first',
};

const PRECEDENCE_SOURCE_LABELS: Record<MetadataPrecedenceSource, string> = {
  Default: 'default',
  Library: 'set for the library',
  Folder: 'set on a folder',
};

const ROLE_LABELS: Record<string, string> = {
  writer: 'Story',
  author: 'Author',
  artist: 'Art',
  penciller: 'Art',
  inker: 'Inks',
  colorist: 'Colors',
  letterer: 'Letters',
  coverArtist: 'Cover',
  editor: 'Editor',
  translator: 'Translation',
};

export function originLabel(origin: MetadataOrigin | null | undefined): string {
  return origin ? ORIGIN_LABELS[origin] ?? '' : '';
}

export function formatLabel(format: MetadataFormat | null | undefined): string {
  return format ? FORMAT_LABELS[format] ?? '' : '';
}

export function roleLabel(role: string): string {
  return ROLE_LABELS[role] ?? 'Other';
}

/**
 * The one-line facts under the title: "Japan . Comic . 1989 . Ongoing, 43 vols . Webtoon".
 * Empty parts are skipped.
 */
export function metaLine(info: SeriesInfoDto): string {
  const parts: string[] = [];
  const origin = originLabel(info.origin);
  if (origin) parts.push(origin);
  const format = formatLabel(info.format);
  if (format && info.format !== 'Comic') parts.push(format);
  if (info.startYear) parts.push(String(info.startYear));
  const status = info.originStatus ? STATUS_LABELS[info.originStatus] : '';
  if (status && info.originVolumes) parts.push(`${status}, ${info.originVolumes} vol${info.originVolumes === 1 ? '' : 's'}`);
  else if (status) parts.push(status);
  else if (info.originVolumes) parts.push(`${info.originVolumes} vols`);
  if (info.webtoon === true) parts.push('Webtoon');
  return parts.join(' · ');
}

/** "Web first (set on a folder)" style description of the effective precedence. */
export function precedenceLabel(info: Pick<SeriesInfoDto, 'precedence' | 'precedenceSource'>): string {
  return `${PRECEDENCE_LABELS[info.precedence]} (${PRECEDENCE_SOURCE_LABELS[info.precedenceSource]})`;
}

/** "Vol 3 . #12" for an archive's own ComicInfo. */
export function itemLine(item: { number?: string | null; volume?: number | null; year?: number | null } | null | undefined): string {
  if (!item) return '';
  const parts: string[] = [];
  if (item.volume !== null && item.volume !== undefined) parts.push(`Vol ${item.volume}`);
  if (item.number) parts.push(`#${item.number}`);
  if (item.year) parts.push(String(item.year));
  return parts.join(' · ');
}

/** Relative age of a timestamp for "fetched N days ago" (stable for tests via `now`). */
export function ageLabel(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return '';
  const then = Date.parse(iso);
  if (Number.isNaN(then)) return '';
  const days = Math.floor((now - then) / 86_400_000);
  if (days <= 0) return 'today';
  if (days === 1) return '1 day ago';
  return `${days} days ago`;
}

/** Whether the state carries something to show (the top-bar button and the (i) use it). */
export function hasSeriesContent(info: Pick<SeriesInfoDto, 'state'> | null | undefined): boolean {
  return !!info && info.state !== 'None' && info.state !== 'DontMatch';
}
