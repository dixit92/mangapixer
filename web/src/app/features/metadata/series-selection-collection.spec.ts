import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { CatalogNodeDto } from '../../core/api/api-types';
import { IdentifyDialogService } from './identify-dialog/identify-dialog.service';
import { MetadataApiService } from './metadata-api.service';
import { MetadataStateService } from './metadata-state.service';
import { SeriesSelectionActionsComponent } from './series-selection-actions.component';

/** Browse selection "Series" menu (1.34.0): "Collection about..." for exactly one folder, "Clear collection" for folders. */
describe('SeriesSelectionActionsComponent - Collection about', () => {
  const folder = (id: string) => ({ id, kind: 'Folder', parentId: '', libraryId: 'lib1', displayName: id, availability: 'Available' }) as CatalogNodeDto;
  const archive = (id: string) => ({ ...folder(id), kind: 'Archive' }) as CatalogNodeDto;

  function create(selected: string[]) {
    @Component({
      standalone: true,
      imports: [SeriesSelectionActionsComponent],
      template: `<app-series-selection-actions [nodes]="nodes" [selected]="selected" />`,
    })
    class Host {
      nodes = [folder('f1'), folder('f2'), archive('a1')];
      selected: ReadonlySet<string> = new Set(selected);
    }
    const dialog = { open: vi.fn(() => Promise.resolve(true)) };
    const api = {
      getSettings: () => of({ showSeriesInfo: true, libraries: [] }),
      getFolderContent: (id: string) => of({ nodeId: id, effective: 'Auto' }),
      clearCollection: vi.fn((id: string) => of({ nodeId: id })),
    };
    const state = { refresh: vi.fn() };
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [
        provideNoopAnimations(),
        { provide: MetadataApiService, useValue: api },
        { provide: IdentifyDialogService, useValue: dialog },
        { provide: MetadataStateService, useValue: state },
      ],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="series-selection-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const item = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement;
    return { dialog, api, state, item };
  }

  it('opens the identify dialog in its collection mode for the one selected folder', () => {
    const { dialog, item } = create(['f2']);
    expect(item('bulk-collection').disabled).toBe(false);
    item('bulk-collection').click();
    expect(dialog.open).toHaveBeenCalledWith('f2', 'collection');
  });

  it('is disabled for an archive or a multi-selection', () => {
    expect(create(['a1']).item('bulk-collection').disabled).toBe(true);
    TestBed.resetTestingModule();
    expect(create(['f1', 'f2']).item('bulk-collection').disabled).toBe(true);
  });

  it('clears collections on every selected folder (archives are left out)', () => {
    const { api, state, item } = create(['f1', 'f2', 'a1']);
    item('bulk-clear-collection').click();
    expect(api.clearCollection.mock.calls.map((c) => c[0])).toEqual(['f1', 'f2']);
    expect(state.refresh).toHaveBeenCalledWith('f1');
  });
});
