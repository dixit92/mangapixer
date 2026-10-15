import { TrackerAccountDto, TrackerEntryDto, TrackerEntryState, TrackerEntryStatus } from '../../core/api/api-types';

/**
 * Tracker sync (1.43.0) words and small helpers shared by the trackers pages. No request is made here.
 */

/** The trackers this client knows, in the order the pages show them. */
export const TRACKER_SLUGS = ['mal', 'mangaupdates'] as const;

/**
 * Consent text versions this client SHOWS (`tracker-consent-text.component.ts`); each must equal the server's current version
 * (`TrackerConsent.AdminVersion` / `UserVersion`). The client sends its own version back, so a server that moved on refuses a
 * consent given to an older text. Bump with the text.
 * DRAFT (1.43.0, lane U): the texts are lane E's DRAFT wording until the integrator applies the owner-approved text.
 */
export const TRACKER_ADMIN_CONSENT_VERSION: Record<string, number> = { mal: 1, mangaupdates: 1 };
export const TRACKER_USER_CONSENT_VERSION: Record<string, number> = { mal: 1, mangaupdates: 1 };

/** The user-facing docs pages (the project's docs site; opened only when the user clicks, without a referrer). */
export const TRACKER_SETUP_DOCS_URL = 'https://mangapixer.com/tracker-setup/';
export const TRACKER_SYNC_DOCS_URL = 'https://mangapixer.com/tracker-sync/';

/** The SPA route MyAnimeList redirects back to after the sign-in (`app.routes.ts`). */
export const TRACKER_CALLBACK_PATH = '/trackers/callback/';

export function trackerName(slug: string | null | undefined): string {
  switch (slug) {
    case 'mal': return 'MyAnimeList';
    case 'mangaupdates': return 'MangaUpdates';
    default: return slug ?? 'the tracker';
  }
}

/** The redirect URL to register for this server, from the address the admin's browser uses right now. */
export function proposedRedirectUri(slug: string, origin: string = globalThis.location?.origin ?? ''): string {
  return `${origin}${TRACKER_CALLBACK_PATH}${slug}`;
}

/** How a user's access ends on each tracker (MangaUpdates: logout; MyAnimeList: no revoke endpoint and no user page - owner A5, 2026-10-14). */
export function revokeHint(slug: string): string {
  return slug === 'mangaupdates'
    ? 'Disconnecting also signs MangaPixer out on MangaUpdates. Changing your MangaUpdates password ends its sign-ins too.'
    : 'Disconnecting deletes MangaPixer\'s sign-in from this server, so it can no longer be used. MyAnimeList has no page to remove an app\'s access; the old sign-in there simply expires.';
}

/** An entry's state in words, for the caller's list. */
export function entryStateText(entry: Pick<TrackerEntryDto, 'state' | 'errorCode' | 'tracker' | 'remoteStatus'>): string {
  const name = trackerName(entry.tracker);
  switch (entry.state) {
    case 'Ok': return 'Up to date';
    case 'TrackerAhead': return `${name} is ahead - nothing sent`;
    case 'LowerHere': return `Lower here than what was sent - ${name} is not lowered`;
    case 'Held': return 'Waiting for you to press Send';
    case 'NoId': return `No ${name} number for this series - an admin can set it`;
    case 'DontSync': return 'Not synced (your choice)';
    case 'Error': return `Last sync failed${entry.errorCode ? ` (${errorText(entry.errorCode)})` : ''}`;
    case 'NotOnList': return `Not on your ${name} list`;
    case 'LeftAsIs': return `Left as is: ${statusText(entry.remoteStatus) ?? 'not Reading'} on ${name}`;
    case 'Waiting': return `${name} asked to slow down - it is tried again soon`;
    case 'Pending': return 'Not checked yet';
    default: return entry.state;
  }
}

/** The states that ask the user to look (shown first). */
export const ATTENTION_STATES: readonly TrackerEntryState[] = ['Held', 'Error', 'LowerHere', 'NoId', 'Waiting'];

export function statusText(status: TrackerEntryStatus | null | undefined): string | null {
  switch (status) {
    case 'Reading': return 'Reading';
    case 'Completed': return 'Completed';
    case 'OnHold': return 'On hold';
    case 'Dropped': return 'Dropped';
    case 'PlanToRead': return 'Plan to read';
    case 'Rereading': return 'Rereading';
    default: return null;
  }
}

/** "12 ch · 2 vol", or null when neither is known. */
export function numbersText(chapters: number | null | undefined, volumes: number | null | undefined): string | null {
  const parts: string[] = [];
  if (chapters != null) parts.push(`${chapters} ch`);
  if (volumes != null) parts.push(`${volumes} vol`);
  return parts.length ? parts.join(' · ') : null;
}

