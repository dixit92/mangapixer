import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { HomeComponent } from './home.component';
import { ContinueReadingEntry } from '../../core/api/api-types';

beforeEach(() => localStorage.clear());

/**
 * Archive context line on Home's Continue reading cards (1.50.0, lane W): the folder name as a muted line under the title, only when the
 * server sent one. Its own spec file (home.component.spec.ts keeps its other owners' tests).
 */
describe('HomeComponent Continue reading context line', () => {
  let httpMock: HttpTestingController;

  function create(continueReading: ContinueReadingEntry[]) {
    TestBed.configureTestingModule({
      imports: [HomeComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(HomeComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    httpMock.expectOne('/api/v1/libraries').flush([]);
    httpMock.expectOne((r) => r.url === '/api/v1/reading/continue').flush(continueReading);
    httpMock.expectOne((r) => r.url === '/api/v1/home/recent-chapters').flush({ libraries: [] });
    httpMock.expectOne('/api/v1/reading/library-preferences').flush(
      { viewMode: 'list', density: 'comfortable', sort: 'name', direction: 'asc', cardSize: '180', libraryPageSize: 100 });
    httpMock.expectOne('/api/v1/reading/home-libraries').flush({ excludedLibraryIds: [] });
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => {
    httpMock.verify();
    vi.restoreAllMocks();
  });

  const entry = (itemId: string, displayName: string, contextName?: string | null): ContinueReadingEntry => ({
    itemId, displayName, pageIndex: 2, contentVersion: 1, updatedAt: '2026-10-28T00:00:00Z', libraryId: 'L1', libraryName: 'Alpha',
    ...(contextName === undefined ? {} : { contextName }),
  });

  it('shows the folder name under a card whose archive has one, and nothing on the others', () => {
    const fixture = create([entry('i1', 'Vol. 012', 'Berserk'), entry('i2', 'Loose Tale', null), entry('i3', 'Older server')]);
    const cards = Array.from(fixture.nativeElement.querySelectorAll('.cont-wrap')) as HTMLElement[];

    expect(cards).toHaveLength(3);
    const line = cards[0].querySelector('[data-testid="context-line"]') as HTMLElement;
    expect(line.textContent?.trim()).toBe('Berserk');
    expect(line.getAttribute('title')).toBe('Berserk');
    // Title, then the context line, then the page line.
    const order = Array.from(cards[0].querySelectorAll('.cont-title, app-context-line, .cont-page')).map((e) => e.className || e.tagName.toLowerCase());
    expect(order).toEqual(['cont-title', 'app-context-line', 'cont-page']);
    expect(cards[1].querySelector('[data-testid="context-line"]')).toBeNull();
    expect(cards[2].querySelector('[data-testid="context-line"]')).toBeNull();
  });
});
