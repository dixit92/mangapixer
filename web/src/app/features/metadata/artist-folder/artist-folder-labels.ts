import { ArtistFolderResultDto, SeriesInfoDto } from '../../../core/api/api-types';

/**
 * "Artist folder" (1.37.0): a folder an admin marked as one artist's works. It is never linked to a series, nothing inside inherits a
 * link from it, and automatic matching keeps working inside it - each work on its own. The artist is the folder's declared creator
 * (compared on the server only, never sent).
 */

/** The roles an artist can have (the declared creator roles, without "any role"). Same labels as the series credits. */
export const ARTIST_ROLE_OPTIONS: readonly { value: string; label: string }[] = [
  { value: 'author', label: 'Story & art' },
  { value: 'writer', label: 'Story' },
  { value: 'artist', label: 'Art' },
];

export const ARTIST_FOLDER_TIP =
  'One artist\'s works: the folder is never linked to a series, and each work inside is matched on its own with the artist as a hint';

/** The folder's own series information is an artist folder (not inherited). */
export function isOwnArtistFolder(info: Pick<SeriesInfoDto, 'state'> | null | undefined): boolean {
  return info?.state === 'ArtistFolder';
}

/** "Artist folder: Name" - the artist when known. */
export function artistFolderLabel(artist: string | null | undefined): string {
  const name = artist?.trim();
  return name ? `Artist folder: ${name}` : 'Artist folder';
}

/** The snackbar after marking a folder: the artist and what happens to the works inside. */
export function artistFolderResultMessage(result: ArtistFolderResultDto | null | undefined): string {
  const parts = [artistFolderLabel(result?.artist?.name)];
  const queued = result?.queued ?? 0;
  if (queued > 0) parts.push(`${queued} work${queued === 1 ? '' : 's'} inside will be matched`);
  else parts.push('the works inside are matched when automatic matching runs');
  return parts.join(' - ');
}
