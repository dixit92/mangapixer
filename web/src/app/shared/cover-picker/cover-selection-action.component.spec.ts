import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { CatalogNodeDto } from '../../core/api/api-types';
import { CoverPickerDialogService } from './cover-picker-dialog.service';
import { CoverSelectionActionComponent } from './cover-selection-action.component';

@Component({
  standalone: true,
  imports: [CoverSelectionActionComponent],
  template: `<app-cover-selection-action [nodes]="nodes()" [selected]="selected()" />`,
})
class HostComponent {
  readonly nodes = signal<CatalogNodeDto[]>([
    { id: 'f1', kind: 'Folder', displayName: 'Series' } as CatalogNodeDto,
    { id: 'a1', kind: 'Archive', displayName: 'Series v01' } as CatalogNodeDto,
  ]);
  readonly selected = signal<ReadonlySet<string>>(new Set());
}

/** Browse selection bar "Cover..." (1.29.0): enabled for exactly one selected item, opens the picker for it. */
describe('CoverSelectionActionComponent', () => {
  function create() {
    const picker = { open: vi.fn(() => Promise.resolve(undefined)) };
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideNoopAnimations(), { provide: CoverPickerDialogService, useValue: picker }],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const button = () => fixture.nativeElement.querySelector('[data-testid="selection-cover"]') as HTMLButtonElement;
    return { fixture, button, picker };
  }

  it('is disabled unless exactly one item is selected', () => {
    const { fixture, button } = create();
    expect(button().disabled).toBe(true);
    fixture.componentInstance.selected.set(new Set(['f1', 'a1']));
    fixture.detectChanges();
    expect(button().disabled).toBe(true);
  });

  it('opens the picker for the one selected item', () => {
    const { fixture, button, picker } = create();
    fixture.componentInstance.selected.set(new Set(['a1']));
    fixture.detectChanges();
    expect(button().disabled).toBe(false);
    button().click();
    expect(picker.open).toHaveBeenCalledWith('a1', 'Series v01');
  });
});
