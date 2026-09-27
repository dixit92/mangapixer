import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';

import { SERIES_INFO_CACHE_SIZE, SERIES_INFO_CACHE_TTL_MS, SeriesInfoCacheService } from './series-info-cache.service';
import { SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from '../../features/metadata/metadata-api.service';
import { MetadataStateService } from '../../features/metadata/metadata-state.service';
import { seriesInfo } from '../../features/metadata/series-info.testing';

/**
 * The hover summary's client cache (1.27.0): one GET per node, shared while in flight,
 * bounded (least recently used goes first), expiring, and dropped for a node whenever
 * `MetadataStateService` announces a link change for it.
 */
describe('SeriesInfoCacheService', () => {
  let getSeriesInfo: ReturnType<typeof vi.fn>;

  function setup() {
    getSeriesInfo = vi.fn((id: string) => of(seriesInfo({ nodeId: id })));
    TestBed.configureTestingModule({ providers: [{ provide: MetadataApiService, useValue: { getSeriesInfo } }] });
    return { cache: TestBed.inject(SeriesInfoCacheService), state: TestBed.inject(MetadataStateService) };
  }

  afterEach(() => vi.useRealTimers());

  it('fetches a node once and serves it from memory afterwards', () => {
    const { cache } = setup();
    const seen: string[] = [];
    cache.get('a').subscribe((i) => seen.push(i.nodeId));
    cache.get('a').subscribe((i) => seen.push(i.nodeId));
    expect(seen).toEqual(['a', 'a']);
    expect(getSeriesInfo).toHaveBeenCalledTimes(1);
    expect(cache.peek('a')?.nodeId).toBe('a');
  });

  it('shares a request in flight', () => {
    const { cache } = setup();
    const slow = new Subject<SeriesInfoDto>();
    getSeriesInfo.mockReturnValue(slow);
    const seen: string[] = [];
    cache.get('a').subscribe((i) => seen.push('1:' + i.nodeId));
    cache.get('a').subscribe((i) => seen.push('2:' + i.nodeId));
    slow.next(seriesInfo({ nodeId: 'a' }));
    slow.complete();
    expect(getSeriesInfo).toHaveBeenCalledTimes(1);
    expect(seen).toEqual(['1:a', '2:a']);
  });

  it('a link change for the node drops it (also a response that was already in flight)', () => {
    const { cache, state } = setup();
    cache.get('a').subscribe();
    state.announce('a', false);
    expect(cache.peek('a')).toBeNull();

    const slow = new Subject<SeriesInfoDto>();
    getSeriesInfo.mockReturnValue(slow);
    cache.get('b').subscribe();
    state.announce('b', true);
    slow.next(seriesInfo({ nodeId: 'b' }));
    expect(cache.peek('b')).toBeNull();
  });

  it(`keeps at most ${SERIES_INFO_CACHE_SIZE} nodes, evicting the least recently used`, () => {
    const { cache } = setup();
    for (let i = 0; i < SERIES_INFO_CACHE_SIZE; i++) cache.get('n' + i).subscribe();
    cache.peek('n0'); // n0 is now the most recent
    cache.get('extra').subscribe();
    expect(cache.size).toBe(SERIES_INFO_CACHE_SIZE);
    expect(cache.peek('n0')).not.toBeNull();
    expect(cache.peek('n1')).toBeNull();
  });

  it('expires entries after the TTL', () => {
    vi.useFakeTimers();
    const { cache } = setup();
    cache.get('a').subscribe();
    vi.advanceTimersByTime(SERIES_INFO_CACHE_TTL_MS + 1);
    expect(cache.peek('a')).toBeNull();
    cache.get('a').subscribe();
    expect(getSeriesInfo).toHaveBeenCalledTimes(2);
  });
});
