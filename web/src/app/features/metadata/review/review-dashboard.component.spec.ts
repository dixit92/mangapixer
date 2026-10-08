import { BreakpointObserver } from '@angular/cdk/layout';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';

import { MetadataReviewItemDto, MetadataReviewPageDto, MetadataReviewTab, SetArtistFolderRequest } from '../../../core/api/api-types';
import { ArtistFolderDialogService } from '../artist-folder/artist-folder-dialog.service';
import { reviewItem, summary } from '../admin-metadata/metadata-admin.testing';
import { IdentifyDialogService } from '../identify-dialog/identify-dialog.service';
import { MetadataApiService } from '../metadata-api.service';
import { MetadataReviewStateService } from '../metadata-review-state.service';
import { MetadataStateService } from '../metadata-state.service';
import { ReviewDashboardComponent } from './review-dashboard.component';

/** A snackbar whose Undo / dismissal the test drives (a new one dismisses the previous one). */
function fakeSnackBar() {
  const refs: { label: string; action: Subject<void>; dismissed: Subject<{ dismissedByAction: boolean }> }[] = [];
  const bar = {
    open: vi.fn((label: string) => {
      const prev = refs[refs.length - 1];
      if (prev) prev.dismissed.next({ dismissedByAction: false });
      const ref = { label, action: new Subject<void>(), dismissed: new Subject<{ dismissedByAction: boolean }>() };
      refs.push(ref);
      return { onAction: () => ref.action, afterDismissed: () => ref.dismissed };
    }),
  };
  const last = () => refs[refs.length - 1];
  return {
    bar, refs, last,
    undo: () => { last().action.next(); last().dismissed.next({ dismissedByAction: true }); },
    close: () => last().dismissed.next({ dismissedByAction: false }),
  };
}

/**
 * The review dashboard (stage 2, section 5), HTTP mocked at the service: tabs + counts,
 * deferred-commit Undo (nothing is sent inside the Undo window), bulk with partial
 * failure, keyboard triage, Flags hand-off, missing-folder re-attach, phone bar.
 */
