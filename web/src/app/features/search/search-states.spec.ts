import { vi } from 'vitest';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Observable, Subject, of } from 'rxjs';

import { SearchComponent } from './search.component';
import { ApiService } from '../../core/api/api.service';
import { CatalogNodeDto, SearchResultsDto } from '../../core/api/api-types';
import { FIRST_ANSWER_SPINNER_DELAY_MS } from '../../shared/first-answer';

const node = (id: string, displayName: string): CatalogNodeDto => ({
  id, parentId: '', libraryId: 'L1', kind: 'Folder', displayName, availability: 'Available', coverUrl: null, childFolderCount: null,
  childArchiveCount: null, pageCount: null, readingState: null, lastReadPage: null, readerDefault: null, isRead: false, readRollup: null,
} as CatalogNodeDto);

const results = (items: CatalogNodeDto[]): SearchResultsDto => ({ query: 'q', items, totalCount: items.length, nextCursor: null, hasMore: false });

/**
 * 1.53.0 (visual audit V2): a search's first page is never read as "No results found." - not answered yet shows the series page's
 * spinner after 300 ms (in place of the previous results), a failure the reader's error block whose "Try again" repeats the same
 * search, and "No results found." only after a successful empty answer.
 */
describe('SearchComponent first-answer states', () => {
  let fixture: ComponentFixture<SearchComponent>;
  let answers: Subject<SearchResultsDto>[];
  let search: ReturnType<typeof vi.fn>;

  function setup(): HTMLElement {
    answers = [];
    search = vi.fn((): Observable<SearchResultsDto> => {
      const s = new Subject<SearchResultsDto>();
      answers.push(s);
      return s;
    });
    TestBed.configureTestingModule({
      imports: [SearchComponent],
      providers: [
        provideRouter([]),
        provideNoopAnimations(),
        { provide: ApiService, useValue: { search, getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })) } },
      ],
    });
    fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  /** Type a query and fire the 300 ms debounce. */
  function type(query: string): void {
    fixture.componentInstance.query = query;
    fixture.componentInstance.onSearch();
    vi.advanceTimersByTime(300);
    fixture.detectChanges();
  }

  const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`);
  const answer = (i: number, value: SearchResultsDto) => { answers[i].next(value); answers[i].complete(); fixture.detectChanges(); };

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  it('not answered yet: the spinner after the delay (never "No results found."), then the results', () => {
    const el = setup();
    type('saga');
    expect(search).toHaveBeenCalledTimes(1);
    expect(q(el, 'search-loading')).toBeNull();
    vi.advanceTimersByTime(FIRST_ANSWER_SPINNER_DELAY_MS);
    fixture.detectChanges();
    const spinner = q(el, 'search-loading');
    expect(spinner).not.toBeNull(); // (a ?. here would pass on a page without it)
    expect(spinner!.querySelector('mat-spinner')).not.toBeNull();
    expect(el.textContent).not.toContain('No results found.');
    answer(0, results([node('a', 'Synthetic Saga')]));
    expect(q(el, 'search-loading')).toBeNull();
    expect(el.textContent).toContain('Synthetic Saga');
  });

  it('a slow new search replaces the previous results with the spinner', () => {
    const el = setup();
    type('saga');
    answer(0, results([node('a', 'Synthetic Saga')]));
    type('other');
    vi.advanceTimersByTime(FIRST_ANSWER_SPINNER_DELAY_MS);
    fixture.detectChanges();
    expect(q(el, 'search-loading')).not.toBeNull();
    expect(el.textContent).not.toContain('Synthetic Saga');
  });

  it('"No results found." only after a successful empty answer', () => {
    const el = setup();
    type('nothing');
    answer(0, results([]));
    expect(el.querySelector('.no-results')?.textContent?.trim()).toBe('No results found.');
    expect(q(el, 'search-failed')).toBeNull();
  });

  it('failed: the error block, not "No results found."; Try again repeats the same search', () => {
    const el = setup();
    type('saga');
    answers[0].error({ status: 500 });
    fixture.detectChanges();
    const block = q(el, 'search-failed') as HTMLElement;
    expect(block.querySelector('mat-icon')?.textContent).toContain('error_outline');
    expect(block.textContent).toContain('The search did not answer.');
    expect(el.querySelector('.no-results')).toBeNull();

    (q(el, 'search-retry') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(search).toHaveBeenCalledTimes(2);
    expect(search.mock.calls[1]).toEqual(search.mock.calls[0]);
    expect(q(el, 'search-failed')).toBeNull();
    answer(1, results([node('a', 'Synthetic Saga')]));
    expect(el.textContent).toContain('Synthetic Saga');
  });
});
