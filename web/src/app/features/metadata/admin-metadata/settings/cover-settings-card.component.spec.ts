import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { MetadataSettingsDto } from '../../../../core/api/api-types';
import { CoverApiService } from '../../../../shared/cover-picker/cover-api.service';
import { MetadataApiService } from '../../metadata-api.service';
import { CoverSettingsCardComponent } from './cover-settings-card.component';

/**
 * Metadata Manager "Covers" card (1.29.0): the crop switch, "Show saved web covers" per library (separate from "Show
 * series information"), and "Delete stored volume covers" behind a confirm. Mocked APIs - never the network.
 */
describe('CoverSettingsCardComponent', () => {
  const settings = (overrides: Partial<MetadataSettingsDto> = {}): MetadataSettingsDto => ({
    showSeriesInfo: true,
    fetchEnabled: false,
    networkDisabledByConfig: false,
    currentConsentVersion: 3,
    spreadCropEnabled: true,
    libraries: [
      { libraryId: 'L1', name: 'Manga', fetchEnabled: true, showSeriesInfo: true, linkCount: 2, showWebCovers: true },
      { libraryId: 'L2', name: 'Comics', fetchEnabled: false, showSeriesInfo: false, linkCount: 0, showWebCovers: false },
    ],
    ...overrides,
  } as MetadataSettingsDto);

  function create() {
    const api = {
      updateSettings: vi.fn(() => of(settings({ spreadCropEnabled: false }))),
      updateLibrary: vi.fn(() => of(settings())),
    };
    const covers = { deleteStoredVolumeCovers: vi.fn(() => of({ coversDeleted: 3, decisionsReset: 2, choicesReset: 0 })) };
    TestBed.configureTestingModule({
      imports: [CoverSettingsCardComponent],
      providers: [provideNoopAnimations(), { provide: MetadataApiService, useValue: api }, { provide: CoverApiService, useValue: covers }],
    });
    const fixture = TestBed.createComponent(CoverSettingsCardComponent);
    fixture.componentRef.setInput('initial', settings());
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const toggle = (id: string) => el.querySelector(`[data-testid="${id}"] button`) as HTMLButtonElement;
    const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement | null;
    return { fixture, el, toggle, q, api, covers };
  }

  it('shows the crop switch and one "Show saved web covers" switch per library', () => {
    const { toggle } = create();
    expect(toggle('md-covers-crop').getAttribute('aria-checked')).toBe('true');
    expect(toggle('md-covers-lib-L1').getAttribute('aria-checked')).toBe('true');
    expect(toggle('md-covers-lib-L2').getAttribute('aria-checked')).toBe('false');
  });

  it('saves the crop switch and a library switch', () => {
    const { fixture, toggle, api } = create();
    toggle('md-covers-crop').click();
    fixture.detectChanges();
    expect(api.updateSettings).toHaveBeenCalledWith({ spreadCropEnabled: false });
    toggle('md-covers-lib-L1').click();
    expect(api.updateLibrary).toHaveBeenCalledWith('L1', { showWebCovers: false });
  });

  it('deletes stored volume covers only after the confirm', () => {
    const { fixture, q, el, covers } = create();
    q('md-covers-delete')!.click();
    fixture.detectChanges();
    expect(covers.deleteStoredVolumeCovers).not.toHaveBeenCalled();
    q('md-covers-delete-confirm')!.click();
    fixture.detectChanges();
    expect(covers.deleteStoredVolumeCovers).toHaveBeenCalled();
    expect(el.textContent).toContain('Deleted 3 stored covers.');
  });
});
