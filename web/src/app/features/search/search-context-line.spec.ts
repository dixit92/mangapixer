import { vi } from 'vitest';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { SearchComponent } from './search.component';
import { ApiService } from '../../core/api/api.service';
import { CatalogNodeDto, SearchResultsDto } from '../../core/api/api-types';

/**
 * Archive context line on the Search page's result cards (1.50.0, lane W): an archive whose server answer carries `contextName` shows the
 * folder it belongs to as a muted line under its name; a folder or a root archive shows none. Its own spec file, so lane U's
 * `search.component.spec.ts` stays untouched.
 */
function node(partial: Partial<CatalogNodeDto>): CatalogNodeDto {
  return {
    id: 'id', parentId: '', libraryId: 'L1', kind: 'Archive', displayName: 'x', availability: 'Available', coverUrl: null,
    childFolderCount: null, childArchiveCount: null, pageCount: null, readingState: null, lastReadPage: null, readerDefault: null,
    isRead: false, readRollup: null, ...partial,
  } as CatalogNodeDto;
}

describe('SearchComponent archive context line', () => {
  let fixture: ComponentFixture<SearchComponent>;

  function search(items: CatalogNodeDto[]): HTMLElement[] {
    const response: SearchResultsDto = { query: 'q', items, totalCount: items.length, nextCursor: null, hasMore: false };
    TestBed.configureTestingModule({
      imports: [SearchComponent],
      providers: [
        provideRouter([]),
        provideNoopAnimations(),
        {
          provide: ApiService,
          useValue: {
            search: vi.fn().mockReturnValue(of(response)),
            getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })),
          },
        },
      ],
    });
    fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();
    fixture.componentInstance.query = 'berserk';
    fixture.componentInstance.onSearch();
    vi.advanceTimersByTime(300);
    fixture.detectChanges();
    return Array.from(fixture.nativeElement.querySelectorAll('.result-card')) as HTMLElement[];
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  it('shows the folder name under a title-less archive, with the whole name as its tooltip', () => {
    const cards = search([
      node({ id: 'a1', displayName: 'Vol. 012', contextName: 'Berserk' }),
      node({ id: 'a2', displayName: 'Ch. 0106.00-0110.00 Vol. 006', contextName: 'Another Series' }),
    ]);

    expect(cards).toHaveLength(2);
    const line = cards[0].querySelector('[data-testid="context-line"]') as HTMLElement;
    expect(line.textContent?.trim()).toBe('Berserk');
    expect(line.getAttribute('title')).toBe('Berserk');
    expect(cards[1].querySelector('[data-testid="context-line"]')?.textContent?.trim()).toBe('Another Series');
    // Under the title, inside the card's text block.
    expect(cards[0].querySelector('.result-title')?.textContent?.trim()).toBe('Vol. 012');
  });

  it('shows no line for a folder or an archive without a context name', () => {
    const cards = search([
      node({ id: 'f1', kind: 'Folder', displayName: 'Berserk' }),
      node({ id: 'a3', displayName: 'Loose Oneshot', contextName: null }),
    ]);

    expect(cards).toHaveLength(2);
    expect(fixture.nativeElement.querySelectorAll('[data-testid="context-line"]')).toHaveLength(0);
  });
});
