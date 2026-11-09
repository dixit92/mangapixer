import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ApiService } from '../../core/api/api.service';
import { RatingChange, RatingsStateService } from './ratings-state.service';

/** 1.52.0 lane R: the ratings calls - PUT / DELETE nodes/{id}/rating, the Favorites `rating` filter - and the change channel. */
describe('ratings API', () => {
  let api: ApiService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(ApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('sets a rating with PUT { score }', () => {
    api.setRating('n1', 7).subscribe();
    const req = httpMock.expectOne('/api/v1/nodes/n1/rating');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ score: 7 });
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('clears a rating with DELETE', () => {
    api.setRating('n1', null).subscribe();
    const req = httpMock.expectOne('/api/v1/nodes/n1/rating');
    expect(req.request.method).toBe('DELETE');
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('sends the Favorites rating filter and the My rating sort only when set', () => {
    api.getFavorites(null, 50, { sort: 'myRating', rating: 'rated' }).subscribe();
    const set = httpMock.expectOne((r) => r.url === '/api/v1/favorites');
    expect(set.request.params.get('sort')).toBe('myRating');
    expect(set.request.params.get('rating')).toBe('rated');
    set.flush({ items: [], totalCount: 0, nextCursor: null, hasMore: false });

    api.getFavorites(null, 50, { rating: 'all' }).subscribe();
    const none = httpMock.expectOne((r) => r.url === '/api/v1/favorites');
    expect(none.request.params.has('rating')).toBe(false);
    none.flush({ items: [], totalCount: 0, nextCursor: null, hasMore: false });

    api.getFavoritesJumpIndex({ rating: 'unrated' }).subscribe();
    const rail = httpMock.expectOne((r) => r.url === '/api/v1/favorites/jump-index');
    expect(rail.request.params.get('rating')).toBe('unrated');
    rail.flush({ buckets: [], direction: 'asc' });
  });

  it('announces a change only once the server took it', () => {
    const ratings = TestBed.inject(RatingsStateService);
    const seen: RatingChange[] = [];
    ratings.changed$.subscribe((c) => seen.push(c));
    ratings.setRating('n2', 4).subscribe();
    expect(seen).toEqual([]);
    httpMock.expectOne('/api/v1/nodes/n2/rating').flush(null, { status: 204, statusText: 'No Content' });
    expect(seen).toEqual([{ nodeId: 'n2', score: 4 }]);

    ratings.setRating('n2', 5).subscribe({ error: () => undefined });
    httpMock.expectOne('/api/v1/nodes/n2/rating').flush({ error: 'invalid_score' }, { status: 400, statusText: 'Bad Request' });
    expect(seen.length).toBe(1);
  });
});
