import { Component, inject, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ReaderPreferencesService, PageAnimation } from '../../core/reading/reader-preferences.service';

/**
 * Reader settings config surface (1.9.0). A small, self-contained toolbar button
 * that opens a menu for the page-navigation TRANSITION (Slide / Reveal / None),
 * kept out of the large `ReaderComponent` so the reader only needs a one-line
 * wiring point. The choice persists per-device via `ReaderPreferencesService`.
 *
 * It mirrors the reader's other menus: the active option carries a check mark,
 * and `opened`/`closed` are surfaced so the reader can pin its auto-hiding chrome
 * visible while the menu is open (same treatment as the mode/fit menus).
 */
@Component({
  selector: 'app-reader-settings-menu',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  template: `
    <button mat-icon-button [matMenuTriggerFor]="animMenu"
            matTooltip="Page transition" aria-label="Page transition"
            (menuOpened)="opened.emit()" (menuClosed)="closed.emit()">
      <mat-icon>animation</mat-icon>
    </button>
    <mat-menu #animMenu="matMenu">
      <button mat-menu-item (click)="choose('slide')" aria-label="Page transition: Slide">
        <mat-icon>{{ prefs.pageAnimation() === 'slide' ? 'check' : 'view_carousel' }}</mat-icon>
        Slide
      </button>
      <button mat-menu-item (click)="choose('reveal')" aria-label="Page transition: Reveal">
        <mat-icon>{{ prefs.pageAnimation() === 'reveal' ? 'check' : 'gradient' }}</mat-icon>
        Reveal
      </button>
      <button mat-menu-item (click)="choose('none')" aria-label="Page transition: None">
        <mat-icon>{{ prefs.pageAnimation() === 'none' ? 'check' : 'block' }}</mat-icon>
        None
      </button>
    </mat-menu>
  `,
})
export class ReaderSettingsMenuComponent {
  readonly prefs = inject(ReaderPreferencesService);

  /** Emitted when the transition menu opens / closes, so the reader can pin chrome. */
  readonly opened = output<void>();
  readonly closed = output<void>();

  choose(mode: PageAnimation): void {
    this.prefs.setPageAnimation(mode);
  }
}
