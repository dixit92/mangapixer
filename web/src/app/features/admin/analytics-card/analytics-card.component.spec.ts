import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { AnalyticsCardComponent } from './analytics-card.component';
import { AnalyticsOverviewDto, AnalyticsUserRowDto } from '../../../core/api/api-types';

/**
 * Admin Analytics card (1.22.0 lane E). The API is mocked at the HTTP layer;
 * these assert the overview/users GET shapes, refresh behavior, loading/error
 * states, and the sortable-table interaction.
 */
describe('AnalyticsCardComponent', () => {
  const OVERVIEW_URL = '/api/v1/admin/analytics/overview';
  const USERS_URL = '/api/v1/admin/analytics/users';
  let httpMock: HttpTestingController;

  const overview = (overrides: Partial<AnalyticsOverviewDto> = {}): AnalyticsOverviewDto => ({
    generatedAt: '2026-09-23T00:00:00Z',
    libraryCount: 2,
    totalNodeCount: 100,
    archiveNodeCount: 80,
    folderNodeCount: 20,
    tombstonedNodeCount: 1,
    analyzedItemCount: 70,
    pendingItemCount: 9,
    failedItemCount: 1,
    userCount: 2,
    activeUserCount: 2,
    adminCount: 1,
    pendingActivationCount: 0,
    readingProgressCount: 10,
    completedItemCount: 4,
    inProgressItemCount: 6,
    bookmarkCount: 3,
    favoriteCount: 5,
    activeSessionCount: 1,
    ...overrides,
  });

  const userRow = (overrides: Partial<AnalyticsUserRowDto> = {}): AnalyticsUserRowDto => ({
    id: 'u1',
    username: 'admin',
    isAdmin: true,
    isActive: true,
    isPendingActivation: false,
    lastLoginAt: '2026-09-22T00:00:00Z',
    chaptersCompleted: 1,
    chaptersInProgress: 0,
    bookmarkCount: 0,
    favoriteCount: 0,
    lastReadingActivityAt: '2026-09-22T00:00:00Z',
    ...overrides,
  });

  function create() {
    TestBed.configureTestingModule({
      imports: [AnalyticsCardComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(AnalyticsCardComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    return fixture;
  }

  function createLoaded(overviewDto = overview(), users: AnalyticsUserRowDto[] = [userRow()]) {
    const fixture = create();
    httpMock.expectOne(OVERVIEW_URL).flush(overviewDto);
    httpMock.expectOne(USERS_URL).flush(users);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => httpMock?.verify());

  it('explains, directly under the tiles, that totals include Private libraries but the table does not', () => {
    const el = createLoaded().nativeElement as HTMLElement;
    const caption = el.querySelector('.tiles + .scope-note') as HTMLElement;
    expect(caption.textContent).toContain('Totals include every user');
    expect(caption.textContent).toContain('Private libraries');
    expect(caption.textContent).toContain('the table below');
  });

  it('loads overview tiles and the user table on init', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;

    expect(c.loading()).toBe(false);
    expect(c.tiles().find((t) => t.label === 'Libraries')?.value).toBe(2);
    expect(c.users().length).toBe(1);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('admin');
  });

  it('refresh re-issues both GETs', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;

    c.load();
    expect(c.loading()).toBe(true);

    httpMock.expectOne(OVERVIEW_URL).flush(overview({ libraryCount: 3 }));
    httpMock.expectOne(USERS_URL).flush([userRow()]);
    fixture.detectChanges();

    expect(c.loading()).toBe(false);
    expect(c.tiles().find((t) => t.label === 'Libraries')?.value).toBe(3);
  });

  it('surfaces the server message when loading fails', () => {
    const fixture = create();
    // forkJoin cancels the sibling request as soon as one errors, so the
    // users GET is never flushed here — only matched, to satisfy verify().
    httpMock.expectOne(USERS_URL);
    httpMock.expectOne(OVERVIEW_URL).flush(
      { error: 'forbidden', message: 'Admins only', detail: null, correlationId: null },
      { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).toBe('Admins only');
  });

  it('sortBy toggles direction on repeated clicks and switches column otherwise', () => {
    const fixture = createLoaded(overview(), [
      userRow({ id: 'u1', username: 'bravo', chaptersCompleted: 1 }),
      userRow({ id: 'u2', username: 'alpha', chaptersCompleted: 5 }),
    ]);
    const c = fixture.componentInstance;

    // Default sort is by username ascending.
    expect(c.sortedUsers().map((u) => u.username)).toEqual(['alpha', 'bravo']);

    c.sortBy('username');
    expect(c.sortDirection()).toBe('desc');
    expect(c.sortedUsers().map((u) => u.username)).toEqual(['bravo', 'alpha']);

    c.sortBy('chaptersCompleted');
    expect(c.sortColumn()).toBe('chaptersCompleted');
    expect(c.sortDirection()).toBe('asc');
    expect(c.sortedUsers().map((u) => u.username)).toEqual(['bravo', 'alpha']);
  });

  it('ariaSort reports the active column and direction, "none" otherwise', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;

    expect(c.ariaSort('username')).toBe('ascending');
    expect(c.ariaSort('bookmarkCount')).toBe('none');

    c.sortBy('username');
    expect(c.ariaSort('username')).toBe('descending');
  });
});
