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
  function create(nodes: Record<string, Partial<CatalogNodeDto>>) {
    const getNode = vi.fn((id: string) => of(nodes[id] as CatalogNodeDto));
    const dismiss = vi.fn();
    TestBed.configureTestingModule({
      imports: [RatingSheetComponent],
      providers: [
        provideNoopAnimations(),
        { provide: ApiService, useValue: { getNode } },
        { provide: RatingsStateService, useValue: { changed$: of(), setRating: vi.fn(() => of(undefined)) } },
        { provide: MAT_BOTTOM_SHEET_DATA, useValue: { archiveId: 'a1' } },
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
});
