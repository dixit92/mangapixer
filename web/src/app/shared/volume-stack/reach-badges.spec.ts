import { TestBed } from '@angular/core/testing';

import { AlsoInVolumeBadgeComponent } from './also-in-volume-badge.component';
import { OfficialReleaseBadgeComponent } from './official-release-badge.component';

/**
 * The reach markers (1.30.0): "Official Volume Available" on a volume held as chapters (1.43.0 wording; the language is in its
 * tooltip), "Also in Volume N" on a chapter card.
 */
describe('reach badges', () => {
  it('names the official release language, overlay or inline, and hides without one', () => {
    const fixture = TestBed.createComponent(OfficialReleaseBadgeComponent);
    fixture.componentRef.setInput('language', 'en');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const badge = el.querySelector('[data-testid="official-release-badge"]')!;
    expect(badge.textContent).toBe('Official Volume Available');
    expect(badge.getAttribute('title')).toBe('Out as an official English volume - you have it as chapters');
    expect(badge.classList.contains('overlay')).toBe(true);
    fixture.componentRef.setInput('overlay', false);
    fixture.componentRef.setInput('language', 'fr');
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="official-release-badge"]')!.textContent).toBe('Official Volume Available');
    expect(el.querySelector('[data-testid="official-release-badge"]')!.getAttribute('title')).toBe('Out as an official French volume - you have it as chapters');
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
