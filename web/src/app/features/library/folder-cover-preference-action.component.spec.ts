import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { CatalogNodeDto } from '../../core/api/api-types';
import { FolderCoverPreferenceActionComponent } from './folder-cover-preference-action.component';
import { FolderCoverPreferenceDialogComponent } from './folder-cover-preference-dialog.component';

@Component({
  standalone: true,
  imports: [FolderCoverPreferenceActionComponent],
  template: `<app-folder-cover-preference-action [nodes]="nodes()" [selected]="selected()" (saved)="saves = saves + 1" />`,
})
class HostComponent {
  saves = 0;
  readonly nodes = signal<CatalogNodeDto[]>([
    { id: 'f1', kind: 'Folder', displayName: 'Shelf' } as CatalogNodeDto,
    { id: 'f2', kind: 'Folder', displayName: 'Other' } as CatalogNodeDto,
    { id: 'a1', kind: 'Archive', displayName: 'Series v01' } as CatalogNodeDto,
  ]);
  readonly selected = signal<ReadonlySet<string>>(new Set());
}

/** Browse selection bar "Folder covers..." (1.32.0): enabled for exactly one selected FOLDER, opens its dialog, reports a save. */
describe('FolderCoverPreferenceActionComponent', () => {
  function create(closeWith = true) {
    const dialog = { open: vi.fn(() => ({ afterClosed: () => of(closeWith) })) };
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideNoopAnimations(), { provide: MatDialog, useValue: dialog }],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const button = () => fixture.nativeElement.querySelector('[data-testid="folder-cover-preference-action"]') as HTMLButtonElement;
    const select = (...ids: string[]) => { fixture.componentInstance.selected.set(new Set(ids)); fixture.detectChanges(); };
    return { fixture, button, select, dialog };
  }

  it('is disabled for nothing, several items, or an archive', () => {
    const { button, select } = create();
    expect(button().disabled).toBe(true);
    select('f1', 'f2');
    expect(button().disabled).toBe(true);
    select('a1');
    expect(button().disabled).toBe(true);
  });

  it("opens the dialog for the one selected folder and reports a save", () => {
    const { fixture, button, select, dialog } = create(true);
    select('f1');
    expect(button().disabled).toBe(false);
    button().click();
    expect(dialog.open).toHaveBeenCalledWith(FolderCoverPreferenceDialogComponent, { data: { nodeId: 'f1', name: 'Shelf' }, width: '460px' });
    expect(fixture.componentInstance.saves).toBe(1);
  });

  it('reports nothing when the dialog was cancelled', () => {
    const { fixture, button, select } = create(false);
    select('f1');
    button().click();
    expect(fixture.componentInstance.saves).toBe(0);
  });
});
