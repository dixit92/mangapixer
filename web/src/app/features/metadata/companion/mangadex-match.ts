import { CompanionDto, CompanionState } from '../../../core/api/api-types';

/** What the MangaDex companion state means, in words (the "Change MangaDex match..." dialog). */
export function companionStateText(state: CompanionState | null | undefined): string {
  switch (state) {
    case 'Auto':
      return 'Found automatically: its own MangaUpdates link names this series.';
    case 'Confirmed':
      return 'Chosen by an admin.';
    case 'NotFound':
      return 'Not found on MangaDex; checked again on the refresh schedule.';
    case 'None':
      return 'Marked "Not on MangaDex": never looked up there.';
    case 'Failed':
      return 'The last check failed; it is tried again later.';
    default:
      return 'Not checked yet.';
  }
}

/** The MangaDex companion among the node's companions, or null. */
export function mangaDexCompanion(companions: readonly CompanionDto[] | null | undefined): CompanionDto | null {
  return companions?.find((c) => c.provider === 'mangadex') ?? null;
}

/** A pasted MangaDex reference the server will accept: a title id, or a mangadex.org/title/... address. */
export function looksLikeMangaDexReference(text: string): boolean {
  const t = text.trim();
  const uuid = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;
  return /^[0-9a-f-]{36}$/i.test(t) ? uuid.test(t) : /^(https?:\/\/)?(www\.)?mangadex\.org\/title\//i.test(t) && uuid.test(t);
}
