import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { ReadStateService } from '../../core/reading/read-state.service';

import { CollectionStackViewComponent } from './collection-stack-view.component';
import { ApiService } from '../../core/api/api.service';
import { CatalogNodeDto, CollectionStackDto } from '../../core/api/api-types';
import { storiesLabel } from '../../shared/volume-stack/stories-label';

/**
 * The stack view of stories collected in one volume (1.37.0, tankoubon stacks): header (cover, title, "N stories"), the REAL folder in
 * the breadcrumbs, the stories in folder order with no volume / missing marks, previous / next stack, the Card / List choice and the
 * browse Select mode.
 */
describe('CollectionStackViewComponent', () => {
  function item(id: string, name: string, extra: Partial<CatalogNodeDto> = {}): CatalogNodeDto {
    return {
      id, parentId: 'f1', libraryId: 'lib1', kind: 'Archive', displayName: name, availability: 'Available', coverUrl: `/api/v1/items/${id}/cover`,
      childFolderCount: null, childArchiveCount: null, pageCount: 20, readingState: null, lastReadPage: null, readerDefault: null,
      isRead: false, readRollup: null, hasSeriesInfo: false, ...extra,
    } as CatalogNodeDto;
  }

  function stack(over: Partial<CollectionStackDto> = {}): CollectionStackDto {
    return {
      folderId: 'f1', key: 'rec1', title: 'Synthetic Collected Volume', coverUrl: '/api/v1/nodes/f1/collection-stacks/rec1/cover?v=x',
      storyCount: 3, previousKey: 'rec0', nextKey: null,
      items: [item('s1', 'Artist - Story One'), item('s2', 'Artist - Story Two', { isRead: true }), item('s3', 'Artist - Story Three', { readingState: 'InProgress' })],
      ...over,
    };
  }

  function setup(result: CollectionStackDto | null, opts: { viewMode?: string; admin?: boolean } = {}) {
    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(
        of({ viewMode: opts.viewMode ?? 'card', density: 'comfortable', sort: 'name', direction: 'asc', listColumns: 2, stackViewMode: null })),
      setLibraryPreferences: vi.fn().mockReturnValue(of(undefined)),
      setItemRead: vi.fn().mockImplementation((id: string, read: boolean) => of({ itemId: id, isRead: read })),
      setFavorite: vi.fn().mockReturnValue(of(undefined)),
      getCollectionStack: vi.fn().mockReturnValue(result ? of(result) : throwError(() => new Error('404'))),
      getVolumeStack: vi.fn(),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'My Library', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null, icon: null }])),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'f1', trail: [{ id: 'anc', displayName: 'Artists' }] })),
      getNode: vi.fn().mockReturnValue(of({ displayName: 'Sample Artist' } as CatalogNodeDto)),
    };
    TestBed.configureTestingModule({
      imports: [CollectionStackViewComponent],
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: { isAdmin: () => !!opts.admin, currentUser: () => null } },
        { provide: ReadStateService, useValue: new ReadStateService() },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => ({ libraryId: 'lib1', nodeId: 'f1', key: 'rec1' } as Record<string, string>)[k] ?? null }) } },
      ],
    });
    const fixture = TestBed.createComponent(CollectionStackViewComponent);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, apiSpy };
  }

  it('asks the collection endpoint by folder and key (never the volume one) and shows the header', () => {
    const { el, apiSpy } = setup(stack());

    expect(apiSpy.getCollectionStack).toHaveBeenCalledWith('f1', 'rec1');
    expect(apiSpy.getVolumeStack).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="stack-title"]')!.textContent).toBe('Synthetic Collected Volume');
    expect(el.querySelector('[data-testid="stack-counts"]')!.textContent).toBe('3 stories - 1 read');
    expect(el.querySelector('[data-testid="stack-source"]')!.textContent).toBe('Stories collected in one volume');
    expect((el.querySelector('.head-cover img') as HTMLImageElement).getAttribute('src')).toBe('/api/v1/nodes/f1/collection-stacks/rec1/cover?v=x');
  });

  it('names the REAL folder in the breadcrumbs, and the stack as the current page', () => {
    const { el } = setup(stack());

    const crumbs = Array.from(el.querySelectorAll('.crumbs a')).map((a) => [a.textContent?.trim(), a.getAttribute('href')]);
    expect(crumbs).toEqual([
      ['My Library', '/libraries/lib1/browse'],
      ['Artists', '/libraries/lib1/browse/anc'],
      ['Sample Artist', '/libraries/lib1/browse/f1'],
    ]);
    expect(el.querySelector('.current')!.textContent).toBe('Synthetic Collected Volume');
  });

  it('lists the stories in order, opening the reader, with read state and no volume or missing marks', () => {
    const { el } = setup(stack());

    const cards = Array.from(el.querySelectorAll('[data-testid="stack-item"]'));
    expect(cards.map((c) => c.querySelector('.title')!.textContent)).toEqual(['Artist - Story One', 'Artist - Story Two', 'Artist - Story Three']);
    expect(cards.map((c) => c.getAttribute('href'))).toEqual(['/reader/s1', '/reader/s2', '/reader/s3']);
    expect(cards[1].querySelector('.badge.read')).not.toBeNull();
    expect(cards[2].querySelector('.badge.reading')).not.toBeNull();
    expect(cards[0].querySelector('.sub')!.textContent).toBe('20 pages');
    expect(el.querySelector('[data-testid="missing-chapter"]')).toBeNull();
    expect(el.textContent).not.toMatch(/Volume \d|missing|MangaUpdates/);
  });

  it('links the previous stack of the folder and hides an arrow without a target', () => {
    const { el } = setup(stack());

    expect(el.querySelector('[data-testid="stack-prev"]')!.getAttribute('href')).toBe('/libraries/lib1/browse/f1/collection/rec0');
    expect(el.querySelector('[data-testid="stack-next"]')).toBeNull();
  });

  it('offers a way back to the folder when the stack is gone', () => {
    const { el } = setup(null);

    const msg = el.querySelector('[data-testid="stack-unavailable"]')!;
    expect(msg.textContent).toContain('not stacked here any more');
    expect(msg.querySelector('a')!.getAttribute('href')).toBe('/libraries/lib1/browse/f1');
  });

  it('shows the stories as the shared list rows in List view', () => {
    const { el } = setup(stack(), { viewMode: 'list' });

    const rows = el.querySelectorAll('[data-testid="stack-row"]');
    expect(rows).toHaveLength(3);
    expect(rows[0].querySelector('.node-title')!.textContent).toBe('Artist - Story One');
    expect(rows[0].querySelector('a.node-card')!.getAttribute('href')).toBe('/reader/s1');
  });

  it('marks the selection read and tells the folder list, then adds it to the favorites', () => {
    const { fixture, comp, el, apiSpy } = setup(stack());
    const notified: string[] = [];
    TestBed.inject(ReadStateService).itemChanged$.subscribe((id) => notified.push(id));
    comp.selection.toggleMode();
    fixture.detectChanges();
    comp.selection.selectWhere(comp.isUnread);
    fixture.detectChanges();

    (el.querySelector('[data-testid="selection-mark-read"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(apiSpy.setItemRead.mock.calls.map((c) => [c[0], c[1]])).toEqual([['s1', true], ['s3', true]]);
    expect(comp.items().every((i) => i.isRead)).toBe(true);
    expect(notified.sort()).toEqual(['s1', 's3']);
    expect(el.querySelector('[data-testid="stack-counts"]')!.textContent).toBe('3 stories - 3 read');

    comp.favorite(true);
    expect(apiSpy.setFavorite.mock.calls.map((c) => c[0])).toEqual(['s1', 's3']);
  });

  it('shows the admin archive actions in Select mode to an admin only', () => {
    const admin = setup(stack(), { admin: true });
    admin.comp.selection.toggleMode();
    admin.fixture.detectChanges();
    expect(admin.el.querySelector('app-series-selection-actions')).not.toBeNull();
    TestBed.resetTestingModule();
    const reader = setup(stack());
    reader.comp.selection.toggleMode();
    reader.fixture.detectChanges();
    expect(reader.el.querySelector('app-series-selection-actions')).toBeNull();
  });

  it('labels counts as stories', () => {
    expect(storiesLabel(1)).toBe('1 story');
    expect(storiesLabel(2)).toBe('2 stories');
  });
});
