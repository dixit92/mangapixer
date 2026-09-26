import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import { MetadataApiService } from './metadata-api.service';
import { MetadataStateService, SeriesInfoChange, ownSeriesInfo } from './metadata-state.service';
import { seriesInfo } from './series-info.testing';

/** Series-link change channel (1.24.0 polish): announce, refresh from one GET, own-info scope. */
describe('MetadataStateService', () => {
  function create(getSeriesInfo: () => unknown) {
    const api = { getSeriesInfo: vi.fn(getSeriesInfo) };
    TestBed.configureTestingModule({ providers: [{ provide: MetadataApiService, useValue: api }] });
    const service = TestBed.inject(MetadataStateService);
    const changes: SeriesInfoChange[] = [];
    service.changed$.subscribe((c) => changes.push(c));
    return { service, api, changes };
  }

  it('announces a known outcome without a request', () => {
    const { service, api, changes } = create(() => of(seriesInfo()));
    service.announce('n1', true);
    expect(changes).toEqual([{ nodeId: 'n1', hasSeriesInfo: true }]);
    expect(api.getSeriesInfo).not.toHaveBeenCalled();
  });

  it('derives the new value from one series-info GET (own ComicInfo survives an Unlink)', () => {
    const { service, api, changes } = create(() =>
      of(seriesInfo({ state: 'ComicInfo', comicInfo: { itemsWithComicInfo: 2, itemsTotal: 3 } })));
    service.refresh('n1');
    expect(api.getSeriesInfo).toHaveBeenCalledTimes(1);
    expect(changes).toEqual([{ nodeId: 'n1', hasSeriesInfo: true }]);
  });

  it('announces nothing when the GET fails', () => {
    const { service, changes } = create(() => throwError(() => ({ error: 'x' })));
    service.refresh('n1');
    expect(changes).toEqual([]);
  });
});

describe('ownSeriesInfo', () => {
  const web = { provider: 'mangaupdates', providerName: 'MangaUpdates', fetchedAt: '2026-09-25T00:00:00Z' };

  it('counts the node\'s own confirmed link or its own ComicInfo', () => {
    expect(ownSeriesInfo(seriesInfo({ state: 'Web', web, link: { state: 'Confirmed', nodeId: 'n1', inherited: false } }))).toBe(true);
    expect(ownSeriesInfo(seriesInfo({ state: 'ComicInfo', comicInfo: { itemsWithComicInfo: 1, itemsTotal: 1 } }))).toBe(true);
  });

  it('never counts inherited links, Don\'t match, hidden or missing information', () => {
    expect(ownSeriesInfo(seriesInfo({ state: 'Web', web, link: { state: 'Confirmed', nodeId: 'parent', inherited: true } }))).toBe(false);
    expect(ownSeriesInfo(seriesInfo({ state: 'DontMatch', link: { state: 'DontMatch', nodeId: 'n1', inherited: false } }))).toBe(false);
    expect(ownSeriesInfo(seriesInfo({ state: 'None', title: null }))).toBe(false);
    expect(ownSeriesInfo(null)).toBe(false);
  });
});