/** The account's state in words; null when it syncs normally. */
export function accountProblem(account: TrackerAccountDto, slug: string): string | null {
  const name = trackerName(slug);
  if (account.state === 'DisconnectedByAdmin') return `An admin disconnected this ${name} account. Connect again to sync.`;
  if (account.state === 'NeedsReconnect') return `${name} no longer accepts MangaPixer's sign-in. Connect again to sync.`;
  if (account.consentRenewalNeeded) return 'What MangaPixer sends has changed. Read the new text and connect again to resume syncing.';
  return null;
}

/** Server error codes (`ApiError.error`) in plain words. */
export function errorText(code: string | null | undefined, slug?: string): string {
  const name = trackerName(slug);
  switch (code) {
    case 'invalid_state': return 'This sign-in is not valid any more (it was used, it expired, or it was started in another browser session). Start again from Settings.';
    case 'connect_rejected': return `${name} did not accept this sign-in. Start again from Settings.`;
    case 'tracker_unavailable': return `${name} could not be reached from this server. Try again later.`;
    case 'tracker_not_allowed': return `An admin has not allowed ${name} on this server.`;
    case 'not_configured': return `An admin has not finished setting up ${name} on this server yet.`;
    case 'consent_required': return 'This page shows an older text than the server. Reload the page and read the text again.';
    case 'invalid_credentials': return 'Enter your username and password.';
    case 'not_connected': return 'Connect your account first.';
    case 'needs_reconnect': return 'Connect your account again.';
    case 'not_syncing': return 'Your account cannot sync right now.';
    case 'invalid_numbers': return 'Give a chapter or volume number from 0 to 100000.';
    case 'invalid_remote_id': return 'Enter the series number or the series address on the tracker.';
    case 'not_linked': return 'Link the folder to a series first.';
    case 'invalid_redirect_uri': return 'Enter the full redirect URL (http or https) exactly as registered.';
    case 'invalid_client_id': return 'The Client ID is too long or not valid.';
    case 'invalid_client_secret': return 'The client secret is too long or not valid.';
    case 'rate_limited': return 'asked to slow down';
    case 'rejected': return 'refused';
    case 'unavailable': return 'not reachable';
    case 'key_ring': return 'the server\'s keys changed';
    case 'expired': return 'the sign-in expired';
    default: return code ?? 'unknown error';
  }
}

/** The `error` code of an HttpErrorResponse carrying an `ApiError`, if any. */
export function apiErrorCode(err: unknown): string | null {
  const body = (err as { error?: { error?: unknown } } | null)?.error;
  return typeof body?.error === 'string' ? body.error : null;
}

/** Plain words for a failed request: the server's code when known, else its message, else a generic line. */
export function failureText(err: unknown, slug?: string): string {
  const code = apiErrorCode(err);
  if (code) return errorText(code, slug);
  const status = (err as { status?: number } | null)?.status;
  if (status === 401) return 'Your session ended. Sign in again.';
  if (status === 403) return 'You are not allowed to do this.';
  return 'Something went wrong. Try again.';
}

/**
 * The tracker's series number from what an admin pastes: the number itself, or the series address on the tracker's site
 * (MyAnimeList `myanimelist.net/manga/<n>/...`; MangaUpdates `mangaupdates.com/series/<base36>/...`, the site's form of the
 * numeric id, as `MangaUpdatesReference` reads it on the server). Returns the decimal id, '' for an empty field (= remove the
 * admin's id), or null when it cannot be read. Parsed locally - nothing is fetched.
 */
export function parseTrackerId(slug: string, input: string): string | null {
  const text = input.trim();
  if (text === '') return '';
  if (/^\d{1,19}$/.test(text)) return BigInt(text) > 0n ? BigInt(text).toString() : null;
  const candidate = text.includes('://') ? text : `https://${text}`;
  let url: URL;
  try {
    url = new URL(candidate);
  } catch {
    return null;
  }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') return null;
  const host = url.hostname.toLowerCase();
  const segments = url.pathname.split('/').filter((s) => s.length > 0);
  if (slug === 'mal') {
    if (host !== 'myanimelist.net' && host !== 'www.myanimelist.net') return null;
    return segments.length >= 2 && segments[0].toLowerCase() === 'manga' && /^\d{1,19}$/.test(segments[1]) && BigInt(segments[1]) > 0n
      ? BigInt(segments[1]).toString()
      : null;
  }
  if (slug === 'mangaupdates') {
    if (host !== 'mangaupdates.com' && host !== 'www.mangaupdates.com') return null;
    if (segments.length >= 2 && segments[0].toLowerCase() === 'series') return decodeBase36(segments[1]);
    return null;
  }
  return null;
}

/** A base36 token (0-9a-z, at most 13 characters) as a positive decimal id within a signed 64-bit range, else null. */
export function decodeBase36(token: string): string | null {
  const t = token.toLowerCase();
  if (t.length === 0 || t.length > 13 || !/^[0-9a-z]+$/.test(t)) return null;
  let value = 0n;
  for (const c of t) value = value * 36n + BigInt(parseInt(c, 36));
  return value > 0n && value <= 9223372036854775807n ? value.toString() : null;
}
