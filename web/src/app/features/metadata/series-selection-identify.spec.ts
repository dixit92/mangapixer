import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, throwError } from 'rxjs';

import { CatalogNodeDto } from '../../core/api/api-types';
import { IdentifyDialogService } from './identify-dialog/identify-dialog.service';
import { MetadataApiService } from './metadata-api.service';
import { SeriesSelectionActionsComponent } from './series-selection-actions.component';

/**
 * Browse selection "Series" menu (1.24.0, lane B2): Identify for one selected node; 1.34.0: several selected nodes are identified one at a
 * time (the dialog's stepping mode) and "Re-run matching" queues the selection with plain-words reasons for what was refused.
 */
describe('SeriesSelectionActionsComponent - Identify', () => {
  const node = (id: string) => ({ id, kind: 'Folder', parentId: '', libraryId: 'lib1', displayName: id, availability: 'Available' }) as CatalogNodeDto;

  function create(selected: string[], bulk: (action: string, ids: string[]) => unknown = () => of({ action: 'RerunMatching', succeeded: 0, failed: 0, results: [] })) {
    @Component({
      standalone: true,
      imports: [SeriesSelectionActionsComponent],
      template: `<app-series-selection-actions [nodes]="nodes" [selected]="selected" />`,
    })
    class Host {
      nodes = [node('f1'), node('f2')];
      selected: ReadonlySet<string> = new Set(selected);
    }
    const dialog = { open: vi.fn(() => Promise.resolve(true)), openMany: vi.fn(() => Promise.resolve(true)) };
    const snackBar = { open: vi.fn() };
    const reviewBulk = vi.fn(bulk);
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [provideNoopAnimations(), { provide: MetadataApiService, useValue: { getSettings: () => of({ showSeriesInfo: true, libraries: [] }), getFolderContent: (id: string) => of({ nodeId: id, effective: 'Auto' }), reviewBulk } },
        { provide: IdentifyDialogService, useValue: dialog }, { provide: MatSnackBar, useValue: snackBar }],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="series-selection-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    return {
      dialog, snackBar, reviewBulk, fixture,
      item: () => document.querySelector('[data-testid="bulk-identify"]') as HTMLButtonElement,
      rerunItem: () => document.querySelector('[data-testid="bulk-rerun"]') as HTMLButtonElement,
    };
  }

  it('opens the dialog for the single selected node', () => {
    const { dialog, item } = create(['f2']);
    expect(item().disabled).toBe(false);
    item().click();
    expect(dialog.open).toHaveBeenCalledWith('f2');
  });

  it('steps through a multi-selection one at a time', () => {
    const { dialog, item } = create(['f1', 'f2']);
    expect(item().disabled).toBe(false);
    expect(item().textContent).toContain('one by one');
    item().click();
    expect(dialog.openMany).toHaveBeenCalledWith(['f1', 'f2']);
    expect(dialog.open).not.toHaveBeenCalled();
  });

  it('queues the selection for matching again and says it', () => {
    const { reviewBulk, snackBar, rerunItem } = create(['f1', 'f2'], () => of({
      action: 'RerunMatching', succeeded: 2, failed: 0, results: [{ nodeId: 'f1', code: 'ok' }, { nodeId: 'f2', code: 'ok' }],
    }));
    expect(rerunItem().disabled).toBe(false);
    rerunItem().click();
    expect(reviewBulk).toHaveBeenCalledWith('RerunMatching', ['f1', 'f2']);
    expect(snackBar.open).toHaveBeenCalledWith('2 items queued to match again', 'Close', { duration: 3500 });
  });

  it('works for one node and explains a refusal inside a folder that is linked, Don\'t match or waiting', () => {
    const { reviewBulk, snackBar, rerunItem } = create(['f2'], () => of({
      action: 'RerunMatching', succeeded: 0, failed: 1, results: [{ nodeId: 'f2', code: 'covered_by_folder' }],
    }));
    rerunItem().click();
    expect(reviewBulk).toHaveBeenCalledWith('RerunMatching', ['f2']);
    const [message, , config] = snackBar.open.mock.calls[0] as unknown as [string, string, { duration: number }];
    expect(message).toContain('inside a folder that is linked, marked Don\'t match or waiting in review');
    expect(config.duration).toBe(10000);
  });

  it('says why a re-run failed outright', () => {
    const { snackBar, rerunItem } = create(['f1'], () => throwError(() => ({ message: 'offline' })));
    rerunItem().click();
    expect(snackBar.open).toHaveBeenCalledWith('Failed: offline', 'Close', { duration: 4000 });
  });
});
