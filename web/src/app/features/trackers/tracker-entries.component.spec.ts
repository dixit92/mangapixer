import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';

import { TrackerEntryDto } from '../../core/api/api-types';
import { TrackerEntriesComponent } from './tracker-entries.component';

const BASE = '/api/v1/trackers/mal';

function entry(o: Partial<TrackerEntryDto> = {}): TrackerEntryDto {
  return {
    seriesId: 's1', title: 'Synthetic Saga', nodeId: 'n1', tracker: 'mal', remoteId: '2', remoteIdSource: 'MangaDex', state: 'Ok',
    errorCode: null, dontSync: false, localChapters: 12, localVolumes: 2, heldChapters: null, heldVolumes: null,
    lastSentChapters: 12, lastSentVolumes: 2, lastSentAt: '2026-10-13T00:00:00Z', remoteChapters: 12, remoteVolumes: 2,
    remoteStatus: 'Reading', remoteMissing: false, remoteSeenAt: '2026-10-13T00:00:00Z', updatedAt: '2026-10-13T00:00:00Z', ...o,
  };
}

/**
 * The caller's series on a tracker (1.43.0): every state in words, Sync now, Send for a held jump, "Set to N" only after the
 * warning that it may lower the tracker, Don't sync, the paged first-connect preview and Send all after a confirm.
 */
