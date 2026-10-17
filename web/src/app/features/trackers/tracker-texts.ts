import {
  TrackerAccountDto,
  TrackerNotSynced,
  TrackerActionBlock,
  TrackerBatchAction,
  TrackerBatchSkip,
  TrackerEntryDto,
  TrackerEntryState,
  TrackerEntryStatus,
  TrackerListBucket,
  TrackerListCountsDto,
  TrackerPreviewDto,
  TrackerSeriesRowDto,
} from '../../core/api/api-types';

/**
 * Tracker sync (1.43.0) words and small helpers shared by the trackers pages. No request is made here.
 */

/** The trackers this client knows, in the order the pages show them. */
export const TRACKER_SLUGS = ['mal', 'mangaupdates'] as const;

/**
 * Consent text versions this client SHOWS (`tracker-consent-text.component.ts`); each must equal the server's current version
 * (`TrackerConsent.AdminVersion` / `UserVersion`). The client sends its own version back, so a server that moved on refuses a
 * consent given to an older text. Bump with the text.
 * 1.43.0: version 1 of each stream is the owner-approved text (2026-10-15).
 */
export const TRACKER_ADMIN_CONSENT_VERSION: Record<string, number> = { mal: 1, mangaupdates: 1 };
export const TRACKER_USER_CONSENT_VERSION: Record<string, number> = { mal: 1, mangaupdates: 1 };

/** The user-facing docs pages (the project's docs site; opened only when the user clicks, without a referrer). */
export const TRACKER_SETUP_DOCS_URL = 'https://mangapixer.com/tracker-setup/';
export const TRACKER_SYNC_DOCS_URL = 'https://mangapixer.com/tracker-sync/';

/**
 * What the gold ring around a cover means (owner, 2026-10-15): read on EVERY tracker the series syncs to - a series synced to two trackers
 * shows no ring on a chapter one of them has not reached. Shown as a legend on the Tracking page and in the series header's popover.
 */
/**
 * The notice after "Check my list on <tracker>" (1.43.0). A tracker whose whole list is read (MyAnimeList, owner option C) reports the
 * list's size, the series here on it and the new rows; otherwise the per-series check (50 a click) reports what it checked.
 */
export function previewNotice(p: TrackerPreviewDto, name: string): string {
  const n = (count: number, one: string, many: string) => `${count} ${count === 1 ? one : many}`;
  if (p.listRead) {
    const entries = n(p.listEntries ?? 0, 'entry', 'entries');
    if (p.capReached) return `Stopped after ${entries}: today's ${name} requests are used up - try again tomorrow.`;
    return `Read your ${name} list (${entries}); ${n(p.matched ?? 0, 'is a series', 'are series')} here, ${p.created ?? 0} new on the `
      + `Tracking page. Nothing was changed on ${name}.`;
  }
  if (p.capReached) return `Checked ${p.entries.length}. Today's request limit for ${name} is reached - try again tomorrow.`;
  return `Checked ${n(p.entries.length, 'series', 'series')}${p.remaining > 0 ? `, ${p.remaining} left` : ''}. Nothing was changed on ${name}.`;
}

export const TRACKER_RING_LEGEND =
  'A gold border around a cover means every tracker this series syncs to counts that chapter or volume as read.';

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

/** What `errorText` says for a code it does not know (a fragment: "Sync problem (an unexpected error)"). */
const UNKNOWN_ERROR_FRAGMENT = 'an unexpected error';

/** The plain sentence of a failed request whose reason the client cannot name. */
export const GENERIC_FAILURE = 'Something went wrong. Try again.';

/** Server error codes (`ApiError.error`) in plain words. */
export function errorText(code: string | null | undefined, slug?: string): string {
  const name = trackerName(slug);
  switch (code) {
    case 'invalid_state': return 'This sign-in is not valid any more (it was used, it expired, or it was started in another browser session). Start again from the Tracking page.';
    case 'connect_rejected': return `${name} did not accept this sign-in. Start again from the Tracking page.`;
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
    case 'no_id': return `No ${name} id for this series - an admin can set it.`;
    case 'not_on_list': return `This series is not on your ${name} list.`;
    case 'daily_cap': return `Today's request limit for ${name} is reached. Try again tomorrow.`;
    case 'dates_not_supported': return `${name} keeps no start or finish dates.`;
    case 'invalid_date': return 'Give dates from 1900 to today, the finish not before the start.';
    case 'invalid_filter':
    case 'invalid_page':
    case 'invalid_search': return 'The list could not be shown with this filter or search (at most 200 characters).';
    case 'unknown_node': return 'This folder is not known any more.';
    case 'invalid_daily_cap': return 'Give a daily request limit from 100 to 20000.';
    case 'invalid_nodes': return 'Select 1 to 500 folders or series.';
    case 'rate_limited': return 'asked to slow down';
    case 'rejected': return 'refused';
    case 'unavailable': return 'not reachable';
    case 'key_ring': return 'the server\'s keys changed';
    case 'expired': return 'the sign-in expired';
    // A code this client does not know is never shown as it is (UX review UX11): a generic fragment instead.
    default: return UNKNOWN_ERROR_FRAGMENT;
  }
}

