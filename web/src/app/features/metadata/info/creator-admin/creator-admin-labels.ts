import { ApiError } from '../../../../core/api/api-types';
import { CreatorDto } from '../info-types';

const NAME_SOURCES: Record<string, string> = {
  record: 'web record',
  providerAuthor: 'author record',
  comicInfo: 'ComicInfo',
  declared: 'declared',
  admin: 'set by an admin',
};

export function nameSourceLabel(source: string): string {
  return NAME_SOURCES[source] ?? source;
}

const PROVIDERS: Record<string, string> = { mangaupdates: 'MangaUpdates' };

/** "MangaUpdates author 12345" from lane F's `mangaupdates:12345` (no request is made to show it). */
export function providerIdLabel(id: string): string {
  const colon = id.indexOf(':');
  if (colon < 0) return id;
  const provider = id.slice(0, colon);
  return `${PROVIDERS[provider] ?? provider} author ${id.slice(colon + 1)}`;
}

/** "3 works · MangaUpdates author 12345": one line about a creator, for the merge picker. */
export function creatorSummary(c: CreatorDto): string {
  const works = c.works ?? 0;
  const parts = [`${works} work${works === 1 ? '' : 's'}`];
  for (const id of c.providerIds ?? []) parts.push(providerIdLabel(id));
  return parts.join(' · ');
}

/** A creator admin error in words: lane F's codes first, then its message. */
export function creatorErrorText(e: unknown): string {
  const err = e as (Partial<ApiError> & { status?: number }) | null;
  switch (err?.error) {
    case 'not_found': return 'This creator no longer exists. Close the panel and open it again.';
    case 'same_creator': return 'A creator cannot be merged into itself.';
    case 'empty_selection': return 'Choose at least one name or id to move.';
    case 'split_all': return 'Keep at least one name or id on this creator.';
    case 'not_on_creator': return 'Something you chose is no longer on this creator. Close the dialog and try again.';
    case 'invalid': return 'Enter a name of at most 256 characters.';
  }
  if (err?.status === 403) return 'Only an admin can change creators.';
  return err?.message || 'The change was not saved.';
}