describe('TrackerEntriesComponent', () => {
  function create(list: TrackerEntryDto[], canSync = true) {
    TestBed.configureTestingModule({
      imports: [TrackerEntriesComponent],
      providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(TrackerEntriesComponent);
    fixture.componentRef.setInput('slug', 'mal');
    fixture.componentRef.setInput('name', 'MyAnimeList');
    fixture.componentRef.setInput('canSync', canSync);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(`${BASE}/entries`).flush(list);
    fixture.detectChanges();
    return { fixture, http, el: fixture.nativeElement as HTMLElement, c: fixture.componentInstance };
  }

  const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement;

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('lists series with their numbers; the ones that need a look come first', () => {
    const { el } = create([
      entry(),
      entry({ seriesId: 's2', title: 'Another Tale', state: 'Held', heldChapters: 40, localChapters: 12 }),
      entry({ seriesId: 's3', title: 'No Number', state: 'NoId', remoteId: null }),
    ]);
    const rows = [...el.querySelectorAll('li')].map((li) => li.getAttribute('data-testid'));
    expect(rows).toEqual(['tracker-entry-s2', 'tracker-entry-s3', 'tracker-entry-s1']);
    expect(q(el, 'tracker-entry-s1').textContent).toContain('Here: 12 ch · 2 vol');
    expect(q(el, 'tracker-entry-s1').textContent).toContain('MyAnimeList: 12 ch · 2 vol (Reading)');
    expect(q(el, 'tracker-entry-s2').textContent).toContain('Waiting for Send: 40 ch');
    expect(q(el, 'tracker-entry-state-s3').textContent).toContain('an admin can set it');
    expect(q(el, 'tracker-sync-now-s3')).toBeNull();
    expect(q(el, 'tracker-set-s3')).toBeNull();
    expect(q(el, 'tracker-send-s1')).toBeNull();
    expect(q(el, 'tracker-send-all-mal').textContent).toContain('Send all (1)');
    expect((q(el, 'tracker-entry-s1').querySelector('a') as HTMLAnchorElement).getAttribute('href')).toBe('/series/n1');
  });

  it('Sync now and Send act on one series and show its new state', () => {
    const { fixture, http, el } = create([entry({ state: 'Held', heldChapters: 40 })]);
    q(el, 'tracker-sync-now-s1').click();
    http.expectOne(`${BASE}/entries/s1/sync`).flush(entry({ state: 'Held', heldChapters: 40 }));
    fixture.detectChanges();
    q(el, 'tracker-send-s1').click();
    http.expectOne(`${BASE}/entries/s1/send`).flush(entry({ localChapters: 40, lastSentChapters: 40 }));
    fixture.detectChanges();
    expect(q(el, 'tracker-entry-state-s1').textContent).toContain('Up to date');
    expect(q(el, 'tracker-send-s1')).toBeNull();
  });

  it('"Set to" warns that it may lower the tracker before it sends', () => {
    const { fixture, http, el, c } = create([entry({ remoteChapters: 50 })]);
    q(el, 'tracker-set-s1').click();
    fixture.detectChanges();
    expect(q(el, 'tracker-set-form-s1').textContent).toContain('also when MyAnimeList has MORE');
    http.expectNone(`${BASE}/entries/s1/set`);
    c.setChapters.set(10);
    c.setVolumes.set(null);
    fixture.detectChanges();
    expect(q(el, 'tracker-set-yes-s1').textContent).toContain('Set MyAnimeList to 10 ch');
    q(el, 'tracker-set-yes-s1').click();
    const req = http.expectOne(`${BASE}/entries/s1/set`);
    expect(req.request.body).toEqual({ chapters: 10, volumes: null });
    req.flush(entry({ remoteChapters: 10, lastSentChapters: 10 }));
    fixture.detectChanges();
    expect(q(el, 'tracker-set-form-s1')).toBeNull();
  });

  it('refuses a negative or fractional number', () => {
    const { fixture, el, c } = create([entry()]);
    q(el, 'tracker-set-s1').click();
    c.setChapters.set(-1);
    fixture.detectChanges();
    expect((q(el, 'tracker-set-yes-s1') as HTMLButtonElement).disabled).toBe(true);
    c.setChapters.set(1.5);
    fixture.detectChanges();
    expect((q(el, 'tracker-set-yes-s1') as HTMLButtonElement).disabled).toBe(true);
  });

  it("Don't sync and Sync again", () => {
    const { fixture, http, el } = create([entry()]);
    q(el, 'tracker-dont-sync-s1').click();
    const req = http.expectOne(`${BASE}/entries/s1/dont-sync`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ dontSync: true });
    req.flush(entry({ dontSync: true, state: 'DontSync' }));
    fixture.detectChanges();
    expect(q(el, 'tracker-dont-sync-s1').textContent).toContain('Sync again');
    expect(q(el, 'tracker-sync-now-s1')).toBeNull();
  });

  it('the preview reads 50 at a time and says that nothing changed on the tracker', () => {
    const { fixture, http, el } = create([]);
    expect(q(el, 'tracker-entries-empty-mal')).not.toBeNull();
    q(el, 'tracker-preview-mal').click();
    http.expectOne(`${BASE}/preview`).flush({ entries: [entry({ state: 'Held', heldChapters: 20 })], remaining: 70, capReached: false });
    fixture.detectChanges();
    expect(q(el, 'tracker-entries-notice-mal').textContent).toContain('Nothing was changed on MyAnimeList');
    expect(q(el, 'tracker-preview-mal').textContent).toContain('Check 50 more (70 left)');
    expect(q(el, 'tracker-entry-s1')).not.toBeNull();
  });

  it('Send all asks first, then reports the counts and reloads', () => {
    const { fixture, http, el } = create([entry({ state: 'Held', heldChapters: 40 })]);
    q(el, 'tracker-send-all-mal').click();
    fixture.detectChanges();
    expect(q(el, 'tracker-send-all-confirm-mal').textContent).toContain('never lowered');
    http.expectNone(`${BASE}/entries/send-all`);
    q(el, 'tracker-send-all-yes-mal').click();
    http.expectOne(`${BASE}/entries/send-all`).flush({ sent: 1, skipped: 0, failed: 0, capReached: false });
    http.expectOne(`${BASE}/entries`).flush([entry()]);
    fixture.detectChanges();
    expect(q(el, 'tracker-entries-notice-mal').textContent).toContain('Sent 1');
  });

  it('an account that cannot sync keeps the tracker actions off', () => {
    const { el } = create([entry({ state: 'Held', heldChapters: 4 })], false);
    expect(q(el, 'tracker-sync-now-s1')).toBeNull();
    expect((q(el, 'tracker-send-s1') as HTMLButtonElement).disabled).toBe(true);
    expect((q(el, 'tracker-preview-mal') as HTMLButtonElement).disabled).toBe(true);
  });

  it('pages a long list', () => {
    const list = Array.from({ length: 60 }, (_, i) => entry({ seriesId: `s${i}`, title: `Series ${String(i).padStart(2, '0')}` }));
    const { fixture, el } = create(list);
    expect(el.querySelectorAll('li')).toHaveLength(50);
    q(el, 'tracker-entries-more-mal').click();
    fixture.detectChanges();
    expect(el.querySelectorAll('li')).toHaveLength(60);
  });
});
