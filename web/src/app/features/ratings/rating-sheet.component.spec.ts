import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MAT_BOTTOM_SHEET_DATA, MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { of } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { CatalogNodeDto } from '../../core/api/api-types';
import { RatingSheetComponent } from './rating-sheet.component';
import { RatingsStateService } from './ratings-state.service';

/** 1.52.0 lane R: the reader's Rate sheet - this archive and its folder, each editable. */
describe('RatingSheetComponent', () => {
  function create(nodes: Record<string, Partial<CatalogNodeDto>>, data: { archiveId?: string; nodeId?: string } = { archiveId: 'a1' }) {
    const getNode = vi.fn((id: string) => of(nodes[id] as CatalogNodeDto));
    const dismiss = vi.fn();
    TestBed.configureTestingModule({
      imports: [RatingSheetComponent],
      providers: [
        provideNoopAnimations(),
        { provide: ApiService, useValue: { getNode } },
        { provide: RatingsStateService, useValue: { changed$: of(), setRating: vi.fn(() => of(undefined)) } },
        { provide: MAT_BOTTOM_SHEET_DATA, useValue: data },
        { provide: MatBottomSheetRef, useValue: { dismiss } },
      ],
    });
    const fixture = TestBed.createComponent(RatingSheetComponent);
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, getNode, dismiss };
  }

  it('lists the archive and its folder, each with its own rating', () => {
    const { el, getNode } = create({
      a1: { id: 'a1', parentId: 'f1', displayName: 'Akira 01.cbz', myRating: 9 },
      f1: { id: 'f1', parentId: '', displayName: 'Akira', myRating: null },
    });
    expect(getNode).toHaveBeenCalledTimes(2);
    const archive = el.querySelector('[data-testid="rating-sheet-archive"]')!;
    const folder = el.querySelector('[data-testid="rating-sheet-folder"]')!;
    expect(archive.textContent).toContain('Akira 01.cbz');
    expect(archive.querySelector('[role="slider"]')!.getAttribute('aria-valuetext')).toBe('4.5 of 5 stars');
    expect(folder.textContent).toContain('Akira');
    expect(folder.querySelector('[role="slider"]')!.getAttribute('aria-valuetext')).toBe('Not rated');
  });

  it('an archive at a library root has no folder row', () => {
    const { el, getNode } = create({ a1: { id: 'a1', parentId: '', displayName: 'One-shot.cbz' } });
    expect(getNode).toHaveBeenCalledTimes(1);
    expect(el.querySelector('[data-testid="rating-sheet-folder"]')).toBeNull();
    expect(el.querySelectorAll('[role="slider"]').length).toBe(1);
  });

  it('closes', () => {
    const { el, dismiss } = create({ a1: { id: 'a1', parentId: '', displayName: 'x' } });
    el.querySelector<HTMLButtonElement>('button[aria-label="Close"]')!.click();
    expect(dismiss).toHaveBeenCalled();
  });

  it('1.53.0 (5b): with one node it rates that node alone - "This folder", no folder row, the server line from the same read', () => {
    const { el, getNode } = create({
      f1: { id: 'f1', parentId: 'p1', kind: 'Folder', displayName: 'Plain Folder', myRating: 6, serverRating: { average: 7, count: 2 } },
    }, { nodeId: 'f1' });
    expect(getNode).toHaveBeenCalledTimes(1);
    expect(getNode).toHaveBeenCalledWith('f1');
    const row = el.querySelector('[data-testid="rating-sheet-archive"]')!;
    expect(row.textContent).toContain('This folder');
    expect(row.querySelector('[role="slider"]')!.getAttribute('aria-valuetext')).toBe('3 of 5 stars');
    expect(row.querySelector('[data-testid="server-rating"]')!.getAttribute('aria-label')).toBe('Server average 3.5 of 5, 2 ratings');
    expect(el.querySelector('[data-testid="rating-sheet-folder"]')).toBeNull();
  });

  it('one archive alone is "This archive"', () => {
    const { el } = create({ a2: { id: 'a2', parentId: 'f1', kind: 'Archive', displayName: 'Vol 1.cbz' } }, { nodeId: 'a2' });
    expect(el.querySelector('[data-testid="rating-sheet-archive"]')!.textContent).toContain('This archive');
    expect(el.querySelectorAll('[role="slider"]').length).toBe(1);
  });
});
