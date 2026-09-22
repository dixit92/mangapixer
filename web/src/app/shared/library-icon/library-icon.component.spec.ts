import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { LibraryIconComponent, deriveDefaultLibraryIcon } from './library-icon.component';

@Component({
  standalone: true,
  imports: [LibraryIconComponent],
  template: `<app-library-icon [name]="name()" [icon]="icon()" [size]="size()" />`,
})
class HostComponent {
  readonly name = signal('My Library');
  readonly icon = signal<string | null>(null);
  readonly size = signal(24);
}

describe('LibraryIconComponent', () => {
  function create() {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders the admin-picked icon ligature when set', () => {
    const fixture = create();
    fixture.componentInstance.icon.set('auto_stories');
    fixture.detectChanges();

    const matIcon = fixture.nativeElement.querySelector('app-library-icon mat-icon');
    expect(matIcon).not.toBeNull();
    expect(matIcon!.textContent!.trim()).toBe('auto_stories');
    expect(matIcon!.getAttribute('aria-hidden')).toBe('true');
    expect(fixture.nativeElement.querySelector('.lib-badge')).toBeNull();
  });

  it('renders a name-derived default badge when icon is null', () => {
    const fixture = create();
    fixture.componentInstance.name.set('Shonen Jump');
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('.lib-badge');
    expect(badge).not.toBeNull();
    expect(badge!.getAttribute('aria-hidden')).toBe('true');
    expect(badge!.textContent!.trim()).toBe('S');
    expect(fixture.nativeElement.querySelector('mat-icon')).toBeNull();
  });

  it('sizes the badge and glyph from the size input', () => {
    const fixture = create();
    fixture.componentInstance.size.set(48);
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('.lib-badge') as HTMLElement;
    expect(badge.style.width).toBe('48px');
    expect(badge.style.height).toBe('48px');
  });
});

describe('deriveDefaultLibraryIcon', () => {
  it('is a pure function of the name — same name always yields the same badge', () => {
    const a = deriveDefaultLibraryIcon('One Piece');
    const b = deriveDefaultLibraryIcon('One Piece');
    expect(a).toEqual(b);
  });

  it('picks the initial from the first letter/digit, ignoring leading whitespace/symbols', () => {
    expect(deriveDefaultLibraryIcon('  naruto').initial).toBe('N');
    expect(deriveDefaultLibraryIcon('#1 Manga').initial).toBe('1');
  });

  it('falls back to "?" for a name with no letters or digits', () => {
    expect(deriveDefaultLibraryIcon('  ---  ').initial).toBe('?');
  });

  it('distributes different names across more than one palette color', () => {
    const names = ['Alpha', 'Beta', 'Gamma', 'Delta', 'Epsilon', 'Zeta', 'Eta', 'Theta', 'Iota', 'Kappa'];
    const colors = new Set(names.map((n) => deriveDefaultLibraryIcon(n).bg));
    expect(colors.size).toBeGreaterThan(1);
  });
});
