import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { HomeComponent } from './home.component';
import { LibraryViewPreferencesDto, RecentChaptersDto } from '../../core/api/api-types';

/**
 * Home page tests. The library sidebar was promoted to the app shell, so home is
 * the consolidated continue-reading row, the New chapters row - STACKED cards per
 * top-level unit, with the shared card-size slider and the per-user library picker -
 * plus the library grid (each card carrying a reading-direction indicator).
 */
describe('HomeComponent', () => {
  let httpMock: HttpTestingController;

  const stackedRecent: RecentChaptersDto = {
    libraries: [
      {
        libraryId: 'L1', libraryName: 'Alpha', stacks: [
          // A top-level folder with three new chapters: ONE card, newest chapter named.
          // Unread (the quiet default): no read-state marker should render.
          { id: 'f1', displayName: 'Series A', isFolder: true, coverUrl: '/api/v1/items/c3/cover',
            latestItemId: 'c3', latestItemName: 'Ch3.cbz', latestAddedAt: '2026-09-12T00:00:00Z', newCount: 3,
            readState: 'unread' },
          // A loose top-level archive: its own single-chapter stack, fully read.
          { id: 'a1', displayName: 'Oneshot.cbz', isFolder: false, coverUrl: null,
            latestItemId: 'a1', latestItemName: 'Oneshot.cbz', latestAddedAt: '2026-09-11T00:00:00Z', newCount: 1,
            readState: 'read' },
        ],
      },
      { libraryId: 'L2', libraryName: 'Beta', stacks: [] },
    ],
  };

  interface Options {
    recent?: RecentChaptersDto;
    prefs?: LibraryViewPreferencesDto;
    excluded?: string[];
    libraries?: unknown[];
  }

  function createComponent(opts: Options = {}) {
    TestBed.configureTestingModule({
      imports: [HomeComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideNoopAnimations(),
      ],
    });
    const fixture = TestBed.createComponent(HomeComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges(); // triggers ngOnInit

    httpMock.expectOne('/api/v1/libraries').flush(opts.libraries ?? [
      { id: 'L1', name: 'Alpha', isScanning: false, itemCount: 3, lastScanCompleted: null, defaultReaderMode: 'PagedRtl' },
      { id: 'L2', name: 'Beta', isScanning: false, itemCount: 5, lastScanCompleted: null, defaultReaderMode: null },
    ]);
    httpMock.expectOne((r) => r.url === '/api/v1/reading/continue').flush([
      { itemId: 'i1', displayName: 'One', pageIndex: 2, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L1', libraryName: 'Alpha' },
      { itemId: 'i2', displayName: 'Two', pageIndex: 0, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L2', libraryName: 'Beta' },
    ]);
    httpMock.expectOne((r) => r.url === '/api/v1/home/recent-chapters').flush(opts.recent ?? stackedRecent);
    httpMock.expectOne('/api/v1/reading/library-preferences').flush(
      opts.prefs ?? { viewMode: 'list', density: 'comfortable', sort: 'name', direction: 'asc', cardSize: '180', libraryPageSize: 100 });
    httpMock.expectOne('/api/v1/reading/home-libraries').flush({ excludedLibraryIds: opts.excluded ?? [] });
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => {
    httpMock.verify();
    vi.restoreAllMocks();
  });

  it('shows the consolidated continue-reading list and the library grid', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    expect(cmp.libraries().length).toBe(2);
    expect(cmp.continueReading().length).toBe(2);

    // One card per library, labelled by name. (Card navigation to the browse
    // root is a RouterLink on <mat-card>; the anchor-based link targets are
    // asserted in the sidebar spec, where hrefs are actually rendered.)
    const cards = fixture.nativeElement.querySelectorAll('.library-card') as NodeListOf<HTMLElement>;
    expect(cards).toHaveLength(2);
    expect(cards[0].textContent).toContain('Alpha');
    expect(cards[1].textContent).toContain('Beta');
  });

  it('shows a reading-direction indicator only for libraries with an explicit mode', () => {
    const fixture = createComponent();
    const cards = fixture.nativeElement.querySelectorAll('.library-card') as NodeListOf<HTMLElement>;

    // L1 = PagedRtl -> badge with the RTL label; L2 = null -> no badge.
    const l1Dir = cards[0].querySelector('.card-dir');
    expect(l1Dir).not.toBeNull();
    expect(l1Dir!.getAttribute('aria-label')).toBe('Reading direction: Right to left');
    expect(cards[1].querySelector('.card-dir')).toBeNull();
  });

  it('removes an item from the continue-reading list on dismiss', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;
    const evt = new Event('click');

    cmp.dismiss(evt, {
      itemId: 'i1', displayName: 'One', pageIndex: 2, contentVersion: 1,
      updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L1', libraryName: 'Alpha',
    });
    httpMock.expectOne('/api/v1/reading/continue/i1').flush(null);

    expect(cmp.continueReading().map((e) => e.itemId)).toEqual(['i2']);
  });

  // --- B1: stacked "New chapters" cards ---

  it('renders one stacked card per top-level unit, grouped by library', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    // Only libraries with at least one stack are surfaced (Beta has none).
    expect(cmp.visibleGroups().map((g) => g.libraryId)).toEqual(['L1']);

    const section = fixture.nativeElement.querySelector('.recent-section') as HTMLElement;
    expect(section).not.toBeNull();
    expect(section.querySelector('h3')!.textContent).toContain('New chapters');

    const libBlocks = section.querySelectorAll('.recent-lib') as NodeListOf<HTMLElement>;
    expect(libBlocks).toHaveLength(1);
    expect(libBlocks[0].querySelector('h4')!.textContent).toContain('Alpha');

    // Two STACKS (not five archives): the folder stack and the loose archive.
    const cards = libBlocks[0].querySelectorAll('.stack-card') as NodeListOf<HTMLElement>;
    expect(cards).toHaveLength(2);

    // Folder stack: unit name, newest chapter name, a +3 badge, stacked-paper edges,
    // cover from the DTO, and an href to the folder's browse view carrying the
    // transient recentlyUpdated sort (so open-in-new-tab lists newest-first too).
    expect(cards[0].querySelector('.cont-title')!.textContent).toContain('Series A');
    expect(cards[0].querySelector('.latest')!.textContent).toContain('Ch3.cbz');
    expect(cards[0].querySelector('.badge.new')!.textContent!.trim()).toBe('+3');
    expect(cards[0].querySelector('.stack.stacked')).not.toBeNull();
    expect(cards[0].querySelector('img')!.getAttribute('src')).toBe('/api/v1/items/c3/cover');
    expect(cards[0].getAttribute('href')).toBe('/libraries/L1/browse/f1?sort=recentlyUpdated');

    // Loose archive: no badge, no stacked edges, folder-less fallback icon, and a
    // RouterLink straight into the reader on the archive itself.
    expect(cards[1].querySelector('.cont-title')!.textContent).toContain('Oneshot.cbz');
    expect(cards[1].querySelector('.badge.new')).toBeNull();
    expect(cards[1].querySelector('.stack.stacked')).toBeNull();
    expect(cards[1].querySelector('img')).toBeNull();
    expect(cards[1].querySelector('.cover-fallback')!.textContent).toContain('menu_book');
    expect(cards[1].getAttribute('href')).toBe('/reader/a1');
  });

  // --- Read-state marker on New-chapters cards (1.20.0) ---

  it('renders no read-state marker for an unread stack (the quiet default)', () => {
    const fixture = createComponent();
    const cards = fixture.nativeElement.querySelectorAll('.stack-card') as NodeListOf<HTMLElement>;
    // f1 (Series A) carries readState 'unread' in the fixture.
    expect(cards[0].querySelector('.badge.read-state')).toBeNull();
  });

  it('renders a green "Read" marker for a fully-read stack, with an aria-label', () => {
    const fixture = createComponent();
    const cards = fixture.nativeElement.querySelectorAll('.stack-card') as NodeListOf<HTMLElement>;
    // a1 (the loose archive) carries readState 'read' in the fixture.
    const marker = cards[1].querySelector('.badge.read-state') as HTMLElement;
    expect(marker).not.toBeNull();
    expect(marker.classList.contains('read')).toBe(true);
    expect(marker.classList.contains('reading')).toBe(false);
    expect(marker.textContent).toContain('Read');
    expect(marker.getAttribute('aria-label')).toBe('All items read');
  });

  it('renders a purple "Reading" marker for a partially-read stack', () => {
    const fixture = createComponent({
      recent: {
        libraries: [
          {
            libraryId: 'L1', libraryName: 'Alpha', stacks: [
              { id: 'f2', displayName: 'Series B', isFolder: true, coverUrl: null,
                latestItemId: 'c9', latestItemName: 'Ch9.cbz', latestAddedAt: '2026-09-12T00:00:00Z',
                newCount: 1, readState: 'reading' },
            ],
          },
        ],
      },
    });
    const card = fixture.nativeElement.querySelector('.stack-card') as HTMLElement;
    const marker = card.querySelector('.badge.read-state') as HTMLElement;
    expect(marker).not.toBeNull();
    expect(marker.classList.contains('reading')).toBe(true);
    expect(marker.classList.contains('read')).toBe(false);
    expect(marker.textContent!.trim()).toBe('Reading');
    expect(marker.getAttribute('aria-label')).toBe('Partially read');
  });

  it('readStateView maps each wire value to the matching marker (or null for unread)', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;
    expect(cmp.readStateView('read')).toEqual({ kind: 'read', text: '✓ Read', tooltip: 'All items read' });
    expect(cmp.readStateView('reading')).toEqual({ kind: 'reading', text: 'Reading', tooltip: 'Partially read' });
    expect(cmp.readStateView('unread')).toBeNull();
  });

  it('folder tap routes with a transient recentlyUpdated sort and does NOT persist a preference', () => {
    const fixture = createComponent();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    const folderCard = fixture.nativeElement.querySelector('.stack-card') as HTMLAnchorElement;
    const click = new MouseEvent('click', { button: 0, cancelable: true, bubbles: true });
    folderCard.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(true);

    // Transient: the folder opens sorted recentlyUpdated via a query param, WITHOUT
    // writing the user's persisted library sort.
    httpMock.expectNone({ method: 'PUT', url: '/api/v1/reading/library-preferences' });
    expect(navigate).toHaveBeenCalledWith(
      ['/libraries', 'L1', 'browse', 'f1'], { queryParams: { sort: 'recentlyUpdated' } });
  });

  it('folder stack href carries the transient recentlyUpdated sort (open-in-new-tab is consistent)', () => {
    const fixture = createComponent();
    const folderCard = fixture.nativeElement.querySelector('.stack-card') as HTMLAnchorElement;
    expect(folderCard.getAttribute('href')).toBe('/libraries/L1/browse/f1?sort=recentlyUpdated');
  });

  it('leaves modified clicks on a folder stack to the browser (open in new tab)', () => {
    const fixture = createComponent();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    const click = new MouseEvent('click', { button: 0, ctrlKey: true, cancelable: true });
    fixture.componentInstance.openFolder(click, stackedRecent.libraries[0], stackedRecent.libraries[0].stacks[0]);

    expect(click.defaultPrevented).toBe(false);
    httpMock.expectNone({ method: 'PUT', url: '/api/v1/reading/library-preferences' });
    expect(navigate).not.toHaveBeenCalled();
  });

  it('keeps the section (with an empty state) when no visible library has new chapters', () => {
    const fixture = createComponent({
      recent: { libraries: [{ libraryId: 'L1', libraryName: 'Alpha', stacks: [] }] },
    });
    const cmp = fixture.componentInstance;

    expect(cmp.visibleGroups().length).toBe(0);
    const section = fixture.nativeElement.querySelector('.recent-section') as HTMLElement;
    // The heading stays (so the section is discoverable) with an empty-state body; the
    // picker that chooses libraries now lives under Settings > New Chapters.
    expect(section).not.toBeNull();
    expect(section.querySelectorAll('.stack-card')).toHaveLength(0);
    expect(section.querySelector('.empty')!.textContent).toContain('Nothing new yet');
  });

  it('hides the New chapters section entirely when the user can see no libraries', () => {
    const fixture = createComponent({ libraries: [], recent: { libraries: [] } });
    expect(fixture.nativeElement.querySelector('.recent-section')).toBeNull();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('New chapters');
  });

  // --- B2: card-size slider (shared library preference) ---

  it('labels the toolbar slider\'s scope with a "Card size" caption (1.20.0)', () => {
    const fixture = createComponent();
    const scope = fixture.nativeElement.querySelector('.toolbar-scope') as HTMLElement;
    expect(scope).not.toBeNull();
    expect(scope.querySelector('.scope-label')!.textContent).toContain('Card size');
    expect(scope.querySelector('.scope-icon')).not.toBeNull();
  });

  it('groups the New-chapters Filter button with its heading via a section divider', () => {
    const fixture = createComponent();
    const head = fixture.nativeElement.querySelector('.section-head') as HTMLElement;
    expect(head.querySelector('.head-divider')).not.toBeNull();
  });

  it('reads the stored library card size and feeds it to the home cards as --card-size', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;
    expect(cmp.cardSize()).toBe(180);
    const home = fixture.nativeElement.querySelector('.home') as HTMLElement;
    expect(home.style.getPropertyValue('--card-size')).toBe('180px');
    const slider = fixture.nativeElement.querySelector('.size-slider') as HTMLInputElement;
    expect(slider.value).toBe('180');
  });

  it('derives the card size from a legacy viewMode+density blob exactly like the browse view', () => {
    const fixture = createComponent({ prefs: { viewMode: 'poster', density: 'compact', sort: 'name' } });
    expect(fixture.componentInstance.cardSize()).toBe(168);
  });

  it('slider input previews live without persisting; change persists into the shared preference', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;
    const slider = fixture.nativeElement.querySelector('.size-slider') as HTMLInputElement;

    slider.value = '220';
    slider.dispatchEvent(new Event('input'));
    expect(cmp.cardSize()).toBe(220);
    httpMock.expectNone({ method: 'PUT', url: '/api/v1/reading/library-preferences' });

    slider.value = '200';
    slider.dispatchEvent(new Event('change'));
    expect(cmp.cardSize()).toBe(200);
    const put = httpMock.expectOne({ method: 'PUT', url: '/api/v1/reading/library-preferences' });
    // Only cardSize changes; the browse view's list mode, sort and page size are echoed back.
    expect(put.request.body).toEqual({
      viewMode: 'list', density: 'comfortable', sort: 'name', direction: 'asc',
      cardSize: '200', libraryPageSize: 100,
    });
    put.flush(null);
    fixture.detectChanges();
    expect((fixture.nativeElement.querySelector('.home') as HTMLElement).style.getPropertyValue('--card-size')).toBe('200px');
  });

  it('setCardSize clamps out-of-range values to the shared slider range', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;
    cmp.setCardSize(9999);
    expect(cmp.cardSize()).toBe(cmp.cardSizeMax);
    httpMock.expectOne({ method: 'PUT', url: '/api/v1/reading/library-preferences' }).flush(null);
    cmp.setCardSize(1);
    expect(cmp.cardSize()).toBe(cmp.cardSizeMin);
    httpMock.expectOne({ method: 'PUT', url: '/api/v1/reading/library-preferences' }).flush(null);
  });

  // --- Library visibility: home READS the per-user excluded set to filter which
  //     libraries contribute cards. The picker that EDITS the set lives under
  //     Settings > New Chapters. ---

  // --- Read-state filter (1.17.0): New chapters row only ---

  it('sends no readState param by default and reloads with it when the filter changes', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    // Default 'all': no readState query param, "Filter" label, inactive styling.
    const button = fixture.nativeElement.querySelector('.filter-toggle') as HTMLElement;
    expect(button.textContent).toContain('Filter');
    expect(button.classList.contains('filter-active')).toBe(false);

    cmp.setRecentReadStateFilter('read');
    const req = httpMock.expectOne((r) => r.url === '/api/v1/home/recent-chapters');
    expect(req.request.params.get('readState')).toBe('read');
    req.flush({ libraries: [] });
    fixture.detectChanges();

    expect(cmp.recentReadStateFilter()).toBe('read');
    expect(button.textContent).toContain('Read');
    expect(button.classList.contains('filter-active')).toBe(true);
  });

  it('does not reload when the filter is set to its current value', () => {
    const fixture = createComponent();
    fixture.componentInstance.setRecentReadStateFilter('all');
    httpMock.expectNone((r) => r.url === '/api/v1/home/recent-chapters');
  });

  it('picks the read-state option from the toolbar menu', () => {
    // mat-menu renders its panel in a CDK overlay outside the component's host
    // element (same pattern as the library browse view's Filter menu), so the
    // trigger is opened via fixture.nativeElement but the options are queried
    // through `document`.
    const fixture = createComponent();
    (fixture.nativeElement.querySelector('.filter-toggle') as HTMLElement).click();
    fixture.detectChanges();

    const panel = document.querySelector('.view-options-menu') as HTMLElement;
    expect(panel, 'the view-options-menu overlay panel').not.toBeNull();
    const options = panel.querySelectorAll('[role="menuitemradio"]') as NodeListOf<HTMLButtonElement>;
    // All / Reading / Read / Unread, in that order.
    expect(options).toHaveLength(4);

    options[2].click(); // "Read"
    const req = httpMock.expectOne((r) => r.url === '/api/v1/home/recent-chapters');
    expect(req.request.params.get('readState')).toBe('read');
    req.flush({ libraries: [] });

    expect(fixture.componentInstance.recentReadStateFilter()).toBe('read');
  });

  it('reads the excluded set from the server and drops those libraries from the row', () => {
    const fixture = createComponent({ excluded: ['L1'] });
    const cmp = fixture.componentInstance;

    // L1 (Alpha) is excluded, so its stacks are filtered out of the row.
    expect(cmp.excludedLibraryIds().has('L1')).toBe(true);
    expect(cmp.visibleGroups()).toEqual([]);
    const section = fixture.nativeElement.querySelector('.recent-section') as HTMLElement;
    expect(section.querySelector('.empty')!.textContent).toContain('libraries shown here');
    // No picker UI on the home page any more - it moved to Settings > New Chapters.
    expect(section.querySelector('.lib-picker-toggle')).toBeNull();
  });
});
