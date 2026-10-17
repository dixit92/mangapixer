import {
  TRACKER_ADMIN_CONSENT_VERSION, TRACKER_USER_CONSENT_VERSION, accountProblem, actionBlockText, bucketText, dateText, decodeBase36,
  entryStateText, errorText, failureText, fromTrackerQuestion, numbersText, ofText, parseTrackerId, previewNotice, proposedRedirectUri,
  revokeHint, trackerName, trackingChips, encodeBase36, trackerSeriesUrl, batchActionsFor, batchQuestion, batchSummary, emptyBatchTotals,
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
    // 1.44.0 (owner screenshot, live 1.43.0): a stored total below what MangaPixer counts is not shown ("130 of 129 ch", "19 of 1 ch").
    expect(ofText(130, 129, 'ch')).toBe('130 ch');
    expect(ofText(19, 1, 'ch')).toBe('19 ch');
    expect(ofText(129, 129, 'ch')).toBe('129 of 129 ch');
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

  // ---- 1.43.2 ----

  it('encodes a MangaUpdates id as the site\'s base36 token - the inverse of decodeBase36', () => {
    for (const id of ['1', '35', '36', '51239621230', '9223372036854775807']) {
      const token = encodeBase36(id)!;
      expect(decodeBase36(token)).toBe(id);
    }
    expect(encodeBase36(decodeBase36('njeqwry'))).toBe('njeqwry');
    for (const bad of [null, '', '0', '-1', '12a', '9223372036854775808', '1'.repeat(20)]) expect(encodeBase36(bad)).toBeNull();
  });

  it('builds the series\' page on the tracker\'s site from a usable id only', () => {
    expect(trackerSeriesUrl('mal', '2')).toBe('https://myanimelist.net/manga/2');
    expect(trackerSeriesUrl('mal', '0012')).toBe('https://myanimelist.net/manga/12');
    expect(trackerSeriesUrl('mangaupdates', '51239621230')).toBe(`https://www.mangaupdates.com/series/${(51239621230).toString(36)}`);
    for (const bad of [null, undefined, '', '0', 'abc', '2/../../x', '1'.repeat(20)]) expect(trackerSeriesUrl('mal', bad)).toBeNull();
    expect(trackerSeriesUrl('other', '2')).toBeNull();
  });

  it('puts the chip\'s natural Select-mode action first', () => {
    expect(batchActionsFor('TrackerAhead')[0]).toBe('FromTracker');
    expect(batchActionsFor('Held')[0]).toBe('ToTracker');
    expect(batchActionsFor('MangaPixerAhead')[0]).toBe('ToTracker');
    expect(batchActionsFor('NotSynced')[0]).toBe('SyncAgain');
    expect(batchActionsFor('All')).toEqual(['FromTracker', 'ToTracker', 'SyncNow', 'DontSync', 'SyncAgain']);
    expect(new Set(batchActionsFor('NotSynced')).size).toBe(5);
  });

  it('words the one confirmation of an apply with its counts', () => {
    expect(batchQuestion('FromTracker', 'mal', 51, 0, true)).toBe('Mark read in MangaPixer what MyAnimeList counts, for 51 series? Nothing is unmarked.');
    expect(batchQuestion('ToTracker', 'mal', 26, 0, true)).toBe(
      'Send MangaPixer\'s progress to MyAnimeList for 26 series? Never lowers; series not on your list are added (Add series I start reading is on).');
    expect(batchQuestion('ToTracker', 'mangaupdates', 1, 2, false)).toBe(
      'Send MangaPixer\'s progress to MangaUpdates for 1 series? Never lowers; series not on your list are left out (Add series I start reading is off). '
      + '2 of the selected are left as they are.');
    expect(batchQuestion('SyncAgain', 'mal', 3, 0, true)).toContain('Sync 3 series with MyAnimeList again?');
  });

  it('sums an apply up in one line, with the skip reasons, failures, the cap, a stop and a Cancel', () => {
    const t = { ...emptyBatchTotals(), done: 49, skipped: 2, marked: 412, alreadyRead: 3, skips: { LeftAsIs: 2 } };
    expect(batchSummary('FromTracker', 'mal', t)).toBe(
      'Set progress from MyAnimeList for 51 series: 412 files marked, 3 already read, 2 skipped (left as is).');
    expect(batchSummary('ToTracker', 'mal', { ...emptyBatchTotals(), done: 20, skipped: 3, failed: 1, skips: { NotAhead: 2, LeftAsIs: 1 } })).toBe(
      'Set progress from MangaPixer to MyAnimeList for 24 series: 20 sent, 3 skipped (2 not ahead, 1 left as is), 1 failed (see their rows).');
    expect(batchSummary('SyncNow', 'mal', { ...emptyBatchTotals(), done: 1, capReached: true, notReached: 4, unsent: 5 })).toContain(
      'Today\'s request limit for MyAnimeList is reached - 9 series not done; try again tomorrow.');
    expect(batchSummary('SyncNow', 'mal', { ...emptyBatchTotals(), done: 1, stoppedCode: 'needs_reconnect', unsent: 2 })).toBe(
      'Synced 1 series with MyAnimeList: 1 checked. Stopped: Connect your account again. 2 series not done.');
    expect(batchSummary('DontSync', 'mal', { ...emptyBatchTotals(), done: 2, cancelled: true, unsent: 0 })).toBe('Turned sync off for 2 series: 2 turned off.');
  });
});