describe('ReviewDashboardComponent', () => {
  const rows = [
    reviewItem({ nodeId: 'n1', displayName: 'Alpha Saga' }),
    reviewItem({ nodeId: 'n2', displayName: 'Beta Saga' }),
    reviewItem({ nodeId: 'n3', displayName: 'Gamma Saga' }),
  ];

  function create(opts: { pages?: Partial<Record<MetadataReviewTab, MetadataReviewItemDto[]>>; phone?: boolean;
    failIds?: string[]; reviewError?: number; tab?: MetadataReviewTab; apiOverrides?: Record<string, unknown>;
    /** What the artist-folder dialog answers (default: Beta Painter, Story & art; null = cancelled). */
    artist?: SetArtistFolderRequest | null } = {}) {
    const snack = fakeSnackBar();
    const pages = opts.pages ?? { NeedsReview: rows };
    const api = {
      candidateImageUrl: (t: string) => `/api/v1/admin/metadata/candidates/${t}/image`,
      getReviewSummary: vi.fn(() => of(summary({ needsReview: 3 }))),
      getReview: vi.fn((tab: MetadataReviewTab): ReturnType<MetadataApiService['getReview']> => (opts.reviewError
        ? throwError(() => ({ status: opts.reviewError, message: 'x' }))
        : of<MetadataReviewPageDto>({ tab, items: pages[tab] ?? [], total: (pages[tab] ?? []).length }))),
      acceptCandidate: vi.fn((nodeId: string) => of({ nodeId })),
      reviewBulk: vi.fn((action: string, nodeIds: string[]) => of({
        action, succeeded: nodeIds.length, failed: 0,
        results: nodeIds.map((nodeId) => ({ nodeId, code: opts.failIds?.includes(nodeId) ? 'not_found' : 'ok' })),
      })),
      clearDontMatch: vi.fn((nodeId: string) => of({ nodeId })),
      acceptCollection: vi.fn((nodeId: string) => of({ change: { nodeId } })),
      clearCollection: vi.fn((nodeId: string) => of({ nodeId })),
      acceptArtist: vi.fn((nodeId: string) => of({ change: { nodeId }, artist: { name: 'Beta Painter', role: 'author' }, queued: 3 })),
      clearArtistFolder: vi.fn((nodeId: string) => of({ nodeId })),
      deleteMissing: vi.fn(() => of(undefined)),
      setReviewLater: vi.fn((_nodeId: string, _on: boolean) => of(undefined)),
      getReviewAuthors: vi.fn(() => of({ items: [{ key: 'circlea', label: 'Circle A', count: 4, later: 1 }, { key: 'artistb', label: 'Artist B', count: 2 }] })),
      reattachMissing: vi.fn((nodeId: string, targetNodeId: string) => of({ nodeId, targetNodeId, link: true,
        precedence: false, readerDefault: false, content: false })),
      ...opts.apiOverrides,
    };
    const dialog = {
      openDialogs: [] as unknown[],
      open: vi.fn(() => ({ afterClosed: () => of({ targetNodeId: 't1', targetName: 'New Folder' }) })),
    };
    const identify = { open: vi.fn(() => Promise.resolve(true)) };
    const artist = { open: vi.fn((): Promise<SetArtistFolderRequest | undefined> => Promise.resolve(opts.artist === undefined
      ? { name: 'Beta Painter', role: 'author' } : (opts.artist ?? undefined))) };
    const reviewState = { refresh: vi.fn(), summary: signal(null) };
    const metadataState = { refresh: vi.fn() };
    TestBed.configureTestingModule({
      imports: [ReviewDashboardComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: MetadataApiService, useValue: api },
        { provide: MatSnackBar, useValue: snack.bar },
        { provide: MatDialog, useValue: dialog },
        { provide: IdentifyDialogService, useValue: identify },
        { provide: ArtistFolderDialogService, useValue: artist },
        { provide: MetadataReviewStateService, useValue: reviewState },
        { provide: MetadataStateService, useValue: metadataState },
        { provide: BreakpointObserver, useValue: { observe: () => of({ matches: !!opts.phone, breakpoints: {} }) } },
      ],
    });
    const fixture = TestBed.createComponent(ReviewDashboardComponent);
    fixture.componentRef.setInput('libraries', [{ id: 'lib1', name: 'Library One' }]);
    if (opts.tab) fixture.componentRef.setInput('initialTab', opts.tab);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const c = fixture.componentInstance;
    const names = () => { fixture.detectChanges(); return Array.from(el.querySelectorAll('[data-testid="review-name"]')).map((n) => n.textContent!.trim()); };
    const key = (k: string, target: EventTarget = document.body) => {
      const e = new KeyboardEvent('keydown', { key: k, bubbles: true });
      target.dispatchEvent(e);
      fixture.detectChanges();
    };
    return { fixture, el, c, api, snack, dialog, identify, artist, reviewState, metadataState, names, key };
  }

  it('loads the Needs review tab with the counts on the tabs', () => {
    const { el, api, names } = create();
    expect(api.getReview).toHaveBeenCalledWith('NeedsReview', null, null, 50, {});
    expect(names()).toEqual(['Alpha Saga', 'Beta Saga', 'Gamma Saga']);
    const tab = el.querySelector('[data-testid="review-tab-AutoLinked"]')!;
    expect(tab.textContent).toContain('Auto-linked');
    expect(tab.textContent).toContain('12');
    expect(el.querySelector('[data-testid="review-tab-NeedsReview"]')!.getAttribute('aria-selected')).toBe('true');
  });

  it('Accept hides the row at once but sends NOTHING until the Undo window closes', () => {
    const { c, api, snack, names } = create();
    c.choose('n1', 2);
    c.onRowAction({ action: 'accept', item: rows[0], rank: c.rankOf(rows[0]) });
    expect(names()).toEqual(['Beta Saga', 'Gamma Saga']);
    expect(c.count('needsReview')).toBe(2);
    expect(snack.last().label).toBe('Accepted "Synthetic Saga Returns" for Alpha Saga');
    expect(api.acceptCandidate).not.toHaveBeenCalled();
    snack.close();
    expect(api.acceptCandidate).toHaveBeenCalledWith('n1', 2);
    expect(names()).toEqual(['Beta Saga', 'Gamma Saga']);
    expect(api.getReviewSummary).toHaveBeenCalledTimes(2); // counts re-read after the change
  });

  it('Undo puts the row and the count back and sends nothing', () => {
    const { c, api, snack, names } = create();
    c.onRowAction({ action: 'dontMatch', item: rows[1] });
    expect(names()).toEqual(['Alpha Saga', 'Gamma Saga']);
    snack.undo();
    expect(names()).toEqual(['Alpha Saga', 'Beta Saga', 'Gamma Saga']);
    expect(c.count('needsReview')).toBe(3);
    expect(api.reviewBulk).not.toHaveBeenCalled();
  });

  it('bulk: acts on the selection, and a row the server refused comes back', () => {
    const { c, api, snack, names, el, fixture } = create({ failIds: ['n3'] });
    c.toggle('n1');
    c.toggle('n3');
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="review-bulkbar"]')!.textContent).toContain('Selected 2');
    (el.querySelector('[data-testid="bulk-DontMatch"]') as HTMLButtonElement).click();
    expect(names()).toEqual(['Beta Saga']);
    snack.close();
    expect(api.reviewBulk).toHaveBeenCalledWith('DontMatch', ['n1', 'n3']);
    expect(names()).toEqual(['Beta Saga', 'Gamma Saga']);
    expect(snack.last().label).toContain('1 item could not be changed (not_found)');
  });

  it('a new action sends the previous one first (only the last can be undone)', () => {
    const { c, api } = create();
    c.onRowAction({ action: 'accept', item: rows[0], rank: 1 });
    c.onRowAction({ action: 'dontMatch', item: rows[1] });
    expect(api.acceptCandidate).toHaveBeenCalledWith('n1', 1);
    expect(api.reviewBulk).not.toHaveBeenCalled();
  });

  it('keyboard: j/k move, a accepts the focused row, keys typed into a field are ignored', () => {
    const { c, api, snack, key } = create();
    key('j');
    expect(c.focusIndex()).toBe(1);
    key('k');
    key('j');
    const input = document.createElement('input');
    document.body.appendChild(input);
    key('a', input);
    expect(snack.bar.open).not.toHaveBeenCalled();
    input.remove();
    key('a');
    snack.close();
    expect(api.acceptCandidate).toHaveBeenCalledWith('n2', 1);
    key('x');
    expect(c.selected().has('n3')).toBe(true); // focus moved on to the next row
  });

  it('switches tabs (sending a pending action first), and hands Flags to the Flags tab', () => {
    const { c, api, el, fixture } = create({ pages: { NeedsReview: rows, AutoLinked: [reviewItem({ nodeId: 'z', candidates: [] })] } });
    const states: unknown[] = [];
    let flags = 0;
    c.stateChange.subscribe((s) => states.push(s));
    c.openFlags.subscribe(() => flags++);
    c.onRowAction({ action: 'accept', item: rows[0], rank: 1 });
    (el.querySelector('[data-testid="review-tab-AutoLinked"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(api.acceptCandidate).toHaveBeenCalledTimes(1);
    expect(api.getReview).toHaveBeenLastCalledWith('AutoLinked', null, null, 50, {});
    expect(states).toEqual([{ tab: 'AutoLinked', library: null }]);
    (el.querySelector('[data-testid="review-tab-Flags"]') as HTMLButtonElement).click();
    expect(flags).toBe(1);
    expect(c.tab()).toBe('AutoLinked');
  });

  it('filters by library', () => {
    const { c, api } = create();
    c.setLibrary('lib1');
    expect(api.getReview).toHaveBeenLastCalledWith('NeedsReview', 'lib1', null, 50, {});
    expect(api.getReviewSummary).toHaveBeenLastCalledWith('lib1');
  });

  it('explains a server without the review list (501)', () => {
    const { el } = create({ reviewError: 501 });
    expect(el.querySelector('[data-testid="review-error"]')!.textContent).toContain('not available on this server yet');
  });

  it('re-attaches a missing folder through the folder picker', async () => {
    // The dashboard lazy-loads the dialog; load it first so the first transform of that module (slow on a busy
    // machine) cannot outlast the waitFor below - it made this test flaky (1.28.0 RC).
    await import('./reattach-dialog.component');
    const missing = reviewItem({ nodeId: 'm1', displayName: 'Old Name', missing: true, candidates: [] });
    const { c, api, dialog, names } = create({ tab: 'MissingFolders', pages: { MissingFolders: [missing] } });
    c.onRowAction({ action: 'reattach', item: missing });
    await vi.waitFor(() => expect(dialog.open).toHaveBeenCalled());
    await vi.waitFor(() => expect(api.reattachMissing).toHaveBeenCalledWith('m1', 't1'));
    expect(names()).toEqual([]);
  });

  it('Identify removes the row after a link', async () => {
    const { c, identify, names } = create();
    c.onRowAction({ action: 'identify', item: rows[2] });
    expect(identify.open).toHaveBeenCalledWith('n3', 'link');
    await vi.waitFor(() => expect(names()).toEqual(['Alpha Saga', 'Beta Saga']));
  });

  it('phone: the focused row\'s actions sit in the bottom bar; long-press starts selecting', () => {
    vi.useFakeTimers();
    try {
      const { c, el, fixture, names } = create({ phone: true });
      names();
      expect(el.querySelector('[data-testid="review-accept"]')).toBeNull(); // no inline actions
      expect(el.querySelector('[data-testid="bar-accept"]')).not.toBeNull();
      c.pressStart({ pointerType: 'touch' } as PointerEvent, rows[1]);
      vi.advanceTimersByTime(600);
      fixture.detectChanges();
      expect(c.selectMode()).toBe(true);
      expect(c.selected().has('n2')).toBe(true);
      expect(el.querySelector('[data-testid="bulk-AcceptTop"]')).not.toBeNull();
      c.onRowTap(0, rows[0]); // the tap that ends the long-press is swallowed
      c.onRowTap(0, rows[0]);
      expect([...c.selected()]).toEqual(['n2', 'n1']);
    } finally {
      vi.useRealTimers();
    }
  });

  it('sends a pending action when the page is left', () => {
    const { c, api, fixture } = create();
    c.onRowAction({ action: 'accept', item: rows[0], rank: 1 });
    fixture.destroy();
    expect(api.acceptCandidate).toHaveBeenCalledWith('n1', 1);
  });

  /**
   * Regression (1.27.0 owner report): switching Auto-linked -> Confirmed right after
   * confirming a row must not reload the new tab until the deferred commit actually
   * landed - otherwise the just-confirmed row is missing until a manual refresh.
   */
  it('waits for a pending commit to settle before loading the tab switched to', () => {
    const bulk$ = new Subject<{ action: string; succeeded: number; failed: number; results: { nodeId: string; code: string }[] }>();
    const confirmed = reviewItem({ nodeId: 'z', displayName: 'Confirmed Saga', candidates: [] });
    const { c, api, el, fixture } = create({
      tab: 'AutoLinked',
      pages: { AutoLinked: rows, Confirmed: [confirmed] },
      apiOverrides: { reviewBulk: vi.fn(() => bulk$) },
    });
    c.onRowAction({ action: 'confirm', item: rows[0] });
    (el.querySelector('[data-testid="review-tab-Confirmed"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    // The Undo window already closed (a row action moved focus to a new tab), so the
    // commit was sent, but its response has not arrived yet: the new tab must not have
    // loaded from the server yet.
    expect(api.getReview).not.toHaveBeenCalledWith('Confirmed', null, null, 50, {});
    bulk$.next({ action: 'Confirm', succeeded: 1, failed: 0, results: [{ nodeId: 'n1', code: 'ok' }] });
    bulk$.complete();
    fixture.detectChanges();
    expect(api.getReview).toHaveBeenCalledWith('Confirmed', null, null, 50, {});
    expect(el.querySelectorAll('[data-testid="review-name"]')[0].textContent).toContain('Confirmed Saga');
  });

  // --- 1.33.0: Later, remembered on the server ---

  it('Later is sent at once and moves the row to the end of a fully loaded list; Undo brings it back', () => {
    const { c, api, el, snack, names } = create();
    c.onRowAction({ action: 'later', item: rows[0] });
    expect(api.setReviewLater).toHaveBeenCalledWith('n1', true);
    expect(names()).toEqual(['Beta Saga', 'Gamma Saga', 'Alpha Saga']);
    expect(el.querySelectorAll('[data-testid="review-later-tag"]')).toHaveLength(1);
    expect(snack.last().label).toBe('Set aside for later: Alpha Saga');
    expect(api.getReviewSummary).toHaveBeenCalledTimes(2); // the Later count is read again
    expect(api.reviewBulk).not.toHaveBeenCalled();

    snack.undo();
    expect(api.setReviewLater).toHaveBeenLastCalledWith('n1', false);
    expect(names()).toEqual(['Beta Saga', 'Gamma Saga', 'Alpha Saga']); // the server puts it back in order on the next load
    expect(el.querySelector('[data-testid="review-later-tag"]')).toBeNull();
  });

  it('Later with more pages to load: the row leaves the loaded list (paging reaches it at the end)', () => {
    const { c, names } = create({ apiOverrides: {
      getReview: vi.fn(() => of({ tab: 'NeedsReview', items: rows, total: 10, hasMore: true, nextCursor: '7' })) } });
    c.onRowAction({ action: 'later', item: rows[1] });
    expect(names()).toEqual(['Alpha Saga', 'Gamma Saga']);
    expect(c.total()).toBe(10); // still in Needs review
  });

  it('the l key sets the focused row aside, and brings a row set aside back', () => {
    const later = reviewItem({ nodeId: 'n9', displayName: 'Later Saga', laterAt: '2026-10-03T12:00:00Z' });
    const { api, key, names } = create({ pages: { NeedsReview: [...rows, later] } });
    key('l');
    expect(api.setReviewLater).toHaveBeenCalledWith('n1', true);
    expect(names()).toEqual(['Beta Saga', 'Gamma Saga', 'Later Saga', 'Alpha Saga']);
    key('j');
    key('j');
    key('l');
    expect(api.setReviewLater).toHaveBeenLastCalledWith('n9', false);
  });

  it('bulk Later sets the selected rows aside in one call', () => {
    const { c, api, names } = create();
    c.toggle('n1');
    c.toggle('n3');
    c.runBulk('Later');
    expect(api.reviewBulk).toHaveBeenCalledWith('Later', ['n1', 'n3']);
    expect(api.setReviewLater).not.toHaveBeenCalled();
    expect(names()).toEqual(['Beta Saga', 'Alpha Saga', 'Gamma Saga']);
  });

  it('the Later filter shows with a count, reloads the list, and drops a row brought back from it', () => {
    const later = reviewItem({ nodeId: 'n9', displayName: 'Later Saga', laterAt: '2026-10-03T12:00:00Z' });
    const { c, api, el, fixture, names } = create({ pages: { NeedsReview: [later] }, apiOverrides: {
      getReviewSummary: vi.fn(() => of(summary({ needsReview: 4, later: 1 }))) } });
    const filter = () => el.querySelector('[data-testid="review-later-filter"]');
    expect(filter()!.textContent).toContain('Later');
    expect(el.querySelector('[data-testid="review-later-only"]')!.textContent).toContain('1');
    (el.querySelector('[data-testid="review-later-only"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(api.getReview).toHaveBeenLastCalledWith('NeedsReview', null, null, 50, { later: true });
    expect(c.laterFilter()).toBe(true);
    c.onRowAction({ action: 'notLater', item: later });
    expect(api.setReviewLater).toHaveBeenCalledWith('n9', false);
    expect(names()).toEqual([]);
    (el.querySelector('[data-testid="review-later-now"]') as HTMLButtonElement).click();
    expect(api.getReview).toHaveBeenLastCalledWith('NeedsReview', null, null, 50, { later: false });
    c.setTab('AutoLinked');
    expect(c.laterFilter()).toBeNull(); // each visit to Needs review starts with every row
    TestBed.resetTestingModule();
    expect(create().el.querySelector('[data-testid="review-later-filter"]')).toBeNull(); // nothing set aside: no filter
  });

  // --- 1.33.0: Same author / Same folder ---

  const doujins = [
    reviewItem({ nodeId: 'd1', displayName: '[Circle A] First', sameAuthor: { key: 'circlea', label: 'Circle A', others: 1 },
      sameFolder: { key: 'f1', label: 'Doujins', others: 1 } }),
    reviewItem({ nodeId: 'd2', displayName: 'Circle A] Second', sameAuthor: { key: 'circlea', label: 'Circle A', others: 1 },
      sameFolder: { key: 'f1', label: 'Doujins', others: 1 } }),
  ];

  it('a row\'s "more by" chip lists that author\'s works; the chip above the list says so and clears it', () => {
    const { api, el, fixture } = create({ pages: { NeedsReview: doujins } });
    (el.querySelector('[data-testid="review-same-author"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(api.getReview).toHaveBeenLastCalledWith('NeedsReview', null, null, 50, { author: 'circlea' });
    const chip = el.querySelector('[data-testid="review-group-chip"]')!;
    expect(chip.textContent!.replace(/\s+/g, ' ')).toContain('By Circle A (2)');
    (el.querySelector('[data-testid="review-group-clear"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(api.getReview).toHaveBeenLastCalledWith('NeedsReview', null, null, 50, {});
    expect(el.querySelector('[data-testid="review-group-chip"]')).toBeNull();
  });

  it('the "more in" chip lists the folder\'s works, with the Later filter', () => {
    const { c, api, el, fixture } = create({ pages: { NeedsReview: doujins } });
    c.setLaterFilter(false);
    fixture.detectChanges();
    (el.querySelector('[data-testid="review-same-folder"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(api.getReview).toHaveBeenLastCalledWith('NeedsReview', null, null, 50, { later: false, folder: 'f1' });
    expect(el.querySelector('[data-testid="review-group-chip"]')!.textContent).toContain('In Doujins');
    c.setTab('AutoLinked');
    expect(c.group()).toBeNull(); // each visit starts with every item
  });

  it('g filters by the focused row\'s author and back; the Authors menu starts from the largest group', () => {
    const { c, api, el, fixture, key } = create({ pages: { NeedsReview: doujins } });
    key('g');
    expect(c.group()).toEqual({ kind: 'author', key: 'circlea', label: 'Circle A' });
    key('g');
    expect(c.group()).toBeNull();
    c.loadAuthors();
    expect(api.getReviewAuthors).toHaveBeenCalledWith(null, 'NeedsReview');
    expect(c.authors()!.map((a) => a.label)).toEqual(['Circle A', 'Artist B']);
    expect(el.querySelector('[data-testid="review-authors"]')).not.toBeNull();
    c.setGroup({ kind: 'author', key: 'artistb', label: 'Artist B' });
    fixture.detectChanges();
    expect(api.getReview).toHaveBeenLastCalledWith('NeedsReview', null, null, 50, { author: 'artistb' });
  });

  describe('1.34.0: Same author / Same folder on Unmatched', () => {
    const unmatched = [
      reviewItem({ nodeId: 'u1', displayName: '[Circle A] First', candidates: [], reasons: [], sameAuthor: { key: 'circlea', label: 'Circle A', others: 1 },
        sameFolder: { key: 'f1', label: 'Doujins', others: 1 } }),
      reviewItem({ nodeId: 'u2', displayName: 'Circle A] Second', candidates: [], reasons: [], sameAuthor: { key: 'circlea', label: 'Circle A', others: 1 },
        sameFolder: { key: 'f1', label: 'Doujins', others: 1 } }),
    ];
    const open = () => create({ tab: 'Unmatched', pages: { Unmatched: unmatched } });

    it('shows the chips and the Authors list, but not the Later filter, and loads the tab without a group', () => {
      const { el, api } = open();
      expect(api.getReview).toHaveBeenCalledWith('Unmatched', null, null, 50, {});
      expect(el.querySelector('[data-testid="review-same-author"]')!.textContent).toContain('1 more by Circle A');
      expect(el.querySelector('[data-testid="review-same-folder"]')!.textContent).toContain('1 more in Doujins');
      expect(el.querySelector('[data-testid="review-authors"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="review-later-filter"]')).toBeNull();
    });

    it('a chip filters the Unmatched list (group only, never Later) and the group chip clears it', () => {
      const { c, api, el, fixture } = open();
      (el.querySelector('[data-testid="review-same-author"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(api.getReview).toHaveBeenLastCalledWith('Unmatched', null, null, 50, { author: 'circlea' });
      expect(el.querySelector('[data-testid="review-group-chip"]')!.textContent!.replace(/\s+/g, ' ')).toContain('By Circle A');
      (el.querySelector('[data-testid="review-group-clear"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(api.getReview).toHaveBeenLastCalledWith('Unmatched', null, null, 50, {});
      c.setGroup({ kind: 'folder', key: 'f1', label: 'Doujins' });
      expect(api.getReview).toHaveBeenLastCalledWith('Unmatched', null, null, 50, { folder: 'f1' });
    });

    it('the Authors list is the Unmatched one; g filters by the focused row\'s author', () => {
      const { c, api, key } = open();
      c.loadAuthors();
      expect(api.getReviewAuthors).toHaveBeenCalledWith(null, 'Unmatched');
      key('g');
      expect(c.group()).toEqual({ kind: 'author', key: 'circlea', label: 'Circle A' });
    });

    it('bulk Re-run and Don\'t match act on the filtered group (the selection of its rows)', () => {
      const { c, api, fixture, el, snack } = open();
      c.setGroup({ kind: 'author', key: 'circlea', label: 'Circle A' });
      c.toggle('u1');
      c.toggle('u2');
      fixture.detectChanges();
      (el.querySelector('[data-testid="bulk-RerunMatching"]') as HTMLButtonElement).click();
      snack.close();
      expect(api.reviewBulk).toHaveBeenCalledWith('RerunMatching', ['u1', 'u2']);
    });

    it('a group set on one tab is gone on the next, and Needs review keeps its own Later filter', () => {
      const { c } = open();
      c.setGroup({ kind: 'author', key: 'circlea', label: 'Circle A' });
      c.setTab('NeedsReview');
      expect(c.group()).toBeNull();
    });
  });

  it('1.31.0: says how many items are being checked again under the current rules, on Needs review only', () => {
    const idle = create();
    expect(idle.el.querySelector('[data-testid="review-rechecking"]')).toBeNull();
    TestBed.resetTestingModule();

    const some = create({ apiOverrides: { getReviewSummary: vi.fn(() => of(summary({ needsReview: 3, pending: 2, recheckPending: 2 }))) } });
    expect(some.el.querySelector('[data-testid="review-rechecking"]')!.textContent!.replace(/\s+/g, ' ').trim())
      .toBe('autorenew 2 items are being checked again under the current rules.');
    TestBed.resetTestingModule();

    const one = create({ apiOverrides: { getReviewSummary: vi.fn(() => of(summary({ recheckPending: 1 }))) } });
    expect(one.el.querySelector('[data-testid="review-rechecking"]')!.textContent).toContain('1 item is being checked again under the current rules.');
    TestBed.resetTestingModule();

    // Another tab does not show it.
    const other = create({ tab: 'Unmatched', apiOverrides: { getReviewSummary: vi.fn(() => of(summary({ recheckPending: 2 }))) } });
    expect(other.el.querySelector('[data-testid="review-rechecking"]')).toBeNull();
  });

  describe('1.34.0: Collection about', () => {
    const doujins = reviewItem({
      nodeId: 'n9', displayName: 'Starlight Academy', workClass: 'Ambiguous', matchLevel: 'ReviewOnly',
      collection: { rank: 2, provider: 'mangaupdates', externalId: '200', title: 'Synthetic Saga Returns' },
    });

    it('a waiting folder that looks like a collection offers "Accept as collection" first, with the suggested series preselected', () => {
      const { c, el, api, snack } = create({ pages: { NeedsReview: [doujins, rows[1]] } });
      expect(el.querySelector('[data-testid="review-collection-hint"]')!.textContent).toContain('Looks like a collection about Synthetic Saga Returns');
      const buttons = Array.from(el.querySelectorAll('[data-node="n9"] .actions button')).map((b) => b.getAttribute('data-testid'));
      expect(buttons[0]).toBe('review-acceptCollection');
      expect(c.rankOf(doujins)).toBe(2);
      // A row without the hint keeps Accept as its first action.
      expect(el.querySelector('[data-node="n2"] .actions button')!.getAttribute('data-testid')).toBe('review-accept');

      (el.querySelector('[data-node="n9"] [data-testid="review-acceptCollection"]') as HTMLButtonElement).click();
      expect(snack.last().label).toBe('Collection about Synthetic Saga Returns: Starlight Academy');
      expect(api.acceptCollection).not.toHaveBeenCalled();
      snack.close();
      expect(api.acceptCollection).toHaveBeenCalledWith('n9', 2);
    });

    it('"f" accepts the focused row as a collection; the bulk bar offers it for the selection', () => {
      const { api, snack, key } = create({ pages: { NeedsReview: [doujins] } });
      key('f');
      snack.close();
      expect(api.acceptCollection).toHaveBeenCalledWith('n9', 2);
      TestBed.resetTestingModule();

      const bulk = create({ pages: { NeedsReview: [doujins, rows[1]] } });
      bulk.c.toggle('n9');
      bulk.fixture.detectChanges();
      (bulk.el.querySelector('[data-testid="bulk-AcceptCollection"]') as HTMLButtonElement).click();
      bulk.snack.close();
      expect(bulk.api.reviewBulk).toHaveBeenCalledWith('AcceptCollection', ['n9']);
    });

    it('the Collections tab clears a collection or changes its series in the identify dialog', async () => {
      const marked = reviewItem({
        nodeId: 'n7', displayName: 'Starlight Fan Works', candidates: [], reasons: [],
        link: { state: 'CollectionAbout', title: 'Starlight Academy', updatedAt: '2026-10-04T10:00:00Z' },
      });
      const { c, el, api, snack, identify } = create({ tab: 'Collections', pages: { Collections: [marked] } });
      expect(el.querySelector('[data-testid="review-link"]')!.textContent).toContain('Collection about Starlight Academy');
      c.onRowAction({ action: 'clearCollection', item: marked });
      snack.close();
      expect(api.clearCollection).toHaveBeenCalledWith('n7');
      c.onRowAction({ action: 'changeCollection', item: marked });
      await Promise.resolve();
      expect(identify.open).toHaveBeenCalledWith('n7', 'collection');
    });

    it('the phone bar uses the short label', () => {
      const { el, c, fixture } = create({ phone: true, pages: { NeedsReview: [doujins] } });
      c.focusIndex.set(0);
      fixture.detectChanges();
      expect(el.querySelector('[data-testid="bar-acceptCollection"]')!.textContent).toContain('Collection');
    });
  });

  describe('1.37.0: Artist folder', () => {
    const folder = reviewItem({ nodeId: 'n5', displayName: 'Beta Painter', workClass: 'Ambiguous', matchLevel: 'ReviewOnly' });
    const archive = reviewItem({ nodeId: 'n6', nodeKind: 'Archive', displayName: 'Qzv Harbor Tale.cbz', workClass: 'CollectionLeaf', matchLevel: 'Archive' });

    it('"r" asks for the artist, then marks the focused folder after the Undo window', async () => {
      const { api, snack, key, artist } = create({ pages: { NeedsReview: [folder] } });
      key('r');
      await Promise.resolve();
      await Promise.resolve();
      expect(artist.open).toHaveBeenCalledWith('Beta Painter');
      expect(snack.last().label).toBe('Artist folder: Beta Painter - Beta Painter');
      expect(api.acceptArtist).not.toHaveBeenCalled();
      snack.close();
      expect(api.acceptArtist).toHaveBeenCalledWith('n5', { name: 'Beta Painter', role: 'author' });
    });

    it('a cancelled dialog changes nothing', async () => {
      const { api, key, names, artist } = create({ pages: { NeedsReview: [folder] }, artist: null });
      key('r');
      await Promise.resolve();
      await Promise.resolve();
      expect(artist.open).toHaveBeenCalled();
      expect(names()).toEqual(['Beta Painter']);
      expect(api.acceptArtist).not.toHaveBeenCalled();
    });

    it('is offered on folder rows only, in Needs review and Unmatched, with the bulk action for the selection', () => {
      const { el, key, artist } = create({ pages: { NeedsReview: [archive, folder] } });
      expect(el.querySelector('[data-node="n5"] [data-testid="review-artistFolder"]')).not.toBeNull();
      expect(el.querySelector('[data-node="n6"] [data-testid="review-artistFolder"]')).toBeNull();
      key('r'); // the focused row is the archive: nothing happens
      expect(artist.open).not.toHaveBeenCalled();
      TestBed.resetTestingModule();

      const unmatched = create({ tab: 'Unmatched', pages: { Unmatched: [folder] } });
      expect(unmatched.el.querySelector('[data-node="n5"] [data-testid="review-artistFolder"]')).not.toBeNull();
      unmatched.c.toggle('n5');
      unmatched.fixture.detectChanges();
      (unmatched.el.querySelector('[data-testid="bulk-MarkArtistFolder"]') as HTMLButtonElement).click();
      unmatched.snack.close();
      expect(unmatched.api.reviewBulk).toHaveBeenCalledWith('MarkArtistFolder', ['n5']);
    });

    it('the Collections tab lists artist folders with their artist, counts them, and removes the mark', () => {
      const marked = reviewItem({
        nodeId: 'n8', displayName: 'Beta Painter', candidates: [], reasons: [], artist: { name: 'Beta Painter', role: 'author' },
        link: { state: 'ArtistFolder', updatedAt: '2026-10-08T10:00:00Z' },
      });
      const { c, el, api, snack } = create({
        tab: 'Collections', pages: { Collections: [marked] },
        apiOverrides: { getReviewSummary: vi.fn(() => of(summary({ collections: 1, artistFolders: 2 }))) },
      });
      expect(el.querySelector('[data-testid="review-tab-Collections"] .badge')!.textContent!.trim()).toBe('3');
      expect(el.querySelector('[data-testid="review-link"]')!.textContent).toContain('Artist folder: Beta Painter');
      const buttons = Array.from(el.querySelectorAll('[data-node="n8"] .actions button')).map((b) => b.getAttribute('data-testid'));
      expect(buttons).toEqual(['review-clearArtistFolder']);
      c.onRowAction({ action: 'clearArtistFolder', item: marked });
      snack.close();
      expect(api.clearArtistFolder).toHaveBeenCalledWith('n8');
    });
  });
});
