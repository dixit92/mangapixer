import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { By } from '@angular/platform-browser';

import { HomeComponent } from './home.component';
import { ContinueReadingEntry, LibraryViewPreferencesDto, RecentChaptersDto } from '../../core/api/api-types';
import { SeriesInfoHoverDirective } from '../../shared/hover-info/series-info-hover.directive';
import { MetadataStateService } from '../metadata/metadata-state.service';

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
    favorites?: unknown[];
    continueReading?: ContinueReadingEntry[];
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
    httpMock.expectOne((r) => r.url === '/api/v1/reading/continue').flush(opts.continueReading ?? [
      { itemId: 'i1', displayName: 'One', pageIndex: 2, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L1', libraryName: 'Alpha' },
      { itemId: 'i2', displayName: 'Two', pageIndex: 0, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L2', libraryName: 'Beta' },
    ]);
    httpMock.expectOne((r) => r.url === '/api/v1/home/recent-chapters').flush(opts.recent ?? stackedRecent);
    httpMock.expectOne('/api/v1/reading/library-preferences').flush(
      opts.prefs ?? { viewMode: 'list', density: 'comfortable', sort: 'name', direction: 'asc', cardSize: '180', libraryPageSize: 100 });
    httpMock.expectOne('/api/v1/reading/home-libraries').flush({ excludedLibraryIds: opts.excluded ?? [] });
    // The opt-in Home "Favorites" row (1.21.0) only fetches when the pref is on.
    if (opts.prefs?.showFavoritesHomeRow) {
      httpMock.expectOne((r) => r.url === '/api/v1/favorites').flush(
        { items: opts.favorites ?? [], totalCount: (opts.favorites ?? []).length, nextCursor: null, hasMore: false });
    }
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

  it('uses the server-sent (layered, versioned) cover of a Continue reading entry, else the file cover (1.29.0)', () => {
    const fixture = createComponent({
      continueReading: [
        { itemId: 'i1', displayName: 'One', pageIndex: 2, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L1', libraryName: 'Alpha',
          coverUrl: '/api/v1/nodes/i1/cover?v=k3' },
        { itemId: 'i2', displayName: 'Two', pageIndex: 0, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L2', libraryName: 'Beta' },
      ],
    });
    const cmp = fixture.componentInstance;
    expect(cmp.continueCover(cmp.continueReading()[0])).toBe('/api/v1/nodes/i1/cover?v=k3');
    expect(cmp.continueCover(cmp.continueReading()[1])).toBe('/api/v1/items/i2/cover');
    const srcs = Array.from(fixture.nativeElement.querySelectorAll('img') as NodeListOf<HTMLImageElement>).map((i) => i.getAttribute('src'));
    expect(srcs).toContain('/api/v1/nodes/i1/cover?v=k3');
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

  // --- Favorites home row (1.21.0), opt-in ---

  it('hides the Favorites row by default (opt-in off)', () => {
    const fixture = createComponent();
    const headings = Array.from(fixture.nativeElement.querySelectorAll('.strip-section h3'))
      .map((h) => (h as HTMLElement).textContent?.trim());
    expect(headings).not.toContain('Favorites');
    expect(fixture.componentInstance.showFavoritesRow()).toBe(false);
  });

  it('renders the Favorites row when the opt-in is on', () => {
    const fixture = createComponent({
      prefs: { viewMode: 'card', density: 'comfortable', sort: 'name', showFavoritesHomeRow: true },
      favorites: [
        { id: 'favA', kind: 'Archive', libraryId: 'L1', displayName: 'Fav Alpha', coverUrl: null },
        { id: 'favF', kind: 'Folder', libraryId: 'L1', displayName: 'Fav Folder', coverUrl: null },
      ],
    });
    expect(fixture.componentInstance.showFavoritesRow()).toBe(true);
    expect(fixture.componentInstance.favorites().length).toBe(2);

    const headings = Array.from(fixture.nativeElement.querySelectorAll('.strip-section h3'))
      .map((h) => (h as HTMLElement).textContent?.trim());
    expect(headings).toContain('Favorites');
  });

  it('renders a favorites stack as a stacked card opening the folder with ?favorites=1 (1.27.0)', () => {
    const fixture = createComponent({
      prefs: { viewMode: 'card', density: 'comfortable', sort: 'name', showFavoritesHomeRow: true },
      favorites: [
        { id: 'fold', kind: 'Folder', libraryId: 'L1', displayName: 'Stacked Series', coverUrl: '/api/v1/items/c1/cover', favoriteStackCount: 3, isFavorite: false },
        { id: 'fold', kind: 'Folder', libraryId: 'L1', displayName: 'Stacked Series', coverUrl: null, isFavorite: true },
        { id: 'favA', kind: 'Archive', libraryId: 'L1', displayName: 'Fav Alpha', coverUrl: null, isFavorite: true },
      ],
    });
    const el: HTMLElement = fixture.nativeElement;
    const section = Array.from(el.querySelectorAll('.strip-section'))
      .find((s) => s.querySelector('h3')?.textContent?.trim() === 'Favorites') as HTMLElement;

    // One stack + two plain cards (the starred folder and its stack do not collide).
    const stacks = section.querySelectorAll('a.stack-card');
    expect(stacks.length).toBe(1);
    expect(section.querySelectorAll('a.cont-card').length).toBe(2);

    const stack = stacks[0] as HTMLAnchorElement;
    expect(stack.getAttribute('href')).toBe('/libraries/L1/browse/fold?favorites=1');
    expect(stack.querySelector('.stack.stacked')).not.toBeNull();
    expect(stack.querySelector('.badge')?.textContent?.trim()).toBe('3');
    expect(stack.querySelector('.cont-page')?.textContent?.trim()).toBe('3 favorites');

    // Plain cards keep their plain links.
    const plain = Array.from(section.querySelectorAll('a.cont-card')).map((a) => a.getAttribute('href'));
    expect(plain).toEqual(['/libraries/L1/browse/fold', '/reader/favA']);
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
    expect(cards[0].getAttribute('href')).toBe('/libraries/L1/browse/f1?sort=recentlyUpdated&volumes=prefer');

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
      ['/libraries', 'L1', 'browse', 'f1'], { queryParams: { sort: 'recentlyUpdated', volumes: 'prefer' } });
  });

  it('folder stack href carries the transient recentlyUpdated sort (open-in-new-tab is consistent)', () => {
    const fixture = createComponent();
    const folderCard = fixture.nativeElement.querySelector('.stack-card') as HTMLAnchorElement;
    expect(folderCard.getAttribute('href')).toBe('/libraries/L1/browse/f1?sort=recentlyUpdated&volumes=prefer');
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
    expect(scope.querySelector('mat-icon')).not.toBeNull();
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

  // --- Card controls (1.28.0): the (i), the hover summary and the star on both Home rows ---

  const flaggedContinue: ContinueReadingEntry[] = [
    { itemId: 'i1', displayName: 'One', pageIndex: 2, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z',
      libraryId: 'L1', libraryName: 'Alpha', hasSeriesInfo: true, isFavorite: true },
    { itemId: 'i2', displayName: 'Two', pageIndex: 0, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z',
      libraryId: 'L2', libraryName: 'Beta', hasSeriesInfo: false, isFavorite: false },
  ];
  const flaggedRecent: RecentChaptersDto = {
    libraries: [{
      libraryId: 'L1', libraryName: 'Alpha', stacks: [
        { ...stackedRecent.libraries[0].stacks[0], hasSeriesInfo: false, isFavorite: true },
        { ...stackedRecent.libraries[0].stacks[1], hasSeriesInfo: true, isFavorite: false },
      ],
    }],
  };

  function hoverZones(fixture: ReturnType<typeof createComponent>, selector: string): string[] {
    return fixture.debugElement.queryAll(By.css(selector)).flatMap((card) =>
      card.queryAll(By.directive(SeriesInfoHoverDirective))
        .map((d) => d.injector.get(SeriesInfoHoverDirective).nodeId() ?? '-'));
  }

  it('shows the (i), the star and the hover zones on Continue-reading cards', () => {
    const fixture = createComponent({ continueReading: flaggedContinue });
    const cards = fixture.nativeElement.querySelectorAll('.cont-wrap') as NodeListOf<HTMLElement>;

    expect(cards[0].querySelector('.cover [data-testid="info-toggle"]')).not.toBeNull();
    expect(cards[1].querySelector('[data-testid="info-toggle"]')).toBeNull();
    const stars = Array.from(cards).map((c) => c.querySelector('.star-btn')!.getAttribute('aria-pressed'));
    expect(stars).toEqual(['true', 'false']);
    // Bottom-right: the top corners hold the dismiss button and the stack badges.
    expect(cards[0].querySelector('app-star-toggle')!.classList).toContain('bottom-right');
    // Cover + title of an item with information; none on the other.
    expect(hoverZones(fixture, '.cont-wrap')).toEqual(['i1', 'i1', '-', '-']);
  });

  it('shows the (i), the star and the hover zones on New-chapters stacks (the stack\'s own node)', () => {
    const fixture = createComponent({ recent: flaggedRecent });
    const cards = fixture.nativeElement.querySelectorAll('.stack-card') as NodeListOf<HTMLElement>;

    expect(cards[0].querySelector('[data-testid="info-toggle"]')).toBeNull();
    expect(cards[1].querySelector('.cover [data-testid="info-toggle"]')).not.toBeNull();
    const stars = Array.from(cards).map((c) => c.querySelector('.star-btn')!.getAttribute('aria-pressed'));
    expect(stars).toEqual(['true', 'false']);
    expect(hoverZones(fixture, '.stack-card')).toEqual(['-', '-', 'a1', 'a1']);
  });

  it('stars a folder stack through the favorites endpoint without opening the folder', () => {
    const fixture = createComponent({ recent: flaggedRecent });
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');
    const star = fixture.nativeElement.querySelectorAll('.stack-card')[1].querySelector('.star-btn') as HTMLButtonElement;

    star.click();
    const req = httpMock.expectOne('/api/v1/nodes/a1/favorite');
    expect(req.request.method).toBe('POST');
    req.flush(null);
    fixture.detectChanges();

    expect(star.getAttribute('aria-pressed')).toBe('true');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('patches a stack in place on a link change and re-fetches Continue reading (anchor rule)', () => {
    vi.useFakeTimers();
    try {
      const fixture = createComponent({ recent: flaggedRecent, continueReading: flaggedContinue });
      const state = TestBed.inject(MetadataStateService);

      state.announce('f1', true);
      // The archive card follows its series folder: an own-rule "false" for it does not hide its (i).
      state.announce('i1', false);
      fixture.detectChanges();
      expect(fixture.componentInstance.recentGroups()[0].stacks[0].hasSeriesInfo).toBe(true);
      expect(fixture.nativeElement.querySelectorAll('.stack-card')[0].querySelector('[data-testid="info-toggle"]')).not.toBeNull();
      expect(fixture.nativeElement.querySelectorAll('.cont-wrap')[0].querySelector('[data-testid="info-toggle"]')).not.toBeNull();

      // One re-fetch after the burst settles; the server's answer wins.
      vi.advanceTimersByTime(300);
      httpMock.expectOne((r) => r.url === '/api/v1/reading/continue')
        .flush([{ ...flaggedContinue[0], hasSeriesInfo: false }, flaggedContinue[1]]);
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelectorAll('.cont-wrap')[0].querySelector('[data-testid="info-toggle"]')).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });
});
