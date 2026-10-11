import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { ReadingModeSuggestionDto, ReadingModeSuggestionsDto } from '../../../core/api/api-types';
import { ReadingModeSuggestionsComponent } from './reading-mode-suggestions.component';
import { appliedSentence, currentText, evidenceLines } from './reading-mode-labels';

const URL = '/api/v1/admin/reading-mode-suggestions';

function row(overrides: Partial<ReadingModeSuggestionDto> = {}): ReadingModeSuggestionDto {
  return {
    nodeId: 'f1',
    libraryId: 'lib1',
    libraryName: 'Ongoing',
    folderName: 'Long Strip Story',
    parentName: 'Manhwa',
    suggestedMode: 'VerticalWebtoon',
    currentMode: 'PagedRtl',
    currentSource: 'Library',
    confidence: 'High',
    signal: 'TallPages',
    measuredPages: 40,
    tallPages: 38,
    comicInfoRightToLeft: 0,
    comicInfoStated: 0,
    linkedOrigin: null,
    linkedWebtoon: null,
    subfolders: 0,
    ...overrides,
  };
}

function list(items: ReadingModeSuggestionDto[], setByAdmin: ReadingModeSuggestionDto[] = []): ReadingModeSuggestionsDto {
  return { items, setByAdmin, foldersChecked: 12, total: items.length, limit: 500 };
}

/**
 * Reading mode suggestions page (1.41.0): lists the suggestions with their evidence and "Now -> Suggested", applies one or the
 * selected rows (the request carries node ids and modes only) and reloads, shows the admin-set folders on demand, and an empty state.
 * Mocked HTTP backend.
 */
