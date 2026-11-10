import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Observable, Subject, of } from 'rxjs';

import { LibraryBrowseComponent } from './library-browse.component';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { CatalogNodeDto, LibraryDto, LibraryViewPreferencesDto, PageResponse } from '../../core/api/api-types';
import { FIRST_ANSWER_SPINNER_DELAY_MS } from '../../shared/first-answer';

beforeEach(() => localStorage.clear());

const node = (id: string, displayName: string): CatalogNodeDto => ({
  id, parentId: '', libraryId: 'lib1', kind: 'Folder', displayName, availability: 'Available', coverUrl: null, childFolderCount: null,
  childArchiveCount: null, pageCount: null, readingState: null, lastReadPage: null, readerDefault: null, isRead: false, readRollup: null,
} as CatalogNodeDto);

const page = (items: CatalogNodeDto[], hasMore = false): PageResponse<CatalogNodeDto> =>
  ({ items, totalCount: items.length, nextCursor: hasMore ? 'c2' : null, hasMore });

/**
 * 1.53.0 (visual audit V2): browse's first page is never read as "This folder is empty." - not loaded shows the series page's spinner
 * after 300 ms, a failed answer the reader's error block whose "Try again" repeats the request, and the empty line only after a successful
 * empty answer. Infinite scroll (load more) keeps its own path: its failure does not replace the cards.
 */
describe('LibraryBrowseComponent first-answer states', () => {
  let answers: Subject<PageResponse<CatalogNodeDto>>[];

  function setup() {
    answers = [];
    const prefs: LibraryViewPreferencesDto = { viewMode: 'grid', density: 'comfortable', sort: 'name' };
    const libs: LibraryDto[] = [{ id: 'lib1', name: 'Test Lib', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null, icon: null }];
    const api = {
      getVolumeView: vi.fn().mockReturnValue(of({ nodeId: 'x', available: false, active: false, consolidated: false, stackCount: 0 })),
      getLibraryPreferences: vi.fn().mockReturnValue(of(prefs)),
      getRatingSharing: vi.fn().mockReturnValue(of({ share: true, serverEnabled: false })),
      getLibraries: vi.fn().mockReturnValue(of(libs)),
      // Every browse is answered by the test, one Subject per request.
      browseLibrary: vi.fn((): Observable<PageResponse<CatalogNodeDto>> => {
        const s = new Subject<PageResponse<CatalogNodeDto>>();
        answers.push(s);
        return s;
      }),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'x', trail: [] })),
      getNode: vi.fn().mockReturnValue(of(node('x', 'Folder'))),
      getJumpIndex: vi.fn().mockReturnValue(of({ libraryId: 'lib1', buckets: [] })),
    };
    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ApiService, useValue: api },
        { provide: AuthService, useValue: { isAdmin: () => false } },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : null) }) } },
      ],
    });
    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    return { fixture, api, el: fixture.nativeElement as HTMLElement };
  }

  const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`);
  const answer = (i: number, value: PageResponse<CatalogNodeDto>) => { answers[i].next(value); answers[i].complete(); };

  beforeEach(() => vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] }));
  afterEach(() => vi.useRealTimers());

  it('not loaded: nothing at first, the spinner after the delay - never "This folder is empty."', () => {
    const { fixture, el } = setup();
    expect(answers.length).toBe(1);
    expect(q(el, 'browse-loading')).toBeNull();
    expect(el.textContent).not.toContain('This folder is empty.');
    vi.advanceTimersByTime(FIRST_ANSWER_SPINNER_DELAY_MS);
    fixture.detectChanges();
    const spinner = q(el, 'browse-loading');
    expect(spinner).not.toBeNull(); // (a ?. here would pass on a page without it)
    expect(spinner!.querySelector('mat-spinner')).not.toBeNull();
    expect(el.textContent).not.toContain('This folder is empty.');

    answer(0, page([node('a', 'Alpha')]));
    fixture.detectChanges();
    expect(q(el, 'browse-loading')).toBeNull();
    expect(el.textContent).toContain('Alpha');
  });

  it('an empty folder says so only after a successful answer', () => {
    const { fixture, el } = setup();
    answer(0, page([]));
    fixture.detectChanges();
    expect(q(el, 'browse-empty')?.textContent).toContain('This folder is empty.');
    expect(q(el, 'browse-loading')).toBeNull();
    expect(q(el, 'browse-failed')).toBeNull();
  });

  it('failed: the error block; Try again repeats the same request and shows the folder', () => {
    const { fixture, el, api } = setup();
    answers[0].error({ status: 500 });
    fixture.detectChanges();
    const block = q(el, 'browse-failed') as HTMLElement;
    expect(block.querySelector('mat-icon')?.textContent).toContain('error_outline');
    expect(block.textContent).toContain('Could not load this folder.');
    expect(el.textContent).not.toContain('This folder is empty.');

    (q(el, 'browse-retry') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(api.browseLibrary).toHaveBeenCalledTimes(2);
    expect(api.browseLibrary.mock.calls[1]).toEqual(api.browseLibrary.mock.calls[0]);
    expect(q(el, 'browse-failed')).toBeNull();
    answer(1, page([node('a', 'Alpha')]));
    fixture.detectChanges();
    expect(el.textContent).toContain('Alpha');
  });

  it('infinite scroll keeps its own path: a failed next page leaves the cards and shows no error block', () => {
    const { fixture, el } = setup();
    answer(0, page([node('a', 'Alpha')], true));
    fixture.detectChanges();
    fixture.componentInstance.loadMore();
    answers[1].error({ status: 500 });
    fixture.detectChanges();
    expect(el.textContent).toContain('Alpha');
    expect(q(el, 'browse-failed')).toBeNull();
    expect(q(el, 'browse-loading')).toBeNull();
  });
});