/** The `error` code of an HttpErrorResponse carrying an `ApiError`, if any. */
export function apiErrorCode(err: unknown): string | null {
  const body = (err as { error?: { error?: unknown } } | null)?.error;
  return typeof body?.error === 'string' ? body.error : null;
}

/** The codes `errorText` words as a fragment ("refused", for "Last sync failed (...)") as a sentence of their own. */
function failureSentence(code: string, slug?: string): string | null {
  const name = trackerName(slug);
  switch (code) {
    case 'rate_limited': return `${name} asked MangaPixer to slow down. Try again in a moment.`;
    case 'rejected': return `${name} refused the request.`;
    case 'unavailable': return `${name} could not be reached from this server. Try again later.`;
    case 'key_ring': return `The server's keys changed. Connect your ${name} account again.`;
    case 'expired': return `Your ${name} sign-in expired. Connect again.`;
    default: return null;
  }
}

/**
 * Plain words for a failed request: the server's code when this client knows it, else the HTTP status' meaning, else a generic
 * line - never a raw code (UX review UX11).
 */
export function failureText(err: unknown, slug?: string): string {
  const code = apiErrorCode(err);
  if (code) {
    const known = failureSentence(code, slug) ?? errorText(code, slug);
    if (known !== UNKNOWN_ERROR_FRAGMENT) return known;
  }
  const status = (err as { status?: number } | null)?.status;
  if (status === 401) return 'Your session ended. Sign in again.';
  if (status === 403) return 'You are not allowed to do this.';
  return GENERIC_FAILURE;
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

/** A positive decimal id (within a signed 64-bit range) as the base36 token MangaUpdates' site addresses use; null for anything else. */
export function encodeBase36(decimal: string | null | undefined): string | null {
  if (!decimal || !/^\d{1,19}$/.test(decimal)) return null;
  const value = BigInt(decimal);
  return value > 0n && value <= 9223372036854775807n ? value.toString(36) : null;
}

/**
 * 1.43.2: the series' own page on the tracker's site, from the tracker's id the row carries (`remoteId`) - MyAnimeList
 * `https://myanimelist.net/manga/<id>`, MangaUpdates `https://www.mangaupdates.com/series/<base36 id>` (the address form the site
 * uses and `parseTrackerId` reads back). Null without a usable id. Opened only by the user's click, in a new tab, without a referrer
 * (`rel="noopener noreferrer"`) - nothing is fetched here.
 */
export function trackerSeriesUrl(slug: string | null | undefined, remoteId: string | null | undefined): string | null {
  if (!remoteId || !/^\d{1,19}$/.test(remoteId) || BigInt(remoteId) <= 0n) return null;
  switch (slug) {
    case 'mal': return `https://myanimelist.net/manga/${BigInt(remoteId).toString()}`;
    case 'mangaupdates': {
      const token = encodeBase36(remoteId);
      return token ? `https://www.mangaupdates.com/series/${token}` : null;
    }
    default: return null;
  }
}

/** A base36 token (0-9a-z, at most 13 characters) as a positive decimal id within a signed 64-bit range, else null. */
export function decodeBase36(token: string): string | null {
  const t = token.toLowerCase();
  if (t.length === 0 || t.length > 13 || !/^[0-9a-z]+$/.test(t)) return null;
  let value = 0n;
  for (const c of t) value = value * 36n + BigInt(parseInt(c, 36));
  return value > 0n && value <= 9223372036854775807n ? value.toString() : null;
}

// ---- The Tracking page (1.43.0 owner redesign) ----

/** The SPA route of the Tracking page (account menu > Tracking). */
export const TRACKING_PATH = '/tracking';

/** One filter chip of the Tracking list: the server's `filter` value, its words and its count. */
export interface TrackingChip {
  filter: 'All' | TrackerListBucket;
  label: string;
  count: number;
}

/**
 * The chips in the order the page shows them (owner-approved mockup): All, MangaPixer ahead, <tracker> ahead, Left as is, Waiting for
 * you, No <tracker> id, Not synced - always shown, with their counts; then In sync, Not checked yet and Sync problem, only while they
 * hold a series (the server has these buckets too, and a row in them would otherwise be reachable only under All).
 */
export function trackingChips(counts: TrackerListCountsDto | null | undefined, slug: string): TrackingChip[] {
  const name = trackerName(slug);
  const c = counts ?? {
    all: 0, inSync: 0, mangaPixerAhead: 0, trackerAhead: 0, leftAsIs: 0, held: 0, noId: 0, notSynced: 0, problem: 0, unchecked: 0, attention: 0,
  };
  const always: TrackingChip[] = [
    { filter: 'All', label: 'All', count: c.all },
    { filter: 'MangaPixerAhead', label: 'MangaPixer ahead', count: c.mangaPixerAhead },
    { filter: 'TrackerAhead', label: `${name} ahead`, count: c.trackerAhead },
    { filter: 'LeftAsIs', label: 'Left as is', count: c.leftAsIs },
    { filter: 'Held', label: 'Waiting for you', count: c.held },
    { filter: 'NoId', label: `No ${name} id`, count: c.noId },
    { filter: 'NotSynced', label: 'Not synced', count: c.notSynced },
  ];
  const optional: TrackingChip[] = [
    { filter: 'InSync', label: 'In sync', count: c.inSync },
    { filter: 'Unchecked', label: 'Not checked yet', count: c.unchecked },
    { filter: 'Problem', label: 'Sync problem', count: c.problem },
  ];
  return [...always, ...optional.filter((o) => o.count > 0)];
}

/** The chip count each bucket adds to (`TrackerListCountsDto`). */
const COUNT_OF_BUCKET: Record<TrackerListBucket, Exclude<keyof TrackerListCountsDto, 'all' | 'attention'>> = {
  InSync: 'inSync', MangaPixerAhead: 'mangaPixerAhead', TrackerAhead: 'trackerAhead', LeftAsIs: 'leftAsIs', Held: 'held', NoId: 'noId',
  NotSynced: 'notSynced', Problem: 'problem', Unchecked: 'unchecked',
};

/** The buckets the account menu's badge counts (the server's `TrackerListRules.NeedsAttention`). */
const ATTENTION_BUCKETS: readonly TrackerListBucket[] = ['Held', 'TrackerAhead'];

/**
 * The chip counts after one series moved from one bucket to another (a row action answered with the row's new state; UX review
 * UX2). The counts are the user's whole list - the server counts them before the filter and the search - so the move is exact
 * without a new request. `all` stays; a bucket never drops below 0.
 */
export function movedCounts(counts: TrackerListCountsDto, from: TrackerListBucket, to: TrackerListBucket): TrackerListCountsDto {
  if (from === to) return counts;
  const next = { ...counts };
  const out = COUNT_OF_BUCKET[from];
  const into = COUNT_OF_BUCKET[to];
  if (out) next[out] = Math.max(0, next[out] - 1);
  if (into) next[into] = next[into] + 1;
  const weight = (b: TrackerListBucket) => (ATTENTION_BUCKETS.includes(b) ? 1 : 0);
  next.attention = Math.max(0, next.attention + weight(to) - weight(from));
  return next;
}

/** A row's chip in words (with the tracker's status for a series left as is). */
export function bucketText(row: Pick<TrackerSeriesRowDto, 'bucket' | 'tracker' | 'remoteStatus' | 'errorCode' | 'dontSync'>): string {
  const name = trackerName(row.tracker);
  switch (row.bucket) {
    case 'InSync': return 'In sync';
    case 'MangaPixerAhead': return 'MangaPixer ahead';
    case 'TrackerAhead': return `${name} ahead`;
    case 'LeftAsIs': return `Left as is · ${statusText(row.remoteStatus) ?? 'not Reading'} on ${name}`;
    case 'Held': return 'Waiting for you';
    case 'NoId': return `No ${name} id`;
    case 'NotSynced': return row.dontSync ? 'Not synced (your choice)' : `Not synced - not on your ${name} list`;
    case 'Problem': return `Sync problem${row.errorCode ? ` (${errorText(row.errorCode)})` : ''}`;
    case 'Unchecked': return 'Not checked yet';
    default: return row.bucket;
  }
}

/** The chip's colour family: `ahead` (accent), `tracker` (gold), `warn`, `muted` or `ok`. */
export function bucketTone(bucket: TrackerListBucket): 'ok' | 'ahead' | 'tracker' | 'warn' | 'muted' {
  switch (bucket) {
    case 'InSync': return 'ok';
    case 'MangaPixerAhead': return 'ahead';
    case 'TrackerAhead':
    case 'LeftAsIs': return 'tracker';
    case 'Held':
    case 'Problem': return 'warn';
    default: return 'muted';
  }
}

/**
 * Why a "Set progress" action cannot move anything now (the disabled button's tooltip). `direction` 'from' = "from <tracker> to
 * MangaPixer", 'to' = "from MangaPixer to <tracker>".
 */
export function actionBlockText(block: TrackerActionBlock, direction: 'from' | 'to', slug: string): string | null {
  const name = trackerName(slug);
  switch (block) {
    case 'None': return null;
    case 'NotChecked': return `${name} was not checked for this series yet - use Sync now first`;
    case 'NotAhead': return direction === 'from'
      ? `${name} is not ahead of MangaPixer - nothing to bring in`
      : `MangaPixer is not ahead of ${name} - nothing to send`;
    case 'NoId': return `No ${name} id for this series - an admin can set it`;
    case 'DontSync': return `You turned sync off for this series on ${name}`;
    case 'LeftAsIs': return `Left as is on ${name} (on hold, dropped, completed or rereading) - MangaPixer does not change it`;
    case 'NotOnList': return `Not on your ${name} list`;
    default: return block;
  }
}

/** 1.44.0: why reading in a folder is not sent to the user's trackers (the series popover; owner, 2026-10-17). */
export function notSyncedText(reason: TrackerNotSynced): string {
  switch (reason) {
    case 'PrivateLibrary': return 'Reading here is not sent to your trackers: this folder is in one of your Private libraries.';
    case 'Doujinshi': return 'Reading here is not sent to your trackers: this series is doujinshi.';
    case 'SwitchedOff': return 'Reading here is not sent to your trackers: you turned sync off for this folder.';
  }
}

/** 1.44.0: the offer line - "MyAnimeList's 271 chapters finish volume 27; its list says 25." */
export function volumesFromChaptersText(row: TrackerSeriesRowDto): string | null {
  if (row.volumesFromChapters == null || row.remoteChapters == null) return null;
  const name = trackerName(row.tracker);
  const listed = row.remoteVolumes ? `its list says ${row.remoteVolumes}` : 'its list has no volumes';
  return `${name}'s ${row.remoteChapters} chapters finish volume ${row.volumesFromChapters}; ${listed}.`;
}

/** 1.44.0: the offer's button. */
export function volumesFromChaptersLabel(row: TrackerSeriesRowDto): string {
  return `Set ${trackerName(row.tracker)}'s volumes to ${row.volumesFromChapters}`;
}

/** After "Turn sync on": when the folder's reading is counted (nothing is sent by the switch itself). */
export const SYNC_TURNED_ON_TEXT = 'Sync is on for this folder. MangaPixer counts it the next time you read here, or with Sync now on the Tracking page.';

/** "45 of 51 ch · 4 of 10 vol", "12 ch", or null when neither side is known. */
export function ofText(read: number | null | undefined, total: number | null | undefined, unit: 'ch' | 'vol'): string | null {
  if (read == null) return null;
  // 1.44.0: a stored total below the count (an old record, a total that counts differently) is left out - never "130 of 129 ch".
  return total != null && total > 0 && total >= read ? `${read} of ${total} ${unit}` : `${read} ${unit}`;
}

/**
 * The confirm question of "Set progress from <tracker> to MangaPixer" ("Mark chapters 1-51 read in MangaPixer?"). Chapters when
 * the tracker has them, else volumes.
 */
export function fromTrackerQuestion(row: Pick<TrackerSeriesRowDto, 'remoteChapters' | 'remoteVolumes'>): string {
  const parts: string[] = [];
  if (row.remoteChapters != null && row.remoteChapters > 0) parts.push(row.remoteChapters === 1 ? 'chapter 1' : `chapters 1-${row.remoteChapters}`);
  if (row.remoteVolumes != null && row.remoteVolumes > 0) parts.push(row.remoteVolumes === 1 ? 'volume 1' : `volumes 1-${row.remoteVolumes}`);
  return `Mark ${parts.length ? parts.join(' and ') : 'what the tracker has'} read in MangaPixer?`;
}

/** A `yyyy-MM-dd` date as the page shows it ("Oct 14, 2026"), locale-independent; '—' for none. */
export function dateText(value: string | null | undefined): string {
  if (!value) return '—';
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!m) return value;
  const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  const month = months[Number(m[2]) - 1];
  return month ? `${month} ${Number(m[3])}, ${m[1]}` : value;
}

