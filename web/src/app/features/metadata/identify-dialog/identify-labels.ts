import { IdentifyPreviewDto, MatchStrength, MetadataOriginStatus } from '../../../core/api/api-types';
import { formatLabel, originLabel } from '../series-info-labels';

/** Display helpers for the identify dialog (1.24.0, lane B2). Pure; unit-tested. */

export const STRENGTH_LABELS: Record<MatchStrength, string> = {
  Strong: 'Strong',
  Possible: 'Possible',
  Weak: 'Weak',
};

const STATUS_WORDS: Record<MetadataOriginStatus, string> = {
  Unknown: '',
  Ongoing: 'ongoing',
  Complete: 'complete',
  Hiatus: 'on hiatus',
  Cancelled: 'cancelled',
};

/** "Manga . Japan . 1989" for a candidate: the raw provider type, the origin when it adds something, the year. */
export function candidateLine(c: { providerType?: string | null; origin?: Parameters<typeof originLabel>[0]; year?: number | null }): string {
  const parts: string[] = [];
  if (c.providerType) parts.push(c.providerType);
  const origin = originLabel(c.origin);
  if (origin && origin.toLowerCase() !== (c.providerType ?? '').toLowerCase()) parts.push(origin);
  if (c.year) parts.push(String(c.year));
  return parts.join(' · ');
}

/**
 * "Manga · Japan · 1989 · 43 vols, ongoing" for a preview record: the provider type
 * like the results list (the normalized format only when the provider gave no type).
 */
export function previewLine(p: IdentifyPreviewDto): string {
  const parts: string[] = [];
  const type = p.providerType || formatLabel(p.format);
  if (type) parts.push(type);
  const origin = originLabel(p.origin);
  if (origin && origin.toLowerCase() !== type.toLowerCase()) parts.push(origin);
  if (p.startYear) parts.push(String(p.startYear));
  const status = p.originStatus ? STATUS_WORDS[p.originStatus] : '';
  if (p.originVolumes && status) parts.push(`${p.originVolumes} vol${p.originVolumes === 1 ? '' : 's'}, ${status}`);
  else if (p.originVolumes) parts.push(`${p.originVolumes} vol${p.originVolumes === 1 ? '' : 's'}`);
  else if (status) parts.push(status);
  return parts.join(' · ');
}

/** Local tall-strip signal as text. */
export function tallStripsLabel(value: boolean | null | undefined): string {
  if (value === true) return 'tall strips (webtoon-shaped pages)';
  if (value === false) return 'regular pages';
  return 'not measured yet';
}

/** "Try again after 14:05" for a backoff error, or '' when no time is known. */
export function retryLabel(detail: string | null | undefined, locale?: string): string {
  if (!detail) return '';
  const at = new Date(detail);
  if (Number.isNaN(at.getTime())) return '';
  return `Try again after ${at.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })}.`;
}

/** Percent score, 0-100. */
export function scorePercent(score: number): number {
  return Math.round(Math.max(0, Math.min(1, score)) * 100);
}
