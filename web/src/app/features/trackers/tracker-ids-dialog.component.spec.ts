import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { vi } from 'vitest';

import { TrackerSeriesIdsDto } from '../../core/api/api-types';
import { TrackerIdsDialogComponent } from './tracker-ids-dialog.component';

const URL = '/api/v1/admin/trackers/nodes/n1/ids';

function ids(o: Partial<TrackerSeriesIdsDto> = {}): TrackerSeriesIdsDto {
  return {
    seriesId: 'r1',
    ids: [
      { tracker: 'mal', remoteId: '2', source: 'MangaDex', overrideId: null },
      { tracker: 'mangaupdates', remoteId: '51239621230', source: 'Link', overrideId: null },
    ],
    ...o,
  };
}

/** "Set MyAnimeList / MangaUpdates id" (1.43.0, owner Q6): the id in use and its source; a number or a series address. */
describe('TrackerIdsDialogComponent', () => {
  const close = vi.fn();

  function create(slug: string, answer: TrackerSeriesIdsDto) {
    close.mockClear();
    TestBed.configureTestingModule({
      imports: [TrackerIdsDialogComponent],
      providers: [
        provideNoopAnimations(), provideHttpClient(), provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useValue: { nodeId: 'n1', slug } },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    const fixture = TestBed.createComponent(TrackerIdsDialogComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(URL).flush(answer);
    fixture.detectChanges();
    return { fixture, http, el: fixture.nativeElement as HTMLElement, c: fixture.componentInstance };
  }

  const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement;

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('shows the MyAnimeList id in use and saves one read from a series address', () => {
    const { fixture, http, el, c } = create('mal', ids());
    expect(q(el, 'tracker-ids-current').textContent).toContain("In use: 2 (from the series' MangaDex record)");
    c.text.set('https://myanimelist.net/manga/13/One_Piece');
    fixture.detectChanges();
    q(el, 'tracker-ids-dialog').dispatchEvent(new Event('submit'));
    const req = http.expectOne(`${URL}/mal`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ remoteId: '13' });
    req.flush(ids());
    expect(close).toHaveBeenCalledWith(true);
  });

  it('says what it cannot read and keeps Save off', () => {
    const { fixture, el, c } = create('mangaupdates', ids());
    c.text.set('https://myanimelist.net/manga/13');
    fixture.detectChanges();
    expect(q(el, 'tracker-ids-invalid').textContent).toContain('mangaupdates.com/series');
    expect(c.canSave()).toBe(false);
  });

  it("an empty field removes the admin's id", () => {
    const answer = ids();
    answer.ids[0] = { tracker: 'mal', remoteId: '99', source: 'Admin', overrideId: '99' };
    const { fixture, http, el, c } = create('mal', answer);
    expect(q(el, 'tracker-ids-current').textContent).toContain('set by an admin');
    expect(c.text()).toBe('99');
    c.text.set('');
    fixture.detectChanges();
    c.save();
    expect(http.expectOne(`${URL}/mal`).request.body).toEqual({ remoteId: '' });
  });

  it('a folder without a series link cannot take an id', () => {
    const { el, c } = create('mal', { seriesId: null, ids: [] });
    expect(q(el, 'tracker-ids-not-linked').textContent).toContain('not linked to a series');
    expect(c.canSave()).toBe(false);
  });
});