// ---- 1.43.2: the Tracking list's Select mode (one multi-select for every chip; it replaces "Send all") ----

/** The bar's actions in their default order. */
export const BATCH_ACTIONS: readonly TrackerBatchAction[] = ['FromTracker', 'ToTracker', 'SyncNow', 'DontSync', 'SyncAgain', 'VolumesFromChapters'];

/**
 * The bar's actions for a chip: the chip's natural action first (MyAnimeList ahead -> from the tracker; Waiting for you / MangaPixer
 * ahead -> to the tracker; Not synced -> Sync again), then the rest in the default order.
 */
export function batchActionsFor(filter: string): TrackerBatchAction[] {
  const first: TrackerBatchAction | null = filter === 'TrackerAhead' ? 'FromTracker'
    : filter === 'Held' || filter === 'MangaPixerAhead' ? 'ToTracker'
      : filter === 'NotSynced' ? 'SyncAgain' : null;
  return first ? [first, ...BATCH_ACTIONS.filter((a) => a !== first)] : [...BATCH_ACTIONS];
}

/** An action's words: the full label, and the short one a narrow bar shows (the full one stays its accessible name). */
export function batchActionLabel(action: TrackerBatchAction, slug: string, short = false): string {
  const name = trackerName(slug);
  switch (action) {
    case 'FromTracker': return short ? `${name} → MangaPixer` : `Set progress from ${name} to MangaPixer`;
    case 'ToTracker': return short ? `MangaPixer → ${name}` : `Set progress from MangaPixer to ${name}`;
    case 'SyncNow': return 'Sync now';
    case 'DontSync': return "Don't sync";
    case 'SyncAgain': return 'Sync again';
    case 'VolumesFromChapters': return short ? `${name} volumes` : `Set ${name}'s volumes from its chapters`;
    default: return action;
  }
}

