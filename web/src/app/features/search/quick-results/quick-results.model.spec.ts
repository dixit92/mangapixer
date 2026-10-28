import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, of } from 'rxjs';

import { ApiService } from '../../../core/api/api.service';
import { SearchSuggestDto } from '../../../core/api/api-types';
import { QUICK_DEBOUNCE_MS, QuickResults, completeToken, nodeCommands, quickEntries, quickSections } from './quick-results.model';

function answer(partial: Partial<SearchSuggestDto> = {}): SearchSuggestDto {
  return { query: 'q', series: [], folders: [], archives: [], creators: [], tags: [], field: null, ...partial };
}

describe('quick results model', () => {
  it('groups the non-empty lists in the owner order: series, folders, archives, creators, tags', () => {
    const sections = quickSections(answer({
      tags: [{ kind: 'tag', id: 'fv_t', label: 'Time Travel', filter: 'tag:fv_t', count: 2 }],
      folders: [{ kind: 'folder', id: 'f1', label: 'Berserk', libraryId: 'L1', nodeKind: 'Folder', detail: 'Manga' }],
      series: [{ kind: 'series', id: 's1', label: 'Monster', libraryId: 'L1', nodeKind: 'Folder', detail: 'aka Kaibutsu' }],
      creators: [{ kind: 'creator', id: 'fv_c', label: 'Naoki Urasawa', filter: 'creator:fv_c', count: 1 }],
    }));
    expect(sections.map((s) => s.title)).toEqual(['Series', 'Folders', 'Creators', 'Tags']);
    expect(quickEntries(sections).map((e) => e.label)).toEqual(['Monster', 'Berserk', 'Naoki Urasawa', 'Time Travel']);
  });

  it('opens a folder in browse, an archive in the reader, and a creator or tag as a tag-view filter', () => {
    const entries = quickEntries(quickSections(answer({
      folders: [{ kind: 'folder', id: 'f1', label: 'F', libraryId: 'L1', nodeKind: 'Folder' }],
      archives: [{ kind: 'archive', id: 'a1', label: 'A', libraryId: 'L1', nodeKind: 'Archive' }],
      series: [{ kind: 'series', id: 's1', label: 'S', libraryId: 'L2', nodeKind: 'Archive' }],
      tags: [{ kind: 'genre', id: 'fv_g', label: 'Horror', filter: 'genre:fv_g', count: 3 }],
    })));
    expect(entries.map((e) => e.action)).toEqual([
      { type: 'open', commands: ['/reader', 's1'] },
      { type: 'open', commands: ['/libraries', 'L1', 'browse', 'f1'] },
      { type: 'open', commands: ['/reader', 'a1'] },
      { type: 'filter', filter: 'genre:fv_g' },
    ]);
    expect(nodeCommands({ kind: 'folder', id: 'x', label: 'x', libraryId: 'L9' })).toEqual(['/libraries', 'L9', 'browse', 'x']);
  });

  it('shows a typed field\'s values alone, each completing the token from where it starts', () => {
    const sections = quickSections(answer({
      folders: [{ kind: 'folder', id: 'f1', label: 'ignored', libraryId: 'L1', nodeKind: 'Folder' }],
      field: { name: 'creator', prefix: 'ura', start: 8, items: [{ label: 'Naoki Urasawa', insert: 'creator:"Naoki Urasawa"', count: 1 }] },
    }));
    expect(sections.map((s) => s.title)).toEqual(['Creators']);
    expect(sections[0].entries[0].action).toEqual({ type: 'complete', start: 8, insert: 'creator:"Naoki Urasawa"' });
    expect(completeToken('berserk creator:ura', 8, 'creator:"Naoki Urasawa"')).toBe('berserk creator:"Naoki Urasawa" ');
    // A field with no value to offer is an empty list, not the groups.
    expect(quickSections(answer({ field: { name: 'tag', prefix: 'zz', start: 0, items: [] } }))).toEqual([]);
    expect(quickSections(null)).toEqual([]);
  });
});

