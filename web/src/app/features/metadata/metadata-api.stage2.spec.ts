import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { MetadataApiService } from './metadata-api.service';
import { MetadataReviewStateService } from './metadata-review-state.service';
import { summary } from './admin-metadata/metadata-admin.testing';

/**
 * Stage-2 calls of MetadataApiService against lane B's contract (`7e98e13`): method,
 * URL, query and body of every route the admin page, review dashboard and flags use.
 */
describe('MetadataApiService stage 2', () => {
  let api: MetadataApiService;
  let http: HttpTestingController;
  const A = '/api/v1/admin/metadata';

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(MetadataApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the review summary and tabs with only the given query values', () => {
    api.getReviewSummary().subscribe();
    http.expectOne(`${A}/review/summary`).flush(summary());
    api.getReviewSummary('lib1').subscribe();
    http.expectOne(`${A}/review/summary?library=lib1`).flush(summary());
    api.getReview('AutoLinked', null, 'c2', 25).subscribe();
    const r = http.expectOne((req) => req.url === `${A}/review`);
    expect(r.request.params.get('tab')).toBe('AutoLinked');
    expect(r.request.params.has('library')).toBe(false);
    expect(r.request.params.get('cursor')).toBe('c2');
    expect(r.request.params.get('limit')).toBe('25');
    r.flush({ tab: 'AutoLinked', items: [], total: 0 });
  });

  it('accepts a candidate by rank and sends bulk actions', () => {
    api.acceptCandidate('n1', 2).subscribe();
    const accept = http.expectOne({ method: 'POST', url: `${A}/review/n1/accept` });
    expect(accept.request.body).toEqual({ rank: 2 });
    accept.flush({ nodeId: 'n1' });
    api.reviewBulk('DontMatch', ['a', 'b']).subscribe();
    const bulk = http.expectOne({ method: 'POST', url: `${A}/review/bulk` });
    expect(bulk.request.body).toEqual({ action: 'DontMatch', nodeIds: ['a', 'b'] });
    bulk.flush({ action: 'DontMatch', succeeded: 2, failed: 0, results: [] });
  });

  it('re-attaches and deletes missing folders', () => {
    api.reattachMissing('old', 'new').subscribe();
    const re = http.expectOne({ method: 'POST', url: `${A}/missing/old/reattach` });
    expect(re.request.body).toEqual({ targetNodeId: 'new' });
    re.flush({ nodeId: 'old', targetNodeId: 'new', link: true, precedence: false, readerDefault: false, content: false });
    api.deleteMissing('old').subscribe();
    http.expectOne({ method: 'DELETE', url: `${A}/missing/old` }).flush(null);
  });

  it('lists and cancels runs, estimates and starts a library match', () => {
    api.getRuns().subscribe();
    http.expectOne(`${A}/runs?limit=20`).flush({ status: { enabled: false, active: false, pending: 0 }, items: [] });
    api.cancelRun('r1').subscribe();
    http.expectOne({ method: 'POST', url: `${A}/runs/r1/cancel` }).flush({});
    api.getMatchEstimate('lib1').subscribe();
    http.expectOne(`${A}/libraries/lib1/match/estimate`).flush({});
    api.getMatchEstimate('lib1', true).subscribe();
    http.expectOne(`${A}/libraries/lib1/match/estimate?retryUnmatched=true`).flush({});
    api.matchLibrary('lib1', { reviewFirst: true, retryUnmatched: false }).subscribe();
    const m = http.expectOne({ method: 'POST', url: `${A}/libraries/lib1/match` });
    expect(m.request.body).toEqual({ reviewFirst: true, retryUnmatched: false });
    m.flush({});
  });

  it('flags: create, mine, list and resolve', () => {
    api.createFlag('n1', { reason: 'NotOneSeries', note: 'x' }).subscribe();
    const c = http.expectOne({ method: 'POST', url: '/api/v1/nodes/n1/series-info/flags' });
    expect(c.request.body).toEqual({ reason: 'NotOneSeries', note: 'x' });
    c.flush({});
    api.getMyFlag('n1').subscribe();
    http.expectOne({ method: 'GET', url: '/api/v1/nodes/n1/series-info/flags/mine' }).flush({ canFlag: true });
    api.getFlags('resolved', 'lib1').subscribe();
    http.expectOne(`${A}/flags?state=resolved&library=lib1&limit=50`).flush({ items: [], total: 0 });
    api.resolveFlag('f1', 'Dismissed').subscribe();
    const r = http.expectOne({ method: 'POST', url: `${A}/flags/f1/resolve` });
    expect(r.request.body).toEqual({ outcome: 'Dismissed' });
    r.flush({});
  });

  it('reads, sets and clears a folder Content value', () => {
    api.getFolderContent('f1').subscribe();
    http.expectOne({ method: 'GET', url: `${A}/folders/f1/content` }).flush({ nodeId: 'f1', effective: 'Auto' });
    api.setFolderContent('f1', 'DoujinshiAndAdultOneShots').subscribe();
    const put = http.expectOne({ method: 'PUT', url: `${A}/folders/f1/content` });
    expect(put.request.body).toEqual({ content: 'DoujinshiAndAdultOneShots' });
    put.flush({ nodeId: 'f1', effective: 'DoujinshiAndAdultOneShots' });
    api.clearFolderContent('f1').subscribe();
    http.expectOne({ method: 'DELETE', url: `${A}/folders/f1/content` }).flush({ nodeId: 'f1', effective: 'Auto' });
    api.rematchFolderContent('f1').subscribe();
    http.expectOne({ method: 'POST', url: `${A}/folders/f1/content/rematch` }).flush({ affected: 3, queued: 3 });
  });

  it('maps a 501 (stage 2 not implemented yet) to an ApiError with the status', () => {
    let status: number | undefined;
    api.getReviewSummary().subscribe({ error: (e: { status?: number }) => (status = e.status) });
    http.expectOne(`${A}/review/summary`).flush(
      { error: 'not_implemented', message: 'Not implemented yet.', detail: null, correlationId: null },
      { status: 501, statusText: 'Not Implemented' });
    expect(status).toBe(501);
  });
});

describe('MetadataReviewStateService', () => {
  it('sums needs review + open flags for the badge and clears on failure', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const state = TestBed.inject(MetadataReviewStateService);
    const http = TestBed.inject(HttpTestingController);
    expect(state.attention()).toBe(0);
    state.refresh();
    http.expectOne('/api/v1/admin/metadata/review/summary').flush(summary({ needsReview: 5, openFlags: 2 }));
    expect(state.attention()).toBe(7);
    state.refresh();
    http.expectOne('/api/v1/admin/metadata/review/summary').flush(null, { status: 501, statusText: 'Not Implemented' });
    expect(state.summary()).toBeNull();
    expect(state.attention()).toBe(0);
    http.verify();
  });
});
