import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';

import { MetadataReviewItemDto, MetadataReviewTab } from '../../../core/api/api-types';
import { reviewItem } from '../admin-metadata/metadata-admin.testing';
import { MetadataApiService } from '../metadata-api.service';
import { QueuedImageDirective } from './queued-image.directive';
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
      providers: [provideNoopAnimations(), provideRouter([]),
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

  it('a refused candidate cover is retried, then offers "No cover" with a retry - never a broken image (1.29.0)', () => {
    vi.useFakeTimers();
    try {
      const { fixture, el } = create();
      const img = () => el.querySelector('[data-testid="review-series-cover"]') as HTMLImageElement;
      const retryButton = () => el.querySelector('[data-testid="review-series-retry"]') as HTMLButtonElement | null;
      for (const delay of QueuedImageDirective.Backoff) {
        img().dispatchEvent(new Event('error'));
        fixture.detectChanges();
        expect(img().hasAttribute('src')).toBe(false); // nothing to draw a broken-image icon with
        expect(retryButton()).toBeNull(); // still trying
        vi.advanceTimersByTime(delay);
        expect(img().getAttribute('src')).toMatch(/^\/api\/v1\/admin\/metadata\/candidates\/tok1\/image\?r=\d$/);
      }
      img().dispatchEvent(new Event('error'));
      fixture.detectChanges();
      expect(retryButton()!.textContent).toContain('No cover');
      retryButton()!.click();
      fixture.detectChanges();
      expect(img().getAttribute('src')).toBe('/api/v1/admin/metadata/candidates/tok1/image');
      expect(retryButton()).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it('opens the folder in browse: a folder row its own folder, an archive row its containing folder (1.29.0, owner)', () => {
    const href = (item: MetadataReviewItemDto) => {
      TestBed.resetTestingModule();
      return create(item).el.querySelector('[data-testid="review-open-folder"]')?.getAttribute('href');
    };
    expect(href(reviewItem({ nodeId: 'f1', nodeKind: 'Folder' }))).toBe('/libraries/lib1/browse/f1');
    expect(href(reviewItem({ nodeId: 'a1', nodeKind: 'Archive', parentNodeId: 'f9' }))).toBe('/libraries/lib1/browse/f9');
    expect(href(reviewItem({ nodeId: 'a2', nodeKind: 'Archive', parentNodeId: null }))).toBe('/libraries/lib1/browse');
    expect(href(reviewItem({ nodeId: 'g1', nodeKind: 'Folder', missing: true }))).toBeUndefined(); // gone: nothing to open
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
    (el.querySelector('[data-testid="review-later"]') as HTMLButtonElement).click();
    expect(events.map((e) => [e.action, e.rank])).toEqual([['accept', 2], ['dontMatch', undefined], ['later', undefined]]);
    expect(el.querySelector('[data-testid="review-later-tag"]')).toBeNull();
  });

  it('1.33.0: a row set aside shows the Later tag and offers "Not later" on the same key', () => {
    const item = reviewItem({ laterAt: '2026-10-03T12:00:00Z' });
    const { el, events } = create(item, 'NeedsReview');
    expect(el.querySelector('[data-testid="review-later-tag"]')!.textContent).toContain('Later');
    expect(el.querySelector('[data-testid="review-later"]')).toBeNull();
    (el.querySelector('[data-testid="review-notLater"]') as HTMLButtonElement).click();
    expect(events.map((e) => e.action)).toEqual(['notLater']);
    expect(rowActions('NeedsReview', item).map((a) => [a.action, a.key])).toEqual([
      ['accept', 'a'], ['identify', 'i'], ['dontMatch', 'd'], ['notLater', 'l']]);
    expect(rowActions('NeedsReview', reviewItem()).at(-1)!.action).toBe('later');
    // Only Needs review offers it.
    expect(rowActions('Unmatched', reviewItem()).some((a) => a.action === 'later')).toBe(false);
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

  // 1.30.0 (owner): a series and its spin-off are shown together, flagged as one family, each with its role.
  function familyItem(): MetadataReviewItemDto {
    const base = reviewItem();
    const [first, second] = base.candidates!;
    return reviewItem({
      reasons: ['subtitle_family', 'series_family'],
      candidates: [
        { ...first, rank: 1, externalId: '902', title: 'Synthetic Saga - Before the Frost', familyGroup: 1, familyRole: 'prequel',
          reasons: ['subtitle_family', 'series_family'] },
        { ...second, rank: 2, externalId: '777', title: 'Unrelated Saga', familyGroup: null, familyRole: null },
        { ...second, rank: 3, externalId: '901', title: 'Synthetic Saga', familyGroup: 1, familyRole: 'main_story', imageToken: 'tok3' },
      ],
    });
  }

  it('groups a series family in one block with the note and each role, at the place of its best-ranked member', () => {
    const { all, el } = create(familyItem());
    const family = all('[data-testid="review-family"]');
    expect(family).toHaveLength(1);
    expect(family[0].textContent).toContain('Same series family - check which one');
    const inFamily = Array.from(family[0].querySelectorAll('[data-testid="review-candidate"]')).map((c) => c.textContent!);
    expect(inFamily).toHaveLength(2);
    expect(inFamily[0]).toContain('Before the Frost');
    expect(inFamily[1]).toContain('Synthetic Saga');
    expect(all('[data-testid="review-family-role"]').map((r) => r.textContent!.trim())).toEqual(['Prequel', 'Main story']);
    // The family first, then the candidate of its own (ranks unchanged).
    expect(all('[data-testid="review-candidate"]').map((c) => c.querySelector('.cand-title')!.textContent)).toEqual([
      'Synthetic Saga - Before the Frost', 'Synthetic Saga', 'Unrelated Saga']);
    expect(all('[data-testid="review-reason"]').map((c) => c.textContent!.trim())).toEqual(['Spin-off or main story?', 'Series family']);
    expect(el.textContent).not.toContain('subtitle_family');
    // Inside the block the family chips are not repeated on the candidate (the heading says it).
    expect(family[0].querySelectorAll('.chip')).toHaveLength(0);
  });

  it('a family member keeps its rank: choosing it and accepting sends that rank', () => {
    const { fixture, el, events } = create(familyItem());
    const chosen: number[] = [];
    fixture.componentInstance.choose.subscribe((r) => chosen.push(r));
    const main = Array.from(el.querySelectorAll('[data-testid="review-family"] input[type="radio"]'))[1] as HTMLInputElement;
    main.click();
    fixture.detectChanges();
    expect(chosen).toEqual([3]);
    fixture.componentRef.setInput('rank', 3);
    fixture.detectChanges();
    (el.querySelector('[data-testid="review-accept"]') as HTMLButtonElement).click();
    expect(events.map((e) => [e.action, e.rank])).toEqual([['accept', 3]]);
    // The selected series' cover follows the family member.
    expect(el.querySelector('[data-testid="review-series-cover"]')!.getAttribute('src')).toBe('/api/v1/admin/metadata/candidates/tok3/image');
  });

  it('shows no family block or role when no other stored candidate is its family', () => {
    const lone = familyItem();
    lone.candidates = lone.candidates!.filter((c) => c.externalId !== '901');
    const { all } = create(lone);
    expect(all('[data-testid="review-family"]')).toHaveLength(0);
    expect(all('[data-testid="review-family-role"]')).toHaveLength(0);
    expect(all('[data-testid="review-candidate"]')).toHaveLength(2);
  });

  it('offers per-tab actions', () => {
    const it = reviewItem();
    expect(rowActions('Unmatched', it).map((a) => a.action)).toEqual(['identify', 'dontMatch']);
    expect(rowActions('DontMatch', it).map((a) => a.action)).toEqual(['clearDontMatch']);
    expect(rowActions('MissingFolders', it).map((a) => a.action)).toEqual(['reattach', 'deleteMissing']);
    expect(rowActions('Confirmed', it).map((a) => a.action)).toEqual(['identify', 'unlink']);
  });

  it('1.31.0: marks a row being checked again and the duplicate numbers below a folder', () => {
    const plain = create(reviewItem());
    expect(plain.el.querySelector('[data-testid="review-checking-again"]')).toBeNull();
    expect(plain.el.querySelector('[data-testid="review-duplicates"]')).toBeNull();
    TestBed.resetTestingModule();

    const marked = create(reviewItem({ checkingAgain: true, duplicateChapters: 2, duplicateVolumes: 1 }));
    expect(marked.el.querySelector('[data-testid="review-checking-again"]')!.textContent).toContain('Checking again');
    expect(marked.el.querySelector('[data-testid="review-duplicates"]')!.textContent).toBe('2 duplicate chapters, 1 duplicate volume');
    // The earlier result is still what the row shows while it waits.
    expect(marked.all('[data-testid="review-candidate"]')).toHaveLength(2);
    TestBed.resetTestingModule();

    expect(create(reviewItem({ duplicateChapters: 1 })).el.querySelector('[data-testid="review-duplicates"]')!.textContent).toBe('1 duplicate chapter');
  });
});
