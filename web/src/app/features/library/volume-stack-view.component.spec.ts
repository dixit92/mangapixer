import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { ReadStateService } from '../../core/reading/read-state.service';

import { VolumeStackViewComponent } from './volume-stack-view.component';
import { ApiService } from '../../core/api/api.service';
import { CatalogNodeDto, VolumeSlotDto, VolumeStackDto } from '../../core/api/api-types';

/**
 * The stack view (1.29.0): header, counts, source line, ordered slots with placeholders, previous / next volume. 1.30.0: the
 * viewer's Card / List preference, and the browse Select mode (tap, range, the shared selection bar with mark read / unread and
 * favorites, the admin archive actions).
 */
describe('VolumeStackViewComponent', () => {
  function item(id: string, name: string, extra: Partial<CatalogNodeDto> = {}): CatalogNodeDto {
    return {
      id, parentId: 'f1', libraryId: 'lib1', kind: 'Archive', displayName: name, availability: 'Available', coverUrl: `/api/v1/items/${id}/cover`,
      childFolderCount: null, childArchiveCount: null, pageCount: 20, readingState: null, lastReadPage: null, readerDefault: null,
      isRead: false, readRollup: null, hasSeriesInfo: false, ...extra,
    } as CatalogNodeDto;
  }

  const slots: VolumeSlotDto[] = [
    { kind: 'Item', chapter: '37', item: item('c37', 'Series c037') },
    { kind: 'Item', chapter: '38', item: item('c38', 'Series c038', { isRead: true }) },
    { kind: 'Missing', chapter: '39' },
    { kind: 'Item', chapter: '40', item: item('c40', 'Series c040', { readingState: 'InProgress' }) },
    { kind: 'Item', chapter: '40.5', item: item('c40x', 'Series c040.5') },
  ];

  function stack(over: Partial<VolumeStackDto> = {}): VolumeStackDto {
    return {
      folderId: 'f1', key: '5', label: 'Volume 5', coverUrl: '/api/v1/items/c37/cover', confidence: 'Exact', source: 'MangaDex',
      presentCount: 4, chapterCount: 5, missingCount: 1, extraCount: 1, previousKey: '4', nextKey: '6', slots, ...over,
    };
  }

  function setup(result: VolumeStackDto | null, opts: { viewMode?: string; stackViewMode?: string | null; listColumns?: number; admin?: boolean; prefsFail?: boolean } = {}) {
    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(opts.prefsFail
        ? throwError(() => new Error('500'))
        : of({ viewMode: opts.viewMode ?? 'card', density: 'comfortable', sort: 'name', direction: 'asc', listColumns: opts.listColumns ?? 2,
          stackViewMode: opts.stackViewMode ?? null })),
      setLibraryPreferences: vi.fn().mockReturnValue(of(undefined)),
      setItemRead: vi.fn().mockImplementation((id: string, read: boolean) => of({ itemId: id, isRead: read })),
      setFavorite: vi.fn().mockReturnValue(of(undefined)),
      getVolumeStack: vi.fn().mockReturnValue(result ? of(result) : throwError(() => new Error('404'))),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'My Library', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null, icon: null }])),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'f1', trail: [{ id: 'anc', displayName: 'Shelf' }] })),
      getNode: vi.fn().mockReturnValue(of({ displayName: 'My Series' } as CatalogNodeDto)),
    };
    TestBed.configureTestingModule({
      imports: [VolumeStackViewComponent],
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: { isAdmin: () => !!opts.admin, currentUser: () => null } },
        { provide: ReadStateService, useValue: new ReadStateService() },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => ({ libraryId: 'lib1', nodeId: 'f1', key: '5' } as Record<string, string>)[k] ?? null }) } },
      ],
    });
    const fixture = TestBed.createComponent(VolumeStackViewComponent);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, apiSpy };
  }

  function click(el: Element, init: MouseEventInit = {}): void {
    el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ...init }));
  }

  it('asks for the stack by folder and key, and shows the header', () => {
    const { el, apiSpy } = setup(stack());

    expect(apiSpy.getVolumeStack).toHaveBeenCalledWith('f1', '5');
    expect(el.querySelector('[data-testid="stack-title"]')!.textContent).toBe('Volume 5');
    expect(el.querySelector('[data-testid="stack-counts"]')!.textContent).toBe('3 of 5 chapters - 1 extra');
    expect(el.querySelector('[data-testid="stack-source"]')!.textContent).toBe("Grouped by the series' volume list");
  });

  it('names the REAL folder in the breadcrumbs and never a provider on the page', () => {
    const { el } = setup(stack());

    const crumbs = Array.from(el.querySelectorAll('.crumbs a')).map((a) => [a.textContent?.trim(), a.getAttribute('href')]);
    expect(crumbs).toEqual([
      ['My Library', '/libraries/lib1/browse'],
      ['Shelf', '/libraries/lib1/browse/anc'],
      ['My Series', '/libraries/lib1/browse/f1'],
    ]);
    expect(el.querySelector('.current')!.textContent).toBe('Volume 5');
    expect(el.textContent).not.toMatch(/MangaDex|AniList|MangaUpdates/);
  });

  it('lists chapter cards in order with a dashed placeholder where a chapter is missing', () => {
    const { el } = setup(stack());

    const cards = el.querySelectorAll('[data-testid="stack-slots"] > *');
    expect(cards).toHaveLength(5);
    const order = Array.from(cards).map((c) => (c.querySelector('[data-testid="missing-chapter"]') ? 'missing' : c.querySelector('.title')!.textContent));
    expect(order).toEqual(['Series c037', 'Series c038', 'missing', 'Series c040', 'Series c040.5']);
    const placeholder = el.querySelector('[data-testid="missing-chapter"]')!;
    expect(placeholder.getAttribute('aria-label')).toBe('Chapter 39, missing');
    // A placeholder has nothing to open; a chapter opens the reader.
    expect(placeholder.closest('a')).toBeNull();
    expect(el.querySelector('[data-testid="stack-item"]')!.getAttribute('href')).toBe('/reader/c37');
  });

  it('shows read state on the cards', () => {
    const { el } = setup(stack());

    const badges = Array.from(el.querySelectorAll('.badge')).map((b) => b.textContent?.trim());
    expect(badges).toEqual(['✓ Read', 'Reading']);
  });

  it('links previous and next volume inside the same folder', () => {
    const { el } = setup(stack());

    expect(el.querySelector('[data-testid="stack-prev"]')!.getAttribute('href')).toBe('/libraries/lib1/browse/f1/volume/4');
    expect(el.querySelector('[data-testid="stack-next"]')!.getAttribute('href')).toBe('/libraries/lib1/browse/f1/volume/6');
  });

  it('hides the arrows that have no target', () => {
    const { el } = setup(stack({ previousKey: null, nextKey: null }));

    expect(el.querySelector('[data-testid="stack-prev"]')).toBeNull();
    expect(el.querySelector('[data-testid="stack-next"]')).toBeNull();
  });

  it('says so when the volume is estimated, and where a file-name grouping came from', () => {
    const { el } = setup(stack({ confidence: 'Estimated', source: 'AniList', label: '~ Volume 5' }));
    expect(el.querySelector('[data-testid="stack-source"]')!.textContent).toContain('Volumes estimated from the published totals');
    expect(el.querySelector('[data-testid="stack-source"]')!.textContent).toContain('Volume boundaries are estimated');
  });

  it('describes a merged volume file + chapters, and a file-name grouping without a total', () => {
    const merged = stack({
      chapterCount: 2, missingCount: 0, extraCount: 0, presentCount: 3, source: 'FileNames',
      slots: [
        { kind: 'Item', chapter: null, item: item('v5', 'Series v05') },
        { kind: 'Item', chapter: '37', item: item('c37', 'Series c037') },
        { kind: 'Item', chapter: '38', item: item('c38', 'Series c038') },
      ],
    });
    const { el } = setup(merged);

    expect(el.querySelector('[data-testid="stack-counts"]')!.textContent).toBe('Volume file + 2 chapters');
    expect(el.querySelector('[data-testid="stack-source"]')!.textContent).toBe('Grouped by the volume in the file names');
    // The volume file has no chapter number; its card carries none.
    expect(el.querySelectorAll('[data-testid="missing-chapter"]')).toHaveLength(0);
  });

  it('offers a way back to the folder when the volume is not available', () => {
    const { el } = setup(null);

    const note = el.querySelector('[data-testid="stack-unavailable"]')!;
    expect(note.textContent).toContain('This volume is not available');
    expect(note.querySelector('a')!.getAttribute('href')).toBe('/libraries/lib1/browse/f1');
  });

  it('counts a split chapter once and shows a missing part in its place (1.29.0 RC)', () => {
    const split = stack({
      chapterCount: 3, chaptersPresent: 2, missingCount: 1, extraCount: 0, presentCount: 4, previousKey: null, nextKey: null,
      slots: [
        { kind: 'Item', chapter: '3', item: item('c3', 'Series c003') },
        { kind: 'Item', chapter: '4.1', item: item('c41', 'Series c004.1') },
        { kind: 'Item', chapter: '4.2', item: item('c42', 'Series c004.2') },
        { kind: 'Item', chapter: '5.1', item: item('c51', 'Series c005.1') },
        { kind: 'Missing', chapter: '5.2' },
      ],
    });
    const { el } = setup(split);

    expect(el.querySelector('[data-testid="stack-counts"]')!.textContent).toBe('2 of 3 chapters');
    expect(el.querySelector('[data-testid="missing-chapter"]')!.getAttribute('aria-label')).toBe('Chapter 5.2, missing');
  });
  // --- List view (1.30.0) ---

  it('has its own Card / List switch: remembered for the viewer, the other preferences sent back unchanged (1.30.0)', () => {
    const { el, fixture, apiSpy } = setup(stack(), { viewMode: 'card' });
    const list = el.querySelector('[data-testid="stack-view-list"]') as HTMLButtonElement;
    expect(el.querySelector('[data-testid="stack-view-card"]')!.getAttribute('aria-pressed')).toBe('true');
    list.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="stack-slots"]')!.classList).toContain('list');
    expect(list.getAttribute('aria-pressed')).toBe('true');
    const saved = apiSpy.setLibraryPreferences.mock.calls.at(-1)![0];
    expect(saved).toMatchObject({ stackViewMode: 'list', viewMode: 'card', listColumns: 2, sort: 'name' });
  });

  it('prefers its own stored choice over the library view mode', () => {
    const { el } = setup(stack(), { viewMode: 'card', stackViewMode: 'list' });
    expect(el.querySelector('[data-testid="stack-slots"]')!.classList).toContain('list');
  });

  it('follows the viewer\'s library view mode: Card by default, List rows when the preference says so', () => {
    const card = setup(stack());
    expect(card.el.querySelector('[data-testid="stack-slots"]')!.classList).not.toContain('list');
    expect(card.el.querySelectorAll('[data-testid="stack-item"]')).toHaveLength(4);
    expect(card.el.querySelector('app-node-row')).toBeNull();
  });

  it('shows the chapters as the shared list rows in List view, with a compact placeholder where one is missing', () => {
    TestBed.resetTestingModule();
    const { el, apiSpy } = setup(stack(), { viewMode: 'list', listColumns: 3 });

    expect(apiSpy.getLibraryPreferences).toHaveBeenCalled();
    const container = el.querySelector('[data-testid="stack-slots"]') as HTMLElement;
    expect(container.classList).toContain('list');
    expect(container.style.getPropertyValue('--list-columns')).toBe('3');
    const rows = el.querySelectorAll('[data-testid="stack-row"]');
    expect(rows).toHaveLength(4);
    // The row: title, "Ch. 37 · 20 pages", a link to the reader, the leading checkbox, the star and the read badge.
    const first = rows[0];
    expect(first.querySelector('.node-title')!.textContent).toBe('Series c037');
    expect(first.querySelector('.node-sub')!.textContent).toBe('Ch. 37 · 20 pages');
    expect(first.querySelector('a.node-card')!.getAttribute('href')).toBe('/reader/c37');
    expect(first.querySelector('.row-select')).not.toBeNull();
    expect(first.querySelector('app-star-toggle')).not.toBeNull();
    expect(rows[1].querySelector('.badge.read')).not.toBeNull();
    expect(rows[2].querySelector('.badge.reading')).not.toBeNull();
    // Order kept, the missing chapter in its place as a compact placeholder (nothing to open).
    const order = Array.from(container.children).map((c) => (c.querySelector('[data-testid="missing-chapter"]') ? 'missing' : c.querySelector('.node-title')?.textContent));
    expect(order).toEqual(['Series c037', 'Series c038', 'missing', 'Series c040', 'Series c040.5']);
    expect(container.querySelector('app-missing-chapter-card')!.classList).toContain('compact');
    expect(container.querySelector('[data-testid="missing-chapter"]')!.closest('a')).toBeNull();
  });

  it('falls back to Card view when the preferences cannot be read', () => {
    const { comp, el } = setup(stack(), { prefsFail: true });

    expect(comp.viewMode()).toBe('card');
    expect(el.querySelectorAll('[data-testid="stack-item"]')).toHaveLength(4);
  });

  // --- Select mode (1.30.0) ---

  it('offers a Select button, and selecting turns the bar into the shared selection bar', () => {
    const { fixture, el } = setup(stack());
    const select = el.querySelector('[data-testid="stack-select"]') as HTMLButtonElement;
    expect(select).not.toBeNull();

    select.click();
    fixture.detectChanges();

    expect(el.querySelector('.bar.selecting')).not.toBeNull();
    expect(el.querySelector('nav.bar')).toBeNull();
    expect(el.querySelector('[data-testid="selection-count"]')!.textContent!.trim()).toBe('0 selected');
    // Cards stop navigating and show a select check; the star and (i) make way for it.
    const card = el.querySelector('[data-testid="stack-item"]')!;
    expect(card.getAttribute('href')).toBeNull();
    expect(card.querySelector('[data-testid="stack-check"]')).not.toBeNull();
    expect(card.querySelector('app-star-toggle')).toBeNull();

    (el.querySelector('[data-testid="selection-done"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelector('nav.bar')).not.toBeNull();
    expect(el.querySelector('[data-testid="stack-item"]')!.getAttribute('href')).toBe('/reader/c37');
  });

  it('has no Select button when the volume holds nothing to select', () => {
    const { el } = setup(stack({ slots: [{ kind: 'Missing', chapter: '1' }], presentCount: 0 }));
    expect(el.querySelector('[data-testid="stack-select"]')).toBeNull();
  });

  it('a tap selects a chapter (it does not open the reader), Shift-click fills a range, a placeholder is skipped', () => {
    const { fixture, comp, el } = setup(stack());
    comp.selection.toggleMode();
    fixture.detectChanges();
    const cards = () => Array.from(el.querySelectorAll('[data-testid="stack-item"]'));

    click(cards()[0]);
    fixture.detectChanges();
    expect([...comp.selection.selected()]).toEqual(['c37']);
    expect(cards()[0].closest('.slot-wrap')!.classList).toContain('selected');

    // Range c37 .. c40: c38, the (missing) chapter 39 slot and c40 - only the real chapters are added.
    click(cards()[2], { shiftKey: true });
    fixture.detectChanges();
    expect([...comp.selection.selected()].sort()).toEqual(['c37', 'c38', 'c40']);

    // A second tap deselects.
    click(cards()[1]);
    expect([...comp.selection.selected()].sort()).toEqual(['c37', 'c40']);
  });

  it('Select all / all unread / all read act over the volume\'s chapters, never a placeholder', () => {
    const { comp } = setup(stack());

    comp.selection.selectAll();
    expect([...comp.selection.selected()].sort()).toEqual(['c37', 'c38', 'c40', 'c40x']);
    comp.selection.selectWhere(comp.isUnread);
    expect([...comp.selection.selected()].sort()).toEqual(['c37', 'c40', 'c40x']);
    comp.selection.selectWhere(comp.isRead);
    expect([...comp.selection.selected()]).toEqual(['c38']);
  });

  it('long-press on a touch screen selects the pressed chapter and enters select mode', () => {
    vi.useFakeTimers();
    try {
      const { fixture, comp, el } = setup(stack());
      const card = el.querySelector('[data-testid="stack-item"]') as HTMLElement;

      card.dispatchEvent(new PointerEvent('pointerdown', { pointerType: 'touch', bubbles: true }));
      vi.advanceTimersByTime(600);
      fixture.detectChanges();

      expect(comp.selection.mode()).toBe(true);
      expect([...comp.selection.selected()]).toEqual(['c37']);
      // The click the browser fires on release is swallowed (it must not open the reader or undo the selection).
      click(card);
      expect([...comp.selection.selected()]).toEqual(['c37']);
    } finally {
      vi.useRealTimers();
    }
  });

  it('marks the selection read, then unread, and patches the chapters in place', () => {
    const { fixture, comp, el, apiSpy } = setup(stack());
    const readState = TestBed.inject(ReadStateService);
    const notified: string[] = [];
    readState.itemChanged$.subscribe((id) => notified.push(id));
    comp.selection.toggleMode();
    fixture.detectChanges();
    comp.selection.selectWhere(comp.isUnread);
    fixture.detectChanges();

    (el.querySelector('[data-testid="selection-mark-read"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(apiSpy.setItemRead.mock.calls.map((c) => [c[0], c[1]])).toEqual([['c37', true], ['c40', true], ['c40x', true]]);
    expect(comp.items().every((i) => i.isRead)).toBe(true);
    expect(el.querySelectorAll('.badge.read')).toHaveLength(4);
    expect(notified.sort()).toEqual(['c37', 'c40', 'c40x']); // the folder list is told to refresh its stack card
    expect(comp.busy()).toBe(false);

    (el.querySelector('[data-testid="selection-mark-unread"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(apiSpy.setItemRead).toHaveBeenLastCalledWith('c40x', false);
    expect(comp.items().find((i) => i.id === 'c37')!.isRead).toBe(false);
    expect(comp.items().find((i) => i.id === 'c40')!.readingState).toBe('Unread'); // a full reset
  });

  it('adds the selection to the favorites and removes it again', () => {
    const { comp, apiSpy } = setup(stack());
    comp.selection.selected.set(new Set(['c37', 'c38']));

    comp.favorite(true);
    expect(apiSpy.setFavorite.mock.calls.map((c) => [c[0], c[1]])).toEqual([['c37', true], ['c38', true]]);
    expect(comp.items().filter((i) => i.isFavorite).map((i) => i.id)).toEqual(['c37', 'c38']);

    comp.favorite(false);
    expect(comp.items().some((i) => i.isFavorite)).toBe(false);
  });

  it('shows the admin archive actions (series metadata, cover) to an admin only', () => {
    const viewer = setup(stack());
    viewer.comp.selection.toggleMode();
    viewer.fixture.detectChanges();
    expect(viewer.el.querySelector('app-series-selection-actions')).toBeNull();
    TestBed.resetTestingModule();

    const admin = setup(stack(), { admin: true });
    admin.comp.selection.toggleMode();
    admin.fixture.detectChanges();
    expect(admin.el.querySelector('app-series-selection-actions')).not.toBeNull();
    expect(admin.el.querySelector('app-cover-selection-action')).not.toBeNull();
  });

  it('list rows select through their checkbox, which turns select mode on', () => {
    TestBed.resetTestingModule();
    const { fixture, comp, el } = setup(stack(), { viewMode: 'list' });

    (el.querySelectorAll('[data-testid="stack-row"]')[1].querySelector('.row-select') as HTMLElement).click();
    fixture.detectChanges();

    expect(comp.selection.mode()).toBe(true);
    expect([...comp.selection.selected()]).toEqual(['c38']);
    expect(el.querySelectorAll('[data-testid="stack-row"]')[1].querySelector('.node-wrap')!.classList).toContain('selected');
  });

  it('1.31.0: counts a chapter in two files once, names the duplicates and marks each of the two cards', () => {
    const dupSlots: VolumeSlotDto[] = [
      { kind: 'Item', chapter: '1', item: item('a1', 'Series c001') },
      { kind: 'Item', chapter: '1', item: item('a1b', 'Series c001 [2]') },
      { kind: 'Item', chapter: '2', item: item('a2', 'Series c002') },
    ];
    const { el } = setup(stack({ slots: dupSlots, chapterCount: null, chaptersPresent: null, presentCount: 2, missingCount: 0, extraCount: 0,
      duplicates: [{ kind: 'Chapter', number: '1', files: 2 }] }));

    // Two chapters (1 and 2), not three files.
    expect(el.querySelector('[data-testid="stack-counts"]')!.textContent).toBe('2 chapters');
    expect(el.querySelector('[data-testid="stack-duplicates"]')!.textContent).toBe('1 duplicate chapter: Chapter 1: 2 files');
    const subs = Array.from(el.querySelectorAll('.slot .sub')).map((s) => s.textContent!.trim());
    expect(subs).toEqual(['Ch. 1 · 2 files · 20 pages', 'Ch. 1 · 2 files · 20 pages', 'Ch. 2 · 20 pages']);
    expect(el.querySelectorAll('[data-testid="stack-item"]')).toHaveLength(3); // every file keeps its card
  });

  it('1.31.0: shows no duplicate line without duplicates', () => {
    expect(setup(stack()).el.querySelector('[data-testid="stack-duplicates"]')).toBeNull();
  });
});
