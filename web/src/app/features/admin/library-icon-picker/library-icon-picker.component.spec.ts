import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { ALLOWED_ICONS, LibraryIconPickerComponent } from './library-icon-picker.component';

@Component({
  standalone: true,
  imports: [LibraryIconPickerComponent],
  template: `
    <app-library-icon-picker [name]="name()" [current]="current()"
      (picked)="lastPicked.set($event)" (cancelled)="wasCancelled.set(true)" />
  `,
})
class HostComponent {
  readonly name = signal('My Library');
  readonly current = signal<string | null>(null);
  readonly lastPicked = signal<string | null | undefined>(undefined);
  readonly wasCancelled = signal(false);
}

describe('LibraryIconPickerComponent', () => {
  function create() {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders one cell per allowlisted icon plus a Default cell', () => {
    const fixture = create();
    const cells = fixture.nativeElement.querySelectorAll('.picker-cell');
    expect(cells.length).toBe(ALLOWED_ICONS.length + 1);
    expect(fixture.nativeElement.querySelector('.picker-cell.default')).not.toBeNull();
  });

  it('emits the icon name when a grid cell is clicked', () => {
    const fixture = create();
    const cells: HTMLButtonElement[] = fixture.nativeElement.querySelectorAll('.picker-cell');
    const starCell = Array.from(cells).find((c) => c.getAttribute('aria-label') === 'Use icon: star')!;
    starCell.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.lastPicked()).toBe('star');
  });

  it('emits null when the Default cell is clicked', () => {
    const fixture = create();
    const defaultCell: HTMLButtonElement = fixture.nativeElement.querySelector('.picker-cell.default');
    defaultCell.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.lastPicked()).toBeNull();
  });

  it('marks the current icon selected', () => {
    const fixture = create();
    fixture.componentInstance.current.set('bolt');
    fixture.detectChanges();
    const cells: HTMLButtonElement[] = fixture.nativeElement.querySelectorAll('.picker-cell');
    const boltCell = Array.from(cells).find((c) => c.getAttribute('aria-label') === 'Use icon: bolt')!;
    expect(boltCell.classList.contains('selected')).toBe(true);
  });

  it('emits cancel when Cancel is clicked', () => {
    const fixture = create();
    (fixture.nativeElement.querySelector('.picker-close') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.wasCancelled()).toBe(true);
  });
});
