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
 * web part's reason, and "Use this cover" -> the choice, the card patch announcement, the dialog result. 1.36.0: a folder
 * that is not a series lists the covers of the series inside it, per series, above the other items.
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
    expect(el.textContent).toContain('Volume 1');
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

  // 1.36.0: a folder that is not a series - the covers of the series inside it, per series, ABOVE "Another item's cover".
  const seriesInside = (): Partial<CoverOptionsDto> => ({
    web: [],
    webAvailable: true,
    webSeries: [
      { nodeId: 's1', displayName: 'Main Story', seriesTitle: 'Main Story', groups: [
        { volume: 1, covers: [{ id: 'vm1', kind: 'Volume', volume: 1, variant: 0, locale: 'en', stored: true, imageUrl: '/api/v1/volume-covers/vm1/image?v=1' }] },
      ] },
      { nodeId: 's2', displayName: 'Side Story', seriesTitle: 'Side Story Gaiden (record)', groups: [
        { volume: null, covers: [{ id: 'vs1', kind: 'Main', variant: 0, locale: 'ja', stored: true, imageUrl: '/api/v1/volume-covers/vs1/image?v=1' }] },
      ] },
    ],
    webSeriesMore: 3,
  });

  it('a folder with series inside: their covers per series, first, above the other items', () => {
    const { el, q } = create(options(seriesInside()));
    const sections = Array.from(el.querySelectorAll('h3.section')).map((h) => h.textContent?.trim());
    expect(sections).toEqual(['Covers from the web', "Another item's cover"]);
    const series = Array.from(el.querySelectorAll('[data-testid^="cover-picker-series-"]'));
    expect(series.map((s) => s.querySelector('.series-name')!.textContent!.trim())).toEqual(['Main Story', 'Side Story']);
    // The record title shows only when it says more than the folder name.
    expect(series[0].querySelector('.series-title')).toBeNull();
    expect(series[1].querySelector('.series-title')!.textContent).toContain('Side Story Gaiden (record)');
    expect(series[1].textContent).toContain('Series cover · JA');
    expect(q('cover-picker-web-series-more')!.textContent).toContain('3 more series inside have');
    expect(q('cover-picker-web-unavailable')).toBeNull();
    // The series' covers come before the first archive tile in the document.
    const web = q('cover-pick-web-vm1')!;
    const archive = q('cover-pick-archive-a2')!;
    expect(web.compareDocumentPosition(archive) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('a series cover from inside is chosen like any web cover', () => {
    const { fixture, q, api } = create(options(seriesInside()));
    q('cover-pick-web-vs1')!.click();
    fixture.detectChanges();
    expect(q('cover-pick-web-vs1')!.getAttribute('aria-pressed')).toBe('true');
    q('cover-picker-apply')!.click();
    expect(api.setChoice).toHaveBeenCalledWith('f1', { mode: 'VolumeCover', volumeCoverId: 'vs1' });
  });

  it('a series keeps its order: its own web covers after the other items', () => {
    const { el, q } = create();
    const sections = Array.from(el.querySelectorAll('h3.section')).map((h) => h.textContent?.trim());
    expect(sections).toEqual(["Another item's cover", 'Covers from the web']);
    expect(q('cover-picker-web-series-hint')).toBeNull();
  });

  it('explains a folder whose series inside have no stored covers yet', () => {
    const { q } = create(options({ web: [], webAvailable: false, webUnavailableReason: 'no_series_covers', webSeries: [] }));
    expect(q('cover-picker-web-unavailable')!.textContent).toContain('series inside this folder');
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
