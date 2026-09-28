import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { MetadataReviewItemDto, MetadataReviewTab } from '../../../core/api/api-types';
import { reviewItem } from '../admin-metadata/metadata-admin.testing';
import { MetadataApiService } from '../metadata-api.service';
import { ReviewRowActionEvent, ReviewRowComponent, rowActions } from './review-row.component';

/**
 * One review row (stage 2, section 5): candidates as a radio list, reason chips, archive
 * rows and archive groups marked, the other candidates' posters ONLY when expanded (each costs a request);
 * your cover beside the SELECTED series' cover, always shown (1.28.0).
 */
describe('ReviewRowComponent', () => {
  function create(item: MetadataReviewItemDto = reviewItem(), tab: MetadataReviewTab = 'NeedsReview',
    opts: { expanded?: boolean; compact?: boolean; rank?: number } = {}) {
    TestBed.configureTestingModule({
      imports: [ReviewRowComponent],
      providers: [provideNoopAnimations(),
        { provide: MetadataApiService, useValue: { candidateImageUrl: (t: string) => `/api/v1/admin/metadata/candidates/${t}/image` } }],
    });
    const fixture = TestBed.createComponent(ReviewRowComponent);
    fixture.componentRef.setInput('item', item);
    fixture.componentRef.setInput('tab', tab);
    fixture.componentRef.setInput('expanded', !!opts.expanded);
    fixture.componentRef.setInput('compact', !!opts.compact);
    fixture.componentRef.setInput('rank', opts.rank ?? 1);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const events: ReviewRowActionEvent[] = [];
    fixture.componentInstance.action.subscribe((e) => events.push(e));
    return { fixture, el, events, all: (s: string) => Array.from(el.querySelectorAll(s)) as HTMLElement[] };
  }

  it('lists the stored candidates with their line, score and reason chips', () => {
    const { all, el } = create();
    const cands = all('[data-testid="review-candidate"]');
    expect(cands).toHaveLength(2);
    expect(cands[0].textContent).toContain('Synthetic Saga');
    expect(cands[0].textContent).toContain('Manga · 2014 · 14 vols');
    expect(cands[0].textContent).toContain('90%');
    expect(all('[data-testid="review-reason"]').map((c) => c.textContent!.trim())).toEqual(['Close second']);
    expect(el.textContent).toContain('Library One');
    expect(el.textContent).toContain('24 items');
  });

  it('shows your cover next to the SELECTED candidate\'s cover, and follows the radio choice (1.28.0, owner)', () => {
    const first = create(reviewItem({ coverUrl: '/api/v1/items/a9/cover' }), 'NeedsReview', { rank: 1 });
    const src = (f: typeof first, id: string) => f.el.querySelector(`[data-testid="${id}"]`)!.getAttribute('src');
    expect(src(first, 'review-local-cover')).toBe('/api/v1/items/a9/cover');
    expect(src(first, 'review-series-cover')).toBe('/api/v1/admin/metadata/candidates/tok1/image');
    first.fixture.componentRef.setInput('rank', 2); // the admin picks the second candidate
    first.fixture.detectChanges();
    expect(src(first, 'review-series-cover')).toBe('/api/v1/admin/metadata/candidates/tok2/image');
    expect(first.el.textContent).toContain('Selected');
  });

  it('a linked row compares with the link\'s stored poster (no provider request)', () => {
    const { el } = create(reviewItem({ candidates: [], link: {
      state: 'Auto', provider: 'mangaupdates', externalId: '1', recordId: 'r1', title: 'Synthetic Saga', matchMethod: 'Auto',
      matchScore: 1, imageUrl: '/api/v1/nodes/n1/series-info/image?v=r1', updatedAt: '2026-09-28T00:00:00Z' } as never }), 'AutoLinked');
    expect(el.querySelector('[data-testid="review-series-cover"]')!.getAttribute('src')).toBe('/api/v1/nodes/n1/series-info/image?v=r1');
    expect(el.textContent).toContain('Linked');
  });

  it('loads NO other candidate posters until the row is expanded', () => {
    expect(create().all('[data-testid="review-poster"]')).toHaveLength(0);
    TestBed.resetTestingModule();
    const posters = create(reviewItem(), 'NeedsReview', { expanded: true }).all('[data-testid="review-poster"]') as HTMLImageElement[];
    expect(posters.map((p) => p.getAttribute('src'))).toEqual([
      '/api/v1/admin/metadata/candidates/tok1/image', '/api/v1/admin/metadata/candidates/tok2/image']);
  });

  it('marks archive rows and archive groups', () => {
    const { el } = create(reviewItem({ nodeKind: 'Archive', memberNodeIds: ['b', 'c'], matchLevel: 'Archive' }));
    expect(el.querySelector('[data-testid="review-archive"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="review-group"]')!.textContent).toContain('Archive group · 3 archives');
    expect(el.textContent).toContain('Archive match');
    // An archive's own cover is local; no candidate poster is involved.
    expect(el.querySelector('[data-testid="review-local-cover"]')!.getAttribute('src')).toBe('/api/v1/items/n1/cover');
  });

  it('shows a folder\'s cover (its first archive, as browse) instead of a folder icon', () => {
    const { el } = create(reviewItem({ nodeId: 'f1', nodeKind: 'Folder', coverUrl: '/api/v1/items/a9/cover' }), 'NeedsReview');
    expect(el.querySelector('[data-testid="review-local-cover"]')!.getAttribute('src')).toBe('/api/v1/items/a9/cover');
  });

  it('emits Accept with the chosen rank and the tab\'s other actions', () => {
    const { el, events } = create(reviewItem(), 'NeedsReview', { rank: 2 });
    (el.querySelector('[data-testid="review-accept"]') as HTMLButtonElement).click();
    (el.querySelector('[data-testid="review-dontMatch"]') as HTMLButtonElement).click();
    expect(events.map((e) => [e.action, e.rank])).toEqual([['accept', 2], ['dontMatch', undefined]]);
    expect(el.querySelector('[data-testid="review-later"]')).toBeNull(); // removed (owner, 2026-09-26)
  });

  it('shows the current link on the Auto-linked tab and hides inline actions on phone', () => {
    const item = reviewItem({ candidates: [], link: { state: 'Auto', title: 'Synthetic Saga', matchScore: 0.95,
      updatedAt: '2026-09-24T00:00:00Z', imageUrl: '/api/v1/metadata/images/x' } });
    const { el } = create(item, 'AutoLinked');
    expect(el.querySelector('[data-testid="review-link"]')!.textContent).toContain('auto');
    expect(el.querySelector('[data-testid="review-link"]')!.textContent).toContain('Overall 95%');
    expect(el.querySelector('[data-testid="review-confirm"]')).not.toBeNull();
    TestBed.resetTestingModule();
    expect(create(item, 'AutoLinked', { compact: true }).el.querySelector('[data-testid="review-confirm"]')).toBeNull();
  });

  it('offers per-tab actions', () => {
    const it = reviewItem();
    expect(rowActions('Unmatched', it).map((a) => a.action)).toEqual(['identify', 'dontMatch']);
    expect(rowActions('DontMatch', it).map((a) => a.action)).toEqual(['clearDontMatch']);
    expect(rowActions('MissingFolders', it).map((a) => a.action)).toEqual(['reattach', 'deleteMissing']);
    expect(rowActions('Confirmed', it).map((a) => a.action)).toEqual(['identify', 'unlink']);
  });
});
