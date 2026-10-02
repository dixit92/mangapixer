import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { FolderCoverPreferenceDto } from '../../core/api/api-types';
import { FolderCoverPreferenceDialogComponent } from './folder-cover-preference-dialog.component';

/**
 * "Folder covers..." (1.32.0) against a mocked API - never the network: the Inherit line names where the inherited value comes from
 * (a folder above, or the library's setting), the folder's own value is preselected, Save writes it (Inherit removes it), and a
 * failure keeps the dialog open with a message.
 */
describe('FolderCoverPreferenceDialogComponent', () => {
  const state = (o: Partial<FolderCoverPreferenceDto> = {}): FolderCoverPreferenceDto => ({
    nodeId: 'f1', effective: 'Web', inherited: 'Web', ...o,
  });

  function create(initial: FolderCoverPreferenceDto | 'error' = state()) {
    const ref = { close: vi.fn() };
    const api = {
      getFolderCoverPreference: vi.fn(() => (initial === 'error' ? throwError(() => new Error('x')) : of(initial))),
      setFolderCoverPreference: vi.fn(() => of(state({ preference: 'File', effective: 'File' }))),
      clearFolderCoverPreference: vi.fn(() => of(state())),
    };
    TestBed.configureTestingModule({
      imports: [FolderCoverPreferenceDialogComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MAT_DIALOG_DATA, useValue: { nodeId: 'f1', name: 'Shelf' } },
        { provide: MatDialogRef, useValue: ref },
        { provide: ApiService, useValue: api },
      ],
    });
    const fixture = TestBed.createComponent(FolderCoverPreferenceDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
    const radio = (id: string) => q(id)!.querySelector('input') as HTMLInputElement;
    return { fixture, el, q, radio, ref, api };
  }

  it('names the folder above that the inherited value comes from', () => {
    const { q } = create(state({ effective: 'File', inherited: 'File', inheritedSourceNodeId: 'f0', inheritedSourceName: 'Collection' }));
    expect(q('folder-cover-inherit')!.textContent).toContain('Inherit (File covers from Collection)');
  });

  it('says the library decides when no folder above has a value', () => {
    const { q } = create(state({ inherited: 'File', effective: 'File' }));
    expect(q('folder-cover-inherit')!.textContent).toContain("Inherit (File covers - the library's setting)");
  });

  it("preselects the folder's own value and shows the inherited one next to Inherit", () => {
    const { q, radio } = create(state({ preference: 'Web', effective: 'Web', inherited: 'File', inheritedSourceName: 'Collection' }));
    expect(radio('folder-cover-web').checked).toBe(true);
    expect(radio('folder-cover-inherit').checked).toBe(false);
    expect(q('folder-cover-inherit')!.textContent).toContain('File covers from Collection');
  });

  it('saves File covers and closes with true', () => {
    const { fixture, q, radio, api, ref } = create();
    expect(radio('folder-cover-inherit').checked).toBe(true);
    radio('folder-cover-file').click();
    fixture.detectChanges();
    (q('folder-cover-save') as HTMLButtonElement).click();
    expect(api.setFolderCoverPreference).toHaveBeenCalledWith('f1', { preference: 'File' });
    expect(api.clearFolderCoverPreference).not.toHaveBeenCalled();
    expect(ref.close).toHaveBeenCalledWith(true);
  });

  it('removes the folder\'s own value when Inherit is chosen', () => {
    const { fixture, q, radio, api, ref } = create(state({ preference: 'File', effective: 'File' }));
    radio('folder-cover-inherit').click();
    fixture.detectChanges();
    (q('folder-cover-save') as HTMLButtonElement).click();
    expect(api.clearFolderCoverPreference).toHaveBeenCalledWith('f1');
    expect(api.setFolderCoverPreference).not.toHaveBeenCalled();
    expect(ref.close).toHaveBeenCalledWith(true);
  });

  it('keeps Save disabled when the current value could not be read, with a message', () => {
    const { el, q } = create('error');
    expect((q('folder-cover-save') as HTMLButtonElement).disabled).toBe(true);
    expect(el.textContent).toContain('could not be read');
  });

  it('stays open with a message when saving fails', () => {
    const { fixture, el, q, radio, api, ref } = create();
    api.setFolderCoverPreference.mockReturnValue(throwError(() => new Error('x')));
    radio('folder-cover-file').click();
    fixture.detectChanges();
    (q('folder-cover-save') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(ref.close).not.toHaveBeenCalled();
    expect(el.textContent).toContain('could not be saved');
  });
});