/** The confirm button of an action. */
export function batchConfirmLabel(action: TrackerBatchAction): string {
  switch (action) {
    case 'FromTracker': return 'Mark read';
    case 'ToTracker': return 'Send';
    case 'SyncNow': return 'Sync now';
    case 'DontSync': return "Don't sync";
    case 'SyncAgain': return 'Sync again';
    case 'VolumesFromChapters': return 'Set volumes';
    default: return action;
  }
}

const seriesCount = (n: number) => `${n} series`;

/**
 * The one confirmation of an apply, with the counts: `applies` series the action applies to, `others` selected series it leaves as
 * they are. "Set progress from MangaPixer" words the add part from the account's "Add series I start reading".
 */
export function batchQuestion(action: TrackerBatchAction, slug: string, applies: number, others: number, addStartedSeries: boolean): string {
  const name = trackerName(slug);
  let text: string;
  switch (action) {
    case 'FromTracker':
      text = `Mark read in MangaPixer what ${name} counts, for ${seriesCount(applies)}? Nothing is unmarked.`;
      break;
    case 'ToTracker':
      text = `Send MangaPixer's progress to ${name} for ${seriesCount(applies)}? Never lowers; series not on your list are `
        + (addStartedSeries ? 'added (Add series I start reading is on).' : 'left out (Add series I start reading is off).');
      break;
    case 'SyncNow':
      text = `Sync ${seriesCount(applies)} with ${name} now? MangaPixer reads each one there and sends only what syncs on its own; `
        + 'a jump waiting for you keeps waiting.';
      break;
    case 'DontSync':
      text = `Stop syncing ${seriesCount(applies)} with ${name}? Nothing is sent for them until you turn sync on again.`;
      break;
    case 'SyncAgain':
      text = `Sync ${seriesCount(applies)} with ${name} again? They sync the next time you read them, or with Sync now.`;
      break;
    case 'VolumesFromChapters':
      text = `Set ${name}'s volumes to what its own chapters complete, for ${seriesCount(applies)}? Only the volume count changes; `
        + 'the chapters and the status stay as they are.';
      break;
    default:
      text = '';
  }
  return others > 0 ? `${text} ${others} of the selected ${others === 1 ? 'is' : 'are'} left as ${others === 1 ? 'it is' : 'they are'}.` : text;
}

