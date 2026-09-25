import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { CatalogNodeDto } from '../../core/api/api-types';
import { IdentifyDialogService } from './identify-dialog/identify-dialog.service';
import { MetadataApiService } from './metadata-api.service';
import { SeriesSelectionActionsComponent } from './series-selection-actions.component';

/** Browse selection "Series" menu (1.24.0, lane B2): Identify only for exactly one selected node. */
describe('SeriesSelectionActionsComponent - Identify', () => {
  const node = (id: string) => ({ id, kind: 'Folder', parentId: '', libraryId: 'lib1', displayName: id, availability: 'Available' }) as CatalogNodeDto;

  function create(selected: string[]) {
    @Component({
      standalone: true,
      imports: [SeriesSelectionActionsComponent],
      template: `<app-series-selection-actions [nodes]="nodes" [selected]="selected" />`,
    })
    class Host {
      nodes = [node('f1'), node('f2')];
      selected: ReadonlySet<string> = new Set(selected);
    }
    const dialog = { open: vi.fn(() => Promise.resolve(true)) };
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [provideNoopAnimations(), { provide: MetadataApiService, useValue: {} }, { provide: IdentifyDialogService, useValue: dialog }],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="series-selection-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    return { dialog, item: () => document.querySelector('[data-testid="bulk-identify"]') as HTMLButtonElement };
  }

  it('opens the dialog for the single selected node', () => {
    const { dialog, item } = create(['f2']);
    expect(item().disabled).toBe(false);
    item().click();
    expect(dialog.open).toHaveBeenCalledWith('f2');
  });

  it('is disabled for a multi-selection', () => {
    const { dialog, item } = create(['f1', 'f2']);
    expect(item().disabled).toBe(true);
    expect(dialog.open).not.toHaveBeenCalled();
  });
});
