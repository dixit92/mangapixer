import { ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxChange, MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';

import { DEFAULT_HOME_ROW_ORDER, HOME_ROW_LABELS, HomeRowId, moveHomeRow } from './home-rows';

/**
 * Settings > "Home" (1.54.0, owner decision 3): the Home rows in the user's order, each with Move up / Move down, the show / hide
 * switches of the two optional rows (Favorites, Recommended for you) beside them, and Reset. Presentational: Settings owns the
 * `library-preferences` blob (one writer, so no save here can echo another card's stale fields) - this card only says what changed.
 * After a move, focus stays on the moved row (its other button when the one pressed is now disabled at an end) and the new position is
 * announced. The move buttons never disable while a save is in flight (a disabled button cannot keep the focus); Settings sends the
 * latest order once the previous save has answered.
 */
@Component({
  selector: 'app-home-rows-card',
  standalone: true,
  imports: [MatButtonModule, MatCardModule, MatCheckboxModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card data-testid="home-rows-card">
      <mat-card-header>
        <mat-card-title>Home</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        <p class="hint">The rows of the home page, in your order. Saved for your account, on every device.</p>
        @if (loaded()) {
          <ol class="rows">
            @for (row of order(); track row; let i = $index, first = $first, last = $last) {
              <li class="row" data-testid="home-row-item" [attr.data-row]="row">
                <span class="label">{{ labels[row] }}</span>
                @if (row === 'favorites') {
                  <mat-checkbox class="show" [checked]="showFavorites()" [disabled]="favoritesDisabled()" data-testid="show-favorites-row" aria-label="Show the Favorites row on the home page"
                                (change)="showFavoritesChange.emit($event)">Show</mat-checkbox>
                } @else if (row === 'recommended') {
                  <mat-checkbox class="show" [checked]="showRecommendations()" [disabled]="recommendationsDisabled()" data-testid="show-recommendations-row" aria-label="Show the Recommended for you row on the home page"
                                (change)="showRecommendationsChange.emit($event)">Show</mat-checkbox>
                }
                <span class="moves">
                  <button mat-icon-button type="button" data-move="up" [disabled]="first"
                          [attr.aria-label]="'Move ' + labels[row] + ' up'" [title]="'Move ' + labels[row] + ' up'" (click)="move(i, -1)">
                    <mat-icon>arrow_upward</mat-icon>
                  </button>
                  <button mat-icon-button type="button" data-move="down" [disabled]="last"
                          [attr.aria-label]="'Move ' + labels[row] + ' down'" [title]="'Move ' + labels[row] + ' down'" (click)="move(i, 1)">
                    <mat-icon>arrow_downward</mat-icon>
                  </button>
                </span>
              </li>
            }
          </ol>
          <div class="actions">
            <button mat-stroked-button type="button" [disabled]="isDefault()" (click)="resetOrder.emit()">Reset to default order</button>
          </div>
          <span class="sr-status" role="status" aria-live="polite">{{ announcement() }}</span>
          @if (error()) {
            <div class="error" role="alert">{{ error() }}</div>
          }
        } @else {
          <p class="muted">Loading…</p>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .hint { color: var(--mp-text-muted); font-size: 13px; margin: 0 0 8px; }
    .muted { color: var(--mp-text-muted); font-size: 14px; }
    .error { color: var(--mp-error-strong); font-size: 14px; margin: 8px 0 0; }
    .rows { list-style: none; margin: 0; padding: 0; }
    .row { display: flex; align-items: center; gap: 8px; min-height: 44px; border-bottom: 1px solid rgb(var(--mp-ink-rgb) / 0.08); }
    .row:last-child { border-bottom: none; }
    .label { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 14px; }
    .show { flex: none; }
    .moves { flex: none; display: flex; }
    .actions { display: flex; justify-content: flex-end; margin-top: 8px; }
    .sr-status { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
    @media (pointer: coarse) {
      .moves button { width: 44px; height: 44px; }
      .actions button { min-height: 40px; }
    }
  `],
})
export class HomeRowsCardComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly order = input.required<HomeRowId[]>();
  readonly loaded = input(true);
  readonly favoritesDisabled = input(false);
  readonly recommendationsDisabled = input(false);
  readonly error = input<string | null>(null);
  readonly showFavorites = input(false);
  readonly showRecommendations = input(true);

  readonly orderChange = output<HomeRowId[]>();
  readonly resetOrder = output<void>();
  readonly showFavoritesChange = output<MatCheckboxChange>();
  readonly showRecommendationsChange = output<MatCheckboxChange>();

  readonly labels = HOME_ROW_LABELS;
  readonly announcement = signal('');

  isDefault(): boolean {
    return this.order().every((row, i) => row === DEFAULT_HOME_ROW_ORDER[i]);
  }

  move(index: number, delta: -1 | 1): void {
    const before = this.order();
    const row = before[index];
    const next = moveHomeRow(before, index, delta);
    if (next[index] === row) return;
    this.orderChange.emit(next);
    this.announcement.set(`${this.labels[row]} moved to position ${index + delta + 1} of ${next.length}.`);
    // The moved row's element is re-inserted, which drops focus: put it back on the same button, or on the other one at an end.
    afterNextRender(() => {
      const item = this.host.nativeElement.querySelector(`[data-row="${row}"]`);
      const same = item?.querySelector<HTMLButtonElement>(`[data-move="${delta < 0 ? 'up' : 'down'}"]`);
      const other = item?.querySelector<HTMLButtonElement>(`[data-move="${delta < 0 ? 'down' : 'up'}"]`);
      (same && !same.disabled ? same : other)?.focus();
    }, { injector: this.injector });
  }
}