/** Why a batch left a series as it is, in a few words ("2 skipped (left as is)"). */
export function batchSkipText(reason: TrackerBatchSkip, slug: string): string {
  const name = trackerName(slug);
  switch (reason) {
    case 'NotChecked': return 'not checked yet';
    case 'NotAhead': return 'not ahead';
    case 'NoId': return `no ${name} id`;
    case 'DontSync': return 'not synced';
    case 'LeftAsIs': return 'left as is';
    case 'NotOnList': return `not on your ${name} list`;
    case 'NotListed': return 'not on this list any more';
    case 'AlreadyOff': return 'already off';
    case 'AlreadyOn': return 'already on';
    case 'RetryLater': return `${name} asked to slow down - tried again shortly`;
    case 'VolumesUpToDate': return 'volumes already match';
    default: return reason;
  }
}

/** What an apply did, over all its chunks. */
export interface BatchTotals {
  done: number;
  skipped: number;
  failed: number;
  marked: number;
  alreadyRead: number;
  capReached: boolean;
  notReached: number;
  stoppedCode: string | null;
  skips: Partial<Record<TrackerBatchSkip, number>>;
  /** Selected series no request reached: after a Cancel, the cap or a stop (their chunks were never sent). */
  unsent: number;
  cancelled: boolean;
}

