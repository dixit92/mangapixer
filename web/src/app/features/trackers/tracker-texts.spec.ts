import {
  TRACKER_ADMIN_CONSENT_VERSION, TRACKER_USER_CONSENT_VERSION, accountProblem, decodeBase36, entryStateText, errorText, failureText,
  numbersText, parseTrackerId, proposedRedirectUri, revokeHint, trackerName,
} from './tracker-texts';
import { TrackerAccountDto } from '../../core/api/api-types';

function account(overrides: Partial<TrackerAccountDto> = {}): TrackerAccountDto {
  return {
    tracker: 'mal', state: 'Ok', displayName: 'reader', connectedAt: '2026-10-13T00:00:00Z', consentRenewalNeeded: false,
    addStartedSeries: true, privateEntries: true, requestsToday: 0, dailyRequestCap: 300, ...overrides,
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
    expect(errorText('connect_rejected', 'mal')).toBe('MyAnimeList did not accept this sign-in. Start again from Settings.');
    expect(errorText('tracker_unavailable', 'mal')).toContain('could not be reached');
    expect(failureText({ status: 400, error: { error: 'invalid_state', message: 'x' } })).toContain('not valid any more');
    expect(failureText({ status: 401, error: null })).toContain('Sign in again');
    expect(failureText(new Error('boom'))).toBe('Something went wrong. Try again.');
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
});
