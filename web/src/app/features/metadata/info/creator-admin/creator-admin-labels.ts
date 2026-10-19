import { ApiError } from '../../../../core/api/api-types';
import { CreatorDto, CreatorNameSourceName, CreatorProviderIdDto } from '../info-types';

const NAME_SOURCES: Record<CreatorNameSourceName, string> = {
  Record: 'web record',
  ProviderAuthor: 'author record',
  ComicInfo: 'ComicInfo',
  Declared: 'declared',
  Admin: 'set by an admin',
};

export function nameSourceLabel(source: CreatorNameSourceName): string {
  return NAME_SOURCES[source] ?? source;
}

const PROVIDERS: Record<string, string> = { mangaupdates: 'MangaUpdates' };

/** "MangaUpdates author 12345": provider ids as an admin reads them (no request is made to show them). */
export function providerIdLabel(id: CreatorProviderIdDto): string {
  return `${PROVIDERS[id.provider] ?? id.provider} author ${id.externalId}`;
}

/** "3 works · MangaUpdates author 12345": one line about a creator, for the merge picker. */
export function creatorSummary(c: CreatorDto): string {
  const parts = [`${c.works} work${c.works === 1 ? '' : 's'}`];
  for (const id of c.providerIds) parts.push(providerIdLabel(id));
  return parts.join(' · ');
}

/** A creator admin error in words: the server's codes first, then its message. */
export function creatorErrorText(e: unknown): string {
  const err = e as (Partial<ApiError> & { status?: number }) | null;
  switch (err?.error) {
    case 'not_found': return 'This creator no longer exists. Close the panel and open it again.';
    case 'same_creator': return 'A creator cannot be merged into itself.';
    case 'nothing_to_split': return 'Choose at least one name or id to move, and keep at least one.';
    case 'invalid_name': return 'Enter a name of 1 to 200 characters.';
  }
  if (err?.status === 403) return 'Only an admin can change creators.';
  return err?.message || 'The change was not saved.';
}
