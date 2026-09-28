import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { MetadataSettingsDto } from '../../core/api/api-types';
import { settings } from './admin-metadata/metadata-admin.testing';
import { ConsentRenewalBannerComponent } from './consent-renewal-banner.component';
import { MetadataReviewStateService } from './metadata-review-state.service';

/** The ONE consent-renewal banner (1.28.0): shown while either consent needs renewing, for both at once, else hidden. */
describe('ConsentRenewalBannerComponent', () => {
  function create(s: MetadataSettingsDto | null, inPage = false) {
    const state = { settings: signal(s), refreshSettings: vi.fn() };
    TestBed.configureTestingModule({
      imports: [ConsentRenewalBannerComponent],
      providers: [provideRouter([]), { provide: MetadataReviewStateService, useValue: state }],
    });
    const fixture = TestBed.createComponent(ConsentRenewalBannerComponent);
    fixture.componentRef.setInput('inPage', inPage);
    fixture.detectChanges();
    return { fixture, state, el: fixture.nativeElement as HTMLElement };
  }

  it('is hidden without a renewal and loads the settings when none are known', () => {
    expect(create(settings()).el.querySelector('[data-testid="consent-renewal-banner"]')).toBeNull();
    TestBed.resetTestingModule();
    const empty = create(null);
    expect(empty.state.refreshSettings).toHaveBeenCalled();
    expect(empty.el.querySelector('[data-testid="consent-renewal-banner"]')).toBeNull();
  });

  it('covers the fetch consent, the automatic consent, or both', () => {
    const fetch = create(settings({ consentRenewalNeeded: true }));
    expect(fetch.el.textContent).toContain('fetching from the web is paused');
    expect(fetch.el.querySelector('a[data-testid="consent-renewal-open"]')!.getAttribute('href')).toBe('/admin/metadata');
    TestBed.resetTestingModule();
    expect(create(settings({ autoConsentRenewalNeeded: true })).el.textContent).toContain('Automatic matching sends');
    TestBed.resetTestingModule();
    expect(create(settings({ consentRenewalNeeded: true, autoConsentRenewalNeeded: true })).el.textContent)
      .toContain('fetching from the web and Automatic matching are paused');
  });

  it('inside Metadata Manager the button switches to Settings', () => {
    const { fixture, el } = create(settings({ consentRenewalNeeded: true }), true);
    const open = vi.fn();
    fixture.componentInstance.openSettings.subscribe(open);
    (el.querySelector('button[data-testid="consent-renewal-open"]') as HTMLButtonElement).click();
    expect(open).toHaveBeenCalled();
  });
});