describe('ReadingModeSuggestionsComponent', () => {
  function create() {
    TestBed.configureTestingModule({
      imports: [ReadingModeSuggestionsComponent],
      providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(ReadingModeSuggestionsComponent);
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
  }

  const q = (el: HTMLElement, testId: string) => el.querySelector(`[data-testid="${testId}"]`) as HTMLElement;

  it('lists suggestions with their evidence and applies one row', () => {
    const { fixture, http, el } = create();
    http.expectOne(URL).flush(list([
      row(),
      row({ nodeId: 'f2', folderName: 'Tagged Saga', parentName: null, suggestedMode: 'PagedRtl', currentMode: 'PagedLtr', confidence: 'Medium',
        signal: 'ComicInfo', measuredPages: 0, tallPages: 0, comicInfoRightToLeft: 3, comicInfoStated: 4 }),
    ]));
    fixture.detectChanges();

    const rows = el.querySelectorAll('[data-testid="reading-mode-row"]');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('38 of 40 sampled pages are tall strips');
    expect(rows[0].querySelector('[data-testid="reading-mode-current"]')!.textContent).toContain('Right to left (library default)');
    expect(rows[0].querySelector('[data-testid="reading-mode-suggested"]')!.textContent).toContain('Vertical (webtoon)');
    expect(rows[0].querySelector('[data-testid="reading-mode-folder"]')!.getAttribute('href')).toBe('/libraries/lib1/browse/f1');
    expect(rows[1].textContent).toContain('ComicInfo says right to left in 3 of 4 archives');
    expect(rows[1].querySelector('[data-testid="reading-mode-confidence"]')!.textContent).toContain('Medium');
    expect(q(el, 'reading-modes-count').textContent).toContain('2 suggested');

    (rows[1].querySelector('[data-testid="reading-mode-apply"]') as HTMLButtonElement).click();
    const apply = http.expectOne(`${URL}/apply`);
    expect(apply.request.method).toBe('POST');
    expect(apply.request.body).toEqual({ items: [{ nodeId: 'f2', readerMode: 'PagedRtl' }] });
    apply.flush({ applied: 1, skipped: [] });
    http.expectOne(URL).flush(list([row()]));
    fixture.detectChanges();

    expect(q(el, 'reading-modes-message').textContent).toContain('Applied to 1 folder.');
    expect(el.querySelectorAll('[data-testid="reading-mode-row"]')).toHaveLength(1);
    http.verify();
  });

  it('applies the selected rows', () => {
    const { fixture, http, el } = create();
    http.expectOne(URL).flush(list([row(), row({ nodeId: 'f2', folderName: 'Second Strip' }), row({ nodeId: 'f3', folderName: 'Third Strip' })]));
    fixture.detectChanges();

    const apply = q(el, 'reading-modes-apply-selected') as HTMLButtonElement;
    expect(apply.disabled).toBe(true);
    (q(el, 'reading-modes-select-all').querySelector('input') as HTMLInputElement).click();
    fixture.detectChanges();
    const third = el.querySelectorAll('[data-testid="reading-mode-row"]')[2];
    (third.querySelector('input[type="checkbox"]') as HTMLInputElement).click();
    fixture.detectChanges();
    expect(apply.textContent).toContain('Apply selected (2)');

    apply.click();
    const req = http.expectOne(`${URL}/apply`);
    expect(req.request.body).toEqual({ items: [{ nodeId: 'f1', readerMode: 'VerticalWebtoon' }, { nodeId: 'f2', readerMode: 'VerticalWebtoon' }] });
    req.flush({ applied: 1, skipped: [{ nodeId: 'f2', reason: 'not_found' }] });
    http.expectOne(URL).flush(list([row({ nodeId: 'f3', folderName: 'Third Strip' })]));
    fixture.detectChanges();
    expect(q(el, 'reading-modes-message').textContent).toContain('1 folder could not be changed');
    http.verify();
  });

  it('shows the admin-set folders on demand, and an empty state', () => {
    const { fixture, http, el } = create();
    http.expectOne(URL).flush(list([], [row({ currentSource: 'FolderAbove', currentMode: 'PagedRtl' })]));
    fixture.detectChanges();

    expect(q(el, 'reading-modes-empty')).toBeTruthy();
    expect(q(el, 'reading-modes-bulk')).toBeNull();
    expect(el.querySelector('[data-testid="reading-mode-admin-row"]')).toBeNull();
    (q(el, 'reading-modes-admin-set-toggle') as HTMLButtonElement).click();
    fixture.detectChanges();
    const decided = el.querySelector('[data-testid="reading-mode-admin-row"]')!;
    expect(decided.textContent).toContain('Set: Right to left (set on a folder above)');
    expect(decided.textContent).toContain('Evidence points to Vertical (webtoon)');
    expect(decided.querySelector('button')).toBeNull();
    http.verify();
  });

  it('says so when the list cannot be loaded', () => {
    const { fixture, http, el } = create();
    http.expectOne(URL).flush('nope', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    expect(el.textContent).toContain('The suggestions could not be loaded.');
  });
});

describe('reading-mode labels', () => {
  it('names where the current mode comes from', () => {
    expect(currentText(row({ currentSource: 'None', currentMode: null }))).toBe('Not set - each reader\'s own default');
    expect(currentText(row({ currentSource: 'ThisFolder', currentMode: 'PagedLtr' }))).toBe('Left to right (set on this folder)');
  });

  it('lists the evidence, the deciding signal first', () => {
    expect(evidenceLines(row({ signal: 'LinkedRecord', measuredPages: 5, tallPages: 0, linkedOrigin: 'Korea', linkedWebtoon: true })))
      .toEqual(['Linked series: a webtoon, from Korea']);
    expect(evidenceLines(row({ subfolders: 2, comicInfoRightToLeft: 1, comicInfoStated: 1, linkedOrigin: 'Japan' }))).toEqual([
      '38 of 40 sampled pages are tall strips (in 2 subfolders)',
      'ComicInfo says right to left in 1 of 1 archive that states a direction',
      'Linked series: from Japan',
    ]);
  });

  it('sums up an apply', () => {
    expect(appliedSentence(3, 0)).toBe('Applied to 3 folders.');
    expect(appliedSentence(0, 2)).toBe('Nothing was applied. 2 folders could not be changed (removed or not a folder).');
  });
});