describe('QuickResults (debounce, cancel, keyboard)', () => {
  let searchSuggest: ReturnType<typeof vi.fn>;
  let quick: QuickResults;

  function create(impl: (text: string) => Observable<SearchSuggestDto>): void {
    searchSuggest = vi.fn(impl);
    TestBed.configureTestingModule({ providers: [{ provide: ApiService, useValue: { searchSuggest } }] });
    quick = TestBed.runInInjectionContext(() => new QuickResults());
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  const one = (label: string) => answer({ folders: [{ kind: 'folder', id: label, label, libraryId: 'L1', nodeKind: 'Folder' }] });

  it('asks once, 200 ms after the last keystroke', () => {
    create((text) => of(one(text)));
    quick.ask('b');
    quick.ask('be');
    quick.ask('bers');
    vi.advanceTimersByTime(QUICK_DEBOUNCE_MS - 1);
    expect(searchSuggest).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(searchSuggest).toHaveBeenCalledTimes(1);
    expect(searchSuggest).toHaveBeenCalledWith('bers');
    expect(quick.entries().map((e) => e.label)).toEqual(['bers']);
  });

  it('asks about nothing shorter than two characters, and drops the old answer', () => {
    create((text) => of(one(text)));
    quick.ask('be');
    vi.advanceTimersByTime(QUICK_DEBOUNCE_MS);
    expect(quick.entries().length).toBe(1);
    quick.ask('b');
    vi.advanceTimersByTime(QUICK_DEBOUNCE_MS);
    expect(searchSuggest).toHaveBeenCalledTimes(1);
    expect(quick.entries()).toEqual([]);
  });

  it('cancels the request a newer text replaces: a late answer never overwrites a newer one', () => {
    const pending: Record<string, Subject<SearchSuggestDto>> = {};
    create((text) => (pending[text] = new Subject<SearchSuggestDto>()));
    quick.ask('ber');
    vi.advanceTimersByTime(QUICK_DEBOUNCE_MS);
    quick.ask('bers');
    vi.advanceTimersByTime(QUICK_DEBOUNCE_MS);
    expect(searchSuggest).toHaveBeenCalledTimes(2);
    expect(pending['ber'].observed).toBe(false); // unsubscribed = the HTTP request is cancelled
    pending['bers'].next(one('newer'));
    pending['ber'].next(one('late'));
    expect(quick.entries().map((e) => e.label)).toEqual(['newer']);
  });

  it('keeps going after a failed request (an empty list, the next text asks again)', () => {
    let fail = true;
    create((text) => (fail ? new Observable<SearchSuggestDto>((s) => s.error(new Error('x'))) : of(one(text))));
    quick.ask('ber');
    vi.advanceTimersByTime(QUICK_DEBOUNCE_MS);
    expect(quick.entries()).toEqual([]);
    fail = false;
    quick.ask('bers');
    vi.advanceTimersByTime(QUICK_DEBOUNCE_MS);
    expect(quick.entries().map((e) => e.label)).toEqual(['bers']);
  });

  it('moves the keyboard through the entries and back through "none"', () => {
    create(() => of(answer({
      folders: [{ kind: 'folder', id: 'a', label: 'A', libraryId: 'L', nodeKind: 'Folder' }, { kind: 'folder', id: 'b', label: 'B', libraryId: 'L', nodeKind: 'Folder' }],
    })));
    quick.ask('ab');
    vi.advanceTimersByTime(QUICK_DEBOUNCE_MS);
    expect(quick.active()).toBe(-1);
    quick.move(1);
    expect(quick.activeEntry()?.label).toBe('A');
    quick.move(1);
    expect(quick.activeEntry()?.label).toBe('B');
    quick.move(1);
    expect(quick.active()).toBe(-1);
    quick.move(-1);
    expect(quick.activeEntry()?.label).toBe('B');
  });
});
