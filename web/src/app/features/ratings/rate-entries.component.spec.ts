import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Subject } from 'rxjs';

import { CatalogNodeDto } from '../../core/api/api-types';
import { FolderRateButtonComponent } from './folder-rate-button.component';
import { RateSelectionActionComponent } from './rate-selection-action.component';
import { RatingSheetService } from './rating-sheet.service';
import { RatingChange, RatingsStateService } from './ratings-state.service';

/** 1.53.0 lane S (5b, audit N5 / V8): the browse bar's "Rate" and the selection bar's "Rate..." - one `stars` icon, the Rate sheet. */
describe('Rate entries', () => {
  let openNode: ReturnType<typeof vi.fn>;
  let changes: Subject<RatingChange>;

  beforeEach(() => {
    openNode = vi.fn(() => Promise.resolve());
    changes = new Subject<RatingChange>();
    TestBed.configureTestingModule({
      providers: [
        provideNoopAnimations(),
        { provide: RatingSheetService, useValue: { openNode } },
        { provide: RatingsStateService, useValue: { changed$: changes.asObservable() } },
      ],
    });
  });

  it('the browse-bar "Rate" opens the sheet for the folder and shows its rating once rated', () => {
    const fixture = TestBed.createComponent(FolderRateButtonComponent);
    fixture.componentRef.setInput('nodeId', 'f1');
    fixture.componentRef.setInput('rating', null);
    fixture.detectChanges();
    const button = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="folder-rate-button"]')!;
    expect(button.querySelector('mat-icon')!.textContent!.trim()).toBe('stars');
    expect(button.textContent).toContain('Rate');
    expect(button.getAttribute('aria-label')).toBe('Rate this folder');
    button.click();
    expect(openNode).toHaveBeenCalledWith('f1');

    changes.next({ nodeId: 'f1', score: 9 });
    fixture.detectChanges();
    expect(button.querySelector('.lbl')!.textContent!.trim()).toBe('4.5');
    expect(button.getAttribute('aria-label')).toBe('Your rating 4.5 of 5 - rate this folder');
  });

  it('the selection bar "Rate..." is enabled for exactly one folder or archive', () => {
    const fixture = TestBed.createComponent(RateSelectionActionComponent);
    const nodes = [
      { id: 'a', kind: 'Folder', displayName: 'A' },
      { id: 'b', kind: 'Archive', displayName: 'B' },
      { id: 'v', kind: 'VolumeStack', displayName: 'Vol. 1' },
    ] as CatalogNodeDto[];
    fixture.componentRef.setInput('nodes', nodes);
    fixture.componentRef.setInput('selected', new Set<string>());
    fixture.detectChanges();
    const button = () => (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="selection-rate"]')!;
    expect(button().querySelector('mat-icon')!.textContent!.trim()).toBe('stars');
    expect(button().disabled).toBe(true);
    fixture.componentRef.setInput('selected', new Set(['a', 'b']));
    fixture.detectChanges();
    expect(button().disabled).toBe(true);
    fixture.componentRef.setInput('selected', new Set(['v']));
    fixture.detectChanges();
    expect(button().disabled).toBe(true);
    fixture.componentRef.setInput('selected', new Set(['b']));
    fixture.detectChanges();
    expect(button().disabled).toBe(false);
    button().click();
    expect(openNode).toHaveBeenCalledWith('b');
  });
});
