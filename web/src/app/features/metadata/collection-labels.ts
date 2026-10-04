import { CollectionAboutResultDto, SeriesInfoDto } from '../../core/api/api-types';

/**
 * "Collection about" (1.34.0): a folder of works about one series (fan works). The folder shows the series as context; nothing below
 * it inherits the series, and the works inside are matched on their own.
 */
export function collectionLabel(title: string | null | undefined): string {
  return `Collection about ${title?.trim() || 'a series'}`;
}

/** The folder's own series information is a collection (not an inherited one, not a series link). */
export function isOwnCollection(info: Pick<SeriesInfoDto, 'state'> | null | undefined): boolean {
  return info?.state === 'CollectionAbout';
}

/** The snackbar after marking a folder: what happened to its Content and its works. */
export function collectionResultMessage(title: string | null | undefined, result: CollectionAboutResultDto | null | undefined): string {
  const parts = [collectionLabel(title)];
  const queued = result?.queued ?? 0;
  if (queued > 0) parts.push(`${queued} work${queued === 1 ? '' : 's'} inside will be matched`);
  else parts.push('the works inside are matched when automatic matching runs');
  if (result?.contentSet) parts.push('Content set to Doujinshi & adult one-shots');
  return parts.join(' - ');
}