export function emptyBatchTotals(): BatchTotals {
  return { done: 0, skipped: 0, failed: 0, marked: 0, alreadyRead: 0, capReached: false, notReached: 0, stoppedCode: null, skips: {}, unsent: 0,
    cancelled: false };
}

/**
 * The one notice after an apply ("Set progress from MyAnimeList for 51 series: 412 files marked, 3 already read, 2 skipped (left as
 * is)."), with the daily cap, a stop or a Cancel said at the end.
 */
export function batchSummary(action: TrackerBatchAction, slug: string, t: BatchTotals): string {
  const name = trackerName(slug);
  const processed = t.done + t.skipped + t.failed;
  const files = (n: number) => (n === 1 ? '1 file' : `${n} files`);
  const parts: string[] = [];
  let lead: string;
  switch (action) {
    case 'FromTracker':
      lead = `Set progress from ${name} for ${seriesCount(processed)}`;
      parts.push(`${files(t.marked)} marked`, `${t.alreadyRead} already read`);
      break;
    case 'ToTracker':
      lead = `Set progress from MangaPixer to ${name} for ${seriesCount(processed)}`;
      parts.push(`${t.done} sent`);
      break;
    case 'SyncNow':
      lead = `Synced ${seriesCount(processed)} with ${name}`;
      parts.push(`${t.done} checked`);
      break;
    case 'DontSync':
      lead = `Turned sync off for ${seriesCount(processed)}`;
      parts.push(`${t.done} turned off`);
      break;
    case 'VolumesFromChapters':
      lead = `Set ${name}'s volumes from its chapters for ${seriesCount(processed)}`;
      parts.push(`${t.done} raised`);
      break;
    default:
      lead = `Turned sync on again for ${seriesCount(processed)}`;
      parts.push(`${t.done} turned on`);
      break;
  }
  if (t.skipped > 0) {
    const reasons = (Object.entries(t.skips) as [TrackerBatchSkip, number][]).filter(([, n]) => n > 0);
    const why = reasons.length === 1 ? batchSkipText(reasons[0][0], slug) : reasons.map(([r, n]) => `${n} ${batchSkipText(r, slug)}`).join(', ');
    parts.push(`${t.skipped} skipped${why ? ` (${why})` : ''}`);
  }
  if (t.failed > 0) parts.push(`${t.failed} failed (see their rows)`);
  let text = `${lead}: ${parts.join(', ')}.`;
  const left = t.notReached + t.unsent;
  if (t.capReached) text += ` Today's request limit for ${name} is reached - ${seriesCount(left)} not done; try again tomorrow.`;
  else if (t.stoppedCode) text += ` Stopped: ${failureText({ error: { error: t.stoppedCode } }, slug)}${left > 0 ? ` ${seriesCount(left)} not done.` : ''}`;
  else if (t.cancelled && left > 0) text += ` Cancelled - ${seriesCount(left)} not done.`;
  return text;
}
