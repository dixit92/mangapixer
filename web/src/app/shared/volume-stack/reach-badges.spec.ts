import { TestBed } from '@angular/core/testing';

import { AlsoInVolumeBadgeComponent } from './also-in-volume-badge.component';
import { OfficialReleaseBadgeComponent } from './official-release-badge.component';

/** The reach markers (1.30.0): "Available in <language>" on a volume held as chapters, "Also in Volume N" on a chapter card. */
describe('reach badges', () => {
  it('names the official release language, overlay or inline, and hides without one', () => {
    const fixture = TestBed.createComponent(OfficialReleaseBadgeComponent);
    fixture.componentRef.setInput('language', 'en');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const badge = el.querySelector('[data-testid="official-release-badge"]')!;
    expect(badge.textContent).toBe('Available in English');
    expect(badge.classList.contains('overlay')).toBe(true);
    fixture.componentRef.setInput('overlay', false);
    fixture.componentRef.setInput('language', 'fr');
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="official-release-badge"]')!.textContent).toBe('Available in French');
    expect(el.querySelector('.overlay')).toBeNull();
    fixture.componentRef.setInput('language', null);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="official-release-badge"]')).toBeNull();
  });

  it('says which volume file already holds a chapter', () => {
    const fixture = TestBed.createComponent(AlsoInVolumeBadgeComponent);
    fixture.componentRef.setInput('volume', '10');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="also-in-volume-badge"]')!.textContent).toBe('Also in Volume 10');
    fixture.componentRef.setInput('volume', undefined);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="also-in-volume-badge"]')).toBeNull();
  });
});
