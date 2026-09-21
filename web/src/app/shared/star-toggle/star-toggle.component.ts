import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { FavoritesStateService } from '../../core/favorites/favorites-state.service';

/**
 * Reusable "star" favorite toggle (1.21.0), used on browse cards + list rows, in the
 * reader toolbar, in search results, and on the favorites view. Material
 * `star` / `star_border`, keyboard-accessible (it is a real `<button>`, so Enter/Space
 * work for free), with an `aria-pressed` + `aria-label` that reflect state.
 *
 * Optimistic: it flips its own icon immediately, persists through
 * `FavoritesStateService`, and reverts on error. It also subscribes to the service's
 * `changed$` so a toggle of the SAME node anywhere else updates this instance in place.
 *
 * All styling lives here (its own component budget) so the near-limit inline styles of
 * `library-browse.component.ts` and `reader.component.ts` are not touched. Set
 * `overlay` to position the star in a cover corner (host becomes `display: contents`
 * and the button is absolutely placed against the nearest positioned ancestor).
 */
@Component({
  selector: 'app-star-toggle',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.overlay]': 'overlay()',
    '[class.compact]': 'compact()',
  },
  template: `
    <button
      type="button"
      mat-icon-button
      class="star-btn"
      [class.active]="isFav()"
      [attr.aria-pressed]="isFav()"
      [attr.aria-label]="label()"
      [matTooltip]="label()"
      (click)="toggle($event)"
    >
      <mat-icon>{{ isFav() ? 'star' : 'star_border' }}</mat-icon>
    </button>
  `,
  styles: [`
    :host { display: inline-flex; }
    :host(.overlay) { display: contents; }
    :host(.overlay) .star-btn {
      position: absolute; top: 4px; left: 4px; z-index: 3;
      background: rgba(0, 0, 0, 0.45);
    }
    .star-btn.active mat-icon { color: #ffc107; }
    :host(.compact) .star-btn {
      width: 32px; height: 32px; line-height: 32px; padding: 0;
    }
    :host(.compact) .star-btn mat-icon {
      font-size: 20px; width: 20px; height: 20px;
    }
  `],
})
export class StarToggleComponent {
  private readonly favorites = inject(FavoritesStateService);

  /** Opaque public id of the node to favorite (an archive or a folder). */
  readonly nodeId = input.required<string>();

  /** Current favorite state from the server (browse/search/node responses). */
  readonly favorite = input<boolean>(false);

  /** Smaller footprint for dense list rows. */
  readonly compact = input<boolean>(false);

  /** Absolutely position the star in a cover corner (host becomes display:contents). */
  readonly overlay = input<boolean>(false);

  /** Locally-tracked state; seeded from the input and updated optimistically. */
  readonly isFav = signal(false);

  readonly label = computed(() => (this.isFav() ? 'Remove from favorites' : 'Add to favorites'));

  constructor() {
    // Keep in sync with the input when the parent re-fetches (server truth).
    effect(() => this.isFav.set(this.favorite()));

    // Cross-instance sync: another star for the same node toggled elsewhere.
    this.favorites.changed$.pipe(takeUntilDestroyed()).subscribe((change) => {
      if (change.nodeId === this.nodeId()) this.isFav.set(change.favorite);
    });
  }

  toggle(event: Event): void {
    // The star often sits on top of a card link; don't navigate when toggling.
    event.stopPropagation();
    event.preventDefault();

    const next = !this.isFav();
    this.isFav.set(next); // optimistic
    this.favorites.setFavorite(this.nodeId(), next).subscribe({
      error: () => this.isFav.set(!next), // revert on failure
    });
  }
}
