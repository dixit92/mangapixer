import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { VolumeStackViewComponent } from './volume-stack-view.component';
import { ApiService } from '../../core/api/api.service';
import { CatalogNodeDto, VolumeSlotDto, VolumeStackDto } from '../../core/api/api-types';

/** The stack view (1.29.0): header, counts, source line, ordered slots with placeholders, previous / next volume. */
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

  function setup(result: VolumeStackDto | null) {
    const apiSpy = {
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
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => ({ libraryId: 'lib1', nodeId: 'f1', key: '5' } as Record<string, string>)[k] ?? null }) } },
      ],
    });
    const fixture = TestBed.createComponent(VolumeStackViewComponent);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, apiSpy };
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
});
