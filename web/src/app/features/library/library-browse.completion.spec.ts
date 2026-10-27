import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { LibraryBrowseComponent } from './library-browse.component';
import { COMPLETION_OPTIONS, completionLabel, completionOptionLabel } from './completion-filter';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { ReadStateService } from '../../core/reading/read-state.service';
import { BrowseCompletionCountsDto, CatalogNodeDto, JumpIndexBucketDto, LibraryViewPreferencesDto, PageResponse } from '../../core/api/api-types';

// Card size and list columns are per-device (localStorage, 1.48.0): every test starts on a device with no copy.
beforeEach(() => localStorage.clear());

/**
 * The library Filter menu's Completion section (1.44.0, lane C): shown only where the level holds linked series, sends the `completion`
 * browse param (the 13th `browseLibrary` argument), reloads from the top, labels the button, hides the jump rail while it applies,
 * shows the server's per-answer counts, and does nothing where the level holds no linked series.
 */
describe('LibraryBrowseComponent Completion filter (1.44.0)', () => {
  const COMPLETION_ARG = 12;
  const COUNTS: BrowseCompletionCountsDto = { haveItAll: 3, finishedMissing: 1, upToDate: 0, missingSome: 42, cantTell: 2 };

  function node(id: string): CatalogNodeDto {
    return {
      id, parentId: 'p', libraryId: 'lib1', kind: 'Folder', displayName: id,
      availability: 'Available', coverUrl: null, childFolderCount: null, childArchiveCount: null,
      pageCount: null, readingState: null, lastReadPage: null, readerDefault: null, isRead: false, readRollup: null,
    } as unknown as CatalogNodeDto;
  }

  /** A first page; `linkedSeries` as the server says, the counts only when the request carried a completion. */
  function respond(linkedSeries: number) {
    return (...args: unknown[]): unknown => of({
      items: [node('a'), node('b')], totalCount: 2, nextCursor: null, hasMore: false,
      linkedSeries, completionCounts: args[COMPLETION_ARG] ? COUNTS : null,
    } as PageResponse<CatalogNodeDto>);
  }

  function setup(linkedSeries: number, buckets: JumpIndexBucketDto[] = []) {
    const prefs = { viewMode: 'card', density: 'comfortable', sort: 'name' } as LibraryViewPreferencesDto;
    const browseLibrary = vi.fn(respond(linkedSeries));
    const getJumpIndex = vi.fn().mockReturnValue(of({ libraryId: 'lib1', buckets }));
    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        {
          provide: ApiService, useValue: {
            getVolumeView: vi.fn().mockReturnValue(of({ nodeId: 'x', available: false, active: false, consolidated: false, stackCount: 0 })),
            getLibraryPreferences: vi.fn().mockReturnValue(of(prefs)),
            setLibraryPreferences: vi.fn().mockReturnValue(of(undefined)),
            getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null, icon: null }])),
            browseLibrary,
            getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'x', trail: [] })),
            getJumpIndex,
            getReadMark: vi.fn().mockReturnValue(of({ itemId: '', isRead: false })),
            getProgress: vi.fn().mockReturnValue(of(null)),
          },
        },
        { provide: AuthService, useValue: { isAdmin: () => false } },
        { provide: ReadStateService, useValue: new ReadStateService() },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : null) }) } },
      ],
    });
    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, browseLibrary, getJumpIndex, el: fixture.nativeElement as HTMLElement };
  }

  function openFilterMenu(fixture: ReturnType<typeof setup>['fixture'], el: HTMLElement): void {
    (el.querySelector('.filter-toggle') as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  const item = (testId: string) => document.querySelector(`[data-testid="${testId}"]`) as HTMLElement | null;

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  it('the first browse sends no completion', () => {
    const { browseLibrary } = setup(3);
    expect(browseLibrary.mock.calls[0][COMPLETION_ARG] ?? null).toBeNull();
  });

  it('shows the Completion section (Any + the five answers) only where the level holds linked series', () => {
    const shown = setup(3);
    openFilterMenu(shown.fixture, shown.el);
    expect(item('completion-any')?.getAttribute('aria-checked')).toBe('true');
    const texts = COMPLETION_OPTIONS.map((o) => item(`completion-${o.value}`)?.textContent ?? '');
    ['Have it all', 'Finished, missing some', 'Everything so far', 'Missing some', 'Can\'t tell'].forEach((label, i) =>
      expect(texts[i]).toContain(label));
    TestBed.resetTestingModule();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));

    const hidden = setup(0);
    openFilterMenu(hidden.fixture, hidden.el);
    expect(document.querySelector('[role="menu"]')).not.toBeNull(); // the menu is open ...
    expect(item('completion-any')).toBeNull();                      // ... without the section
    expect(item('completion-HaveItAll')).toBeNull();
  });

  it('choosing an answer reloads from the top with it, labels the button and reloads the jump rail with it', () => {
    const { comp, browseLibrary, getJumpIndex, fixture, el } = setup(3, [{ label: 'A', count: 20, firstCursor: null } as JumpIndexBucketDto]);
    expect(comp.jumpBuckets().length).toBe(1);
    openFilterMenu(fixture, el);
    item('completion-MissingSome')!.click();
    fixture.detectChanges();

    const last = browseLibrary.mock.calls.at(-1)!;
    expect(last[2]).toBeNull();                  // from the top
    expect(last[COMPLETION_ARG]).toBe('MissingSome');
    expect(comp.completionFilter()).toBe('MissingSome');
    expect(comp.filterActive()).toBe(true);
    expect(comp.readStateLabel()).toBe('Filtered');
    expect(comp.filterIcon()).toBe('error_outline');
    expect(comp.filterTooltip()).toContain('Completion: Missing some');
    expect(getJumpIndex.mock.calls.at(-1)![4]).toEqual(expect.objectContaining({ completion: 'MissingSome' }));
    expect(comp.jumpBuckets().length).toBe(1);
    expect(item('completion-MissingSome')?.getAttribute('aria-checked')).toBe('true');
  });

  it('shows the counts the server returned once the filter applies, and Any clears them', () => {
    const { comp, browseLibrary, fixture, el } = setup(3);
    comp.setCompletionFilter('HaveItAll');
    fixture.detectChanges();
    openFilterMenu(fixture, el);
    expect(item('completion-MissingSome')?.textContent).toContain('Missing some (42)');
    expect(item('completion-HaveItAll')?.textContent).toContain('Have it all (3)');

    item('completion-any')!.click();
    fixture.detectChanges();
    expect(browseLibrary.mock.calls.at(-1)![COMPLETION_ARG]).toBeNull();
    expect(comp.completionCounts()).toBeNull();
    expect(comp.filterActive()).toBe(false);
    expect(comp.readStateLabel()).toBe('Filter');
  });

  it('with other filters on too, the button still says Filtered with the answer icon', () => {
    const { comp } = setup(3);
    comp.setCompletionFilter('CantTell');
    comp.setReadStateFilter('unread');
    expect(comp.readStateLabel()).toBe('Filtered');
    expect(comp.filterIcon()).toBe('help_outline');
    comp.setCompletionFilter(null);
    expect(comp.readStateLabel()).toBe('Unread');
    expect(comp.filterIcon()).toBe('filter_list');
  });

  it('where the level holds no linked series the kept choice changes nothing on screen (label, rail)', () => {
    const { comp } = setup(0, [{ label: 'A', count: 1, firstCursor: null } as JumpIndexBucketDto]);
    comp.setCompletionFilter('HaveItAll');
    expect(comp.completionApplies()).toBe(false);
    expect(comp.filterActive()).toBe(false);
    expect(comp.readStateLabel()).toBe('Filter');
    expect(comp.filterIcon()).toBe('filter_list');
    expect(comp.jumpBuckets().length).toBe(1);
  });

  it('1.45.0 (UX review #6): an answer that leaves nothing says so and offers Any - never "This folder is empty"', () => {
    const { comp, fixture, el, browseLibrary } = setup(3);
    browseLibrary.mockImplementation((...args: unknown[]) => of({
      items: args[COMPLETION_ARG] ? [] : [node('a'), node('b')], totalCount: 0, nextCursor: null, hasMore: false,
      linkedSeries: 3, completionCounts: args[COMPLETION_ARG] ? COUNTS : null,
    } as PageResponse<CatalogNodeDto>));
    comp.setCompletionFilter('UpToDate');
    fixture.detectChanges();
    const empty = el.querySelector('[data-testid="browse-empty-filtered"]')!;
    expect(empty.textContent).toContain('No series here match "Everything so far".');
    expect(el.textContent).not.toContain('This folder is empty.');
    (el.querySelector('[data-testid="browse-empty-any"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(comp.completionFilter()).toBeNull();
    expect(browseLibrary.mock.lastCall![COMPLETION_ARG] ?? null).toBeNull();
    expect(el.querySelectorAll('.node-wrap')).toHaveLength(2);
  });

  it('1.45.0 (UX review #6): other filters that leave nothing say so and clear every filter in one reload', () => {
    const { comp, fixture, el, browseLibrary } = setup(0);
    browseLibrary.mockImplementation((...args: unknown[]) => of({
      items: args[6] && args[6] !== 'all' || args[9] ? [] : [node('a')], totalCount: 0, nextCursor: null, hasMore: false, linkedSeries: 0,
    } as PageResponse<CatalogNodeDto>));
    comp.setReadStateFilter('unread');
    comp.toggleFavoritesOnly();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="browse-empty-filtered"]')!.textContent).toContain('Nothing here matches the filter.');
    const calls = browseLibrary.mock.calls.length;
    (el.querySelector('[data-testid="browse-empty-clear"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(browseLibrary.mock.calls.length).toBe(calls + 1);
    expect(comp.filterActive()).toBe(false);
    expect(el.querySelector('[data-testid="browse-empty-filtered"]')).toBeNull();
    expect(el.querySelectorAll('.node-wrap')).toHaveLength(1);
  });

  it('labels: the short answer, and the count once counted', () => {
    expect(completionLabel('FinishedMissing')).toBe('Finished, missing some');
    const missing = COMPLETION_OPTIONS.find((o) => o.value === 'MissingSome')!;
    expect(missing.icon).toBe('error_outline');
    expect(completionOptionLabel(missing, null)).toBe('Missing some');
    expect(completionOptionLabel(missing, COUNTS)).toBe('Missing some (42)');
    expect(completionOptionLabel(COMPLETION_OPTIONS.find((o) => o.value === 'UpToDate')!, COUNTS)).toBe('Everything so far (0)');
  });
});
