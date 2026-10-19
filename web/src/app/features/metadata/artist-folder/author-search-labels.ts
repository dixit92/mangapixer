import { AuthorSearchCandidateDto } from '../../../core/api/api-types';

/** Author search by name (1.45.0): the texts of the candidate lists, kept pure for the tests. */

/** The most candidates a list shows (the provider's first page, best first). */
export const MAX_AUTHOR_CANDIDATES = 8;

/** The most genres a candidate shows. */
export const MAX_AUTHOR_GENRES = 3;

/** "Name (as Other name)" - the other name only when the search matched one. */
export function authorCandidateName(c: AuthorSearchCandidateDto): string {
  return c.hitName && c.hitName !== c.name ? `${c.name} (as ${c.hitName})` : c.name;
}

/** "12 series - Drama, Romance, Comedy" (series count and up to three genres; empty when MangaUpdates gives neither). */
export function authorCandidateDetails(c: AuthorSearchCandidateDto): string {
  const parts: string[] = [];
  if (c.totalSeries !== null && c.totalSeries !== undefined) parts.push(`${c.totalSeries} series`);
  const genres = (c.genres ?? []).slice(0, MAX_AUTHOR_GENRES);
  if (genres.length) parts.push(genres.join(', '));
  return parts.join(' - ');
}

/** A link is shown only for an https address on MangaUpdates' own site (the server already keeps only those). */
export function authorPageUrl(c: AuthorSearchCandidateDto): string | null {
  const url = c.siteUrl ?? '';
  return /^https:\/\/(www\.)?mangaupdates\.com\//.test(url) ? url : null;
}
