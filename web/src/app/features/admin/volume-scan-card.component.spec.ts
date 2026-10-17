import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { VolumeScanCardComponent } from './volume-scan-card.component';
import { ApiService } from '../../core/api/api.service';
import { VolumeScanStatusDto } from '../../core/api/api-types';

/** Volume contents card (1.44.0): the switch, its configuration pin and the counts. */
describe('VolumeScanCardComponent', () => {
  const status = (o: Partial<VolumeScanStatusDto> = {}): VolumeScanStatusDto => ({
    enabled: true, configDisabled: false, volumesChecked: 12, volumesWithChapters: 3, chaptersInside: 24, foldersWaiting: 2, running: false,
    ...o,
  });

  function setup(initial: VolumeScanStatusDto) {
    const api = {
      getVolumeScanStatus: vi.fn().mockReturnValue(of(initial)),
      setVolumeScanEnabled: vi.fn().mockImplementation((enabled: boolean) => of(status({ ...initial, enabled }))),
    };
    TestBed.configureTestingModule({
      imports: [VolumeScanCardComponent],
      providers: [provideNoopAnimations(), { provide: ApiService, useValue: api }],
    });
    const fixture = TestBed.createComponent(VolumeScanCardComponent);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, api };
  }

  it('shows the switch on and the counts', () => {
    const { el } = setup(status());
    expect(el.textContent).toContain('Find the chapters inside volume files');
    expect(el.textContent).toContain('Nothing leaves this server');
    expect(el.querySelector('[data-testid="volume-scan-counts"]')!.textContent)
      .toBe('12 volume files checked - chapters found inside 3 of them (24 chapter files). 2 folders waiting.');
  });

  it('turns the scan off with one PUT', () => {
    const { fixture, api } = setup(status());
    fixture.componentInstance.setEnabled(false);
    fixture.detectChanges();
    expect(api.setVolumeScanEnabled).toHaveBeenCalledWith(false);
    expect(fixture.componentInstance.status()!.enabled).toBe(false);
  });

  it('says when the server configuration switched it off, and disables the switch', () => {
    const { el } = setup(status({ configDisabled: true, volumesChecked: 0, volumesWithChapters: 0, chaptersInside: 0, foldersWaiting: null }));
    expect(el.textContent).toContain('Switched off by the server configuration.');
    expect(el.querySelector('[data-testid="volume-scan-counts"]')!.textContent).toBe('0 volume files checked.');
    expect(el.querySelector('mat-slide-toggle button')!.hasAttribute('disabled')).toBe(true);
    // 1.44.0 (UX review): the switch reads OFF when the configuration turned the scan off (it showed on, greyed).
    expect(el.querySelector('mat-slide-toggle button')!.getAttribute('aria-checked')).toBe('false');
  });

  it('1.44.0: large counts carry thousands separators', () => {
    const { el } = setup(status({ volumesChecked: 123456, volumesWithChapters: 1200, chaptersInside: 1234567, foldersWaiting: 1500 }));
    expect(el.querySelector('[data-testid="volume-scan-counts"]')!.textContent)
      .toBe('123,456 volume files checked - chapters found inside 1,200 of them (1,234,567 chapter files). 1,500 folders waiting.');
  });
});
