import {
  TRACKER_ADMIN_CONSENT_VERSION, TRACKER_USER_CONSENT_VERSION, accountProblem, actionBlockText, bucketText, dateText, decodeBase36,
  entryStateText, errorText, failureText, fromTrackerQuestion, numbersText, ofText, parseTrackerId, previewNotice, proposedRedirectUri,
  revokeHint, trackerName, trackingChips,
} from './tracker-texts';
import { listCounts, seriesRow } from './trackers.testing';
import { TrackerAccountDto } from '../../core/api/api-types';

function account(overrides: Partial<TrackerAccountDto> = {}): TrackerAccountDto {
  return {
    tracker: 'mal', state: 'Ok', displayName: 'reader', connectedAt: '2026-10-13T00:00:00Z', consentRenewalNeeded: false,
    addStartedSeries: true, privateEntries: true, requestsToday: 0, dailyRequestCap: 2000, ...overrides,
  };
}

/** Tracker sync words and the local id parser (1.43.0). */
describe('tracker texts', () => {
  it('names the trackers and proposes the redirect URL from the browser address', () => {
    expect(trackerName('mal')).toBe('MyAnimeList');
    expect(trackerName('mangaupdates')).toBe('MangaUpdates');
    expect(proposedRedirectUri('mal', 'https://manga.example.test:8443')).toBe('https://manga.example.test:8443/trackers/callback/mal');
  });

  it('starts every consent stream at version 1', () => {
    expect(TRACKER_ADMIN_CONSENT_VERSION).toEqual({ mal: 1, mangaupdates: 1 });
    expect(TRACKER_USER_CONSENT_VERSION).toEqual({ mal: 1, mangaupdates: 1 });
  });

  it('puts every entry state in words', () => {
    const base = { tracker: 'mal', errorCode: null, remoteStatus: null };
    expect(entryStateText({ ...base, state: 'Ok' })).toBe('Up to date');
    expect(entryStateText({ ...base, state: 'TrackerAhead' })).toBe('MyAnimeList is ahead - nothing sent');
    expect(entryStateText({ ...base, state: 'LowerHere' })).toContain('not lowered');
    expect(entryStateText({ ...base, state: 'Held' })).toBe('Waiting for you to press Send');
    expect(entryStateText({ ...base, state: 'NoId' })).toContain('an admin can set it');
    expect(entryStateText({ ...base, state: 'DontSync' })).toBe('Not synced (your choice)');
    expect(entryStateText({ ...base, state: 'Error', errorCode: 'unavailable' })).toBe('Last sync failed (not reachable)');
    expect(entryStateText({ ...base, state: 'NotOnList' })).toBe('Not on your MyAnimeList list');
    expect(entryStateText({ ...base, state: 'LeftAsIs', remoteStatus: 'OnHold' })).toBe('Left as is: On hold on MyAnimeList');
    expect(entryStateText({ ...base, state: 'Waiting' })).toContain('slow down');
    expect(entryStateText({ ...base, state: 'Pending' })).toBe('Not checked yet');
  });

  it('describes account problems and where access ends', () => {
    expect(accountProblem(account(), 'mal')).toBeNull();
    expect(accountProblem(account({ state: 'DisconnectedByAdmin', displayName: null }), 'mal')).toContain('An admin disconnected');
    expect(accountProblem(account({ state: 'NeedsReconnect' }), 'mal')).toContain('Connect again');
    expect(accountProblem(account({ consentRenewalNeeded: true }), 'mal')).toContain('has changed');
    expect(revokeHint('mangaupdates')).toContain('signs MangaPixer out on MangaUpdates');
    expect(revokeHint('mal')).toContain('deletes MangaPixer\'s sign-in');
  });

  it('turns the callback errors into plain words', () => {
    expect(errorText('invalid_state')).toContain('not valid any more');
    expect(errorText('connect_rejected', 'mal')).toBe('MyAnimeList did not accept this sign-in. Start again from the Tracking page.');
    expect(errorText('tracker_unavailable', 'mal')).toContain('could not be reached');
    expect(failureText({ status: 400, error: { error: 'invalid_state', message: 'x' } })).toContain('not valid any more');
    expect(failureText({ status: 401, error: null })).toContain('Sign in again');
    expect(failureText(new Error('boom'))).toBe('Something went wrong. Try again.');
  });

  it('never shows a server code it does not know: a generic sentence, a generic fragment in a chip (UX review UX11)', () => {
    expect(failureText({ status: 500, error: { error: 'boom', message: 'x' } })).toBe('Something went wrong. Try again.');
    expect(failureText({ status: 403, error: { error: 'boom', message: 'x' } })).toBe('You are not allowed to do this.');
    expect(errorText('boom')).toBe('an unexpected error');
    expect(entryStateText({ tracker: 'mal', errorCode: 'boom', remoteStatus: null, state: 'Error' })).toBe('Last sync failed (an unexpected error)');
    // The codes the tracker routes answer with are all worded.
    expect(failureText({ status: 400, error: { error: 'invalid_nodes', message: 'x' } })).toBe('Select 1 to 500 folders or series.');
    expect(failureText({ status: 400, error: { error: 'invalid_daily_cap', message: 'x' } })).toContain('from 100 to 20000');
  });

  it('formats numbers', () => {
    expect(numbersText(12, 2)).toBe('12 ch · 2 vol');
    expect(numbersText(null, 3)).toBe('3 vol');
    expect(numbersText(null, undefined)).toBeNull();
  });

  it('reads a MyAnimeList id from the number or a series address', () => {
    expect(parseTrackerId('mal', ' 2 ')).toBe('2');
    expect(parseTrackerId('mal', 'https://myanimelist.net/manga/2/Berserk')).toBe('2');
    expect(parseTrackerId('mal', 'myanimelist.net/manga/13')).toBe('13');
    expect(parseTrackerId('mal', 'https://myanimelist.net/anime/2')).toBeNull();
    expect(parseTrackerId('mal', 'https://example.test/manga/2')).toBeNull();
    expect(parseTrackerId('mal', 'javascript:alert(1)')).toBeNull();
    expect(parseTrackerId('mal', '0')).toBeNull();
    expect(parseTrackerId('mal', '')).toBe('');
  });

  it('reads a MangaUpdates id from the number or the base36 series address, like the server', () => {
    // "njeqwry" is the server's own example (MangaUpdatesReference): base36 of the numeric series id.
    expect(parseTrackerId('mangaupdates', 'https://www.mangaupdates.com/series/njeqwry/berserk')).toBe(decodeBase36('njeqwry'));
    expect(decodeBase36('njeqwry')).toBe(parseInt('njeqwry', 36).toString());
    expect(parseTrackerId('mangaupdates', '51239621230')).toBe('51239621230');
    expect(parseTrackerId('mangaupdates', 'https://www.mangaupdates.com/series.html?id=88')).toBeNull();
    expect(parseTrackerId('mangaupdates', 'https://myanimelist.net/manga/2')).toBeNull();
    expect(decodeBase36('zzzzzzzzzzzzzz')).toBeNull();
    expect(decodeBase36('1y2p0ij32e8e8')).toBeNull(); // past the signed 64-bit range
  });

  // ---- The Tracking page (1.43.0 owner redesign) ----

  it('lists the seven owner chips always and the other buckets only while they hold a series', () => {
    expect(trackingChips(listCounts({ trackerAhead: 2 }), 'mal').map((c) => `${c.filter}:${c.label}:${c.count}`)).toEqual([
      'All:All:2', 'MangaPixerAhead:MangaPixer ahead:0', 'TrackerAhead:MyAnimeList ahead:2', 'LeftAsIs:Left as is:0',
      'Held:Waiting for you:0', 'NoId:No MyAnimeList id:0', 'NotSynced:Not synced:0',
    ]);
    expect(trackingChips(listCounts({ inSync: 1, unchecked: 2, problem: 3 }), 'mangaupdates').slice(7).map((c) => c.label))
      .toEqual(['In sync', 'Not checked yet', 'Sync problem']);
    expect(trackingChips(null, 'mal')).toHaveLength(7);
  });

  it('puts every chip of a row in words', () => {
    const r = (o: Parameters<typeof seriesRow>[0]) => bucketText(seriesRow(o));
    expect(r({ bucket: 'InSync' })).toBe('In sync');
    expect(r({ bucket: 'MangaPixerAhead' })).toBe('MangaPixer ahead');
    expect(r({ bucket: 'TrackerAhead' })).toBe('MyAnimeList ahead');
    expect(r({ bucket: 'LeftAsIs', remoteStatus: 'OnHold' })).toBe('Left as is · On hold on MyAnimeList');
    expect(r({ bucket: 'Held' })).toBe('Waiting for you');
    expect(r({ bucket: 'NoId', tracker: 'mangaupdates' })).toBe('No MangaUpdates id');
    expect(r({ bucket: 'NotSynced', dontSync: true })).toBe('Not synced (your choice)');
    expect(r({ bucket: 'NotSynced' })).toBe('Not synced - not on your MyAnimeList list');
    expect(r({ bucket: 'Problem', errorCode: 'rate_limited' })).toBe('Sync problem (asked to slow down)');
    expect(r({ bucket: 'Unchecked' })).toBe('Not checked yet');
  });

  it('gives every reason a "Set progress" action is off', () => {
    expect(actionBlockText('None', 'from', 'mal')).toBeNull();
    expect(actionBlockText('NotAhead', 'from', 'mal')).toBe('MyAnimeList is not ahead of MangaPixer - nothing to bring in');
    expect(actionBlockText('NotAhead', 'to', 'mal')).toBe('MangaPixer is not ahead of MyAnimeList - nothing to send');
    for (const b of ['NotChecked', 'NoId', 'DontSync', 'LeftAsIs', 'NotOnList'] as const) {
      expect(actionBlockText(b, 'to', 'mangaupdates')).toContain('MangaUpdates');
    }
  });

  it('words the from-tracker question, the numbers and the dates', () => {
    expect(fromTrackerQuestion({ remoteChapters: 51, remoteVolumes: null })).toBe('Mark chapters 1-51 read in MangaPixer?');
    expect(fromTrackerQuestion({ remoteChapters: 1, remoteVolumes: 3 })).toBe('Mark chapter 1 and volumes 1-3 read in MangaPixer?');
    expect(ofText(45, 51, 'ch')).toBe('45 of 51 ch');
    expect(ofText(4, null, 'vol')).toBe('4 vol');
    expect(ofText(null, 51, 'ch')).toBeNull();
    expect(dateText('2026-10-14')).toBe('Oct 14, 2026');
    expect(dateText(null)).toBe('—');
  });

  it('says a tracker failure as a sentence that names the tracker', () => {
    expect(failureText({ status: 503, error: { error: 'unavailable' } }, 'mal')).toBe('MyAnimeList could not be reached from this server. Try again later.');
    expect(failureText({ status: 503, error: { error: 'rate_limited' } }, 'mangaupdates')).toContain('MangaUpdates asked MangaPixer to slow down');
    expect(failureText({ status: 409, error: { error: 'daily_cap' } }, 'mal')).toContain("Today's request limit for MyAnimeList");
    expect(failureText({ status: 409, error: { error: 'no_id' } }, 'mal')).toContain('No MyAnimeList id');
  });

  it('words "Check my list": a whole-list read with its counts, the cap stop, and the per-series check (owner option C)', () => {
    const base = { entries: [], remaining: 0, capReached: false };
    expect(previewNotice({ ...base, listRead: true, listEntries: 412, matched: 37, created: 12 }, 'MyAnimeList')).toBe(
      'Read your MyAnimeList list (412 entries); 37 are series here, 12 new on the Tracking page. Nothing was changed on MyAnimeList.');
    expect(previewNotice({ ...base, listRead: true, listEntries: 1, matched: 1, created: 0 }, 'MyAnimeList')).toBe(
      'Read your MyAnimeList list (1 entry); 1 is a series here, 0 new on the Tracking page. Nothing was changed on MyAnimeList.');
    expect(previewNotice({ ...base, listRead: true, listEntries: 1000, capReached: true }, 'MyAnimeList')).toBe(
      'Stopped after 1000 entries: today\'s MyAnimeList requests are used up - try again tomorrow.');
    expect(previewNotice({ ...base, entries: [], remaining: 3 }, 'MangaUpdates')).toBe(
      'Checked 0 series, 3 left. Nothing was changed on MangaUpdates.');
  });
});
