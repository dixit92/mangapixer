import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, throwError } from 'rxjs';

import { CoverOptionsDto } from '../../core/api/api-types';
import { CoverApiService } from './cover-api.service';
import { CoverPickerDialogComponent, choiceFor } from './cover-picker-dialog.component';
import { CoverStateService } from './cover-state.service';

/**
 * The admin cover picker (1.29.0) against a mocked API - never the network: the current state line, the local options
 * (file, both halves, other items), the stored web covers grouped by volume (a not downloaded one cannot be picked), the
 * web part's reason, and "Use this cover" -> the choice, the card patch announcement, the dialog result.
 */
describe('CoverPickerDialogComponent', () => {
  const options = (overrides: Partial<CoverOptionsDto> = {}): CoverOptionsDto => ({
    nodeId: 'f1',
    current: { mode: 'Automatic', autoSource: 'Crop', reason: 'Spread', imageUrl: '/api/v1/nodes/a1/cover?v=abc' },
    local: [
      { kind: 'File', archiveId: 'a1', imageUrl: '/api/v1/items/a1/cover?v=1', label: "This file's cover" },
      { kind: 'CropLeft', archiveId: 'a1', imageUrl: '/api/v1/nodes/a1/cover-crops/left?v=1', label: 'Left half of page 1' },
      { kind: 'CropRight', archiveId: 'a1', imageUrl: '/api/v1/nodes/a1/cover-crops/right?v=1', label: 'Right half of page 1' },
      { kind: 'Archive', archiveId: 'a2', imageUrl: '/api/v1/items/a2/cover?v=1', label: 'Series v02' },
    ],
    web: [
      { volume: 1, covers: [
        { id: 'vc1', kind: 'Volume', volume: 1, variant: 0, locale: 'ja', stored: true, imageUrl: '/api/v1/volume-covers/vc1/image?v=1' },
        { id: 'vc2', kind: 'Volume', volume: 1, variant: 0, locale: 'en', stored: false, imageUrl: null },
      ] },
    ],
    webAvailable: true,
    ...overrides,
  });

  function create(o: CoverOptionsDto | 'error' = options()) {
    const ref = { close: vi.fn() };
    const snackBar = { open: vi.fn() };
    const api = {
      getOptions: vi.fn(() => (o === 'error' ? throwError(() => ({ error: 'x', message: 'Boom' })) : of(o))),
      setChoice: vi.fn(() => of({ mode: 'Archive', imageUrl: '/api/v1/nodes/f1/cover?v=new' })),
      clearChoice: vi.fn(() => of({ mode: 'Automatic', autoSource: 'Crop', imageUrl: '/api/v1/nodes/a1/cover?v=abc' })),
    };
    const coverState = { announce: vi.fn() };
    TestBed.configureTestingModule({
      imports: [CoverPickerDialogComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MAT_DIALOG_DATA, useValue: { nodeId: 'f1', displayName: 'Series' } },
        { provide: MatDialogRef, useValue: ref },
        { provide: MatSnackBar, useValue: snackBar },
        { provide: CoverApiService, useValue: api },
        { provide: CoverStateService, useValue: coverState },
      ],
    });
    const fixture = TestBed.createComponent(CoverPickerDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement | null;
    return { fixture, el, q, api, ref, coverState, snackBar };
  }

  it('shows what the automatic layer uses and every option', () => {
    const { el, q } = create();
    expect(q('cover-picker-current')!.textContent).toContain('front half of page 1');
    expect(q('cover-pick-automatic')!.getAttribute('aria-pressed')).toBe('true');
    for (const id of ['cover-pick-File', 'cover-pick-CropLeft', 'cover-pick-CropRight', 'cover-pick-archive-a2', 'cover-pick-web-vc1']) {
      expect(q(id)).not.toBeNull();
    }
    expect(q('cover-pick-web-vc2')!.disabled).toBe(true); // not downloaded: no request from the picker
    expect(el.textContent).toContain('Vol. 1');
    expect(el.textContent).not.toContain('MangaDex'); // the credit lives in Metadata Manager only
  });

  it('sets another item as the cover, announces the new card URL and closes with the state', () => {
    const { fixture, q, api, ref, coverState } = create();
    q('cover-pick-archive-a2')!.click();
    fixture.detectChanges();
    expect(q('cover-pick-archive-a2')!.getAttribute('aria-pressed')).toBe('true');
    q('cover-picker-apply')!.click();
    expect(api.setChoice).toHaveBeenCalledWith('f1', { mode: 'Archive', archiveId: 'a2' });
    expect(coverState.announce).toHaveBeenCalledWith({ nodeId: 'f1', coverUrl: '/api/v1/nodes/f1/cover?v=new', coverSource: 'Chosen' });
    expect(ref.close).toHaveBeenCalledWith(expect.objectContaining({ mode: 'Archive' }));
  });

  it('Automatic clears the choice', () => {
    const { fixture, q, api } = create(options({ current: { mode: 'FilePinned', imageUrl: '/api/v1/items/a1/cover?v=1' } }));
    expect(q('cover-pick-File')!.getAttribute('aria-pressed')).toBe('true');
    q('cover-pick-automatic')!.click();
    fixture.detectChanges();
    q('cover-picker-apply')!.click();
    expect(api.clearChoice).toHaveBeenCalledWith('f1');
    expect(api.setChoice).not.toHaveBeenCalled();
  });

  it('explains a missing web part', () => {
    const { q } = create(options({ web: [], webAvailable: false, webUnavailableReason: 'dont_match' }));
    expect(q('cover-picker-web-unavailable')!.textContent).toContain("Don't match");
  });

  it('shows a load error and cannot apply', () => {
    const { el, q } = create('error');
    expect(el.textContent).toContain('Boom');
    expect(q('cover-picker-apply')!.disabled).toBe(true);
  });

  it('maps every pick to its request', () => {
    expect(choiceFor({ kind: 'automatic' })).toBeNull();
    expect(choiceFor({ kind: 'file' })).toEqual({ mode: 'FilePinned' });
    expect(choiceFor({ kind: 'crop', side: 'Left' })).toEqual({ mode: 'Crop', cropSide: 'Left' });
    expect(choiceFor({ kind: 'web', coverId: 'vc1' })).toEqual({ mode: 'VolumeCover', volumeCoverId: 'vc1' });
  });
});
