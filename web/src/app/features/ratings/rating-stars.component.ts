import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { RatingsStateService } from './ratings-state.service';
import { STAR_COUNT, isScore, keyScore, ratedLabel, scoreOf, starIcon, starsText, valueText } from './rating-scale';

/** How `app-rating-stars` shows a rating. */
export type RatingStarsMode = 'edit' | 'stars' | 'badge';

/**
 * The one rating control (1.52.0, owner decision 5a): a stored 1-10 score shown as 5 stars with halves.
 *
 * - `edit` (default): a focusable `role="slider"` (`aria-valuemin` 1, `aria-valuemax` 10, `aria-valuetext` "3.5 of 5 stars"). A tap or
 *   click on the LEFT half of a star sets that half star, on the right half the whole star. Keyboard: Right / Up a half star more, Left /
 *   Down a half star less, Page Up / Down a whole star, Home / End half a star / five stars, Delete / Backspace clears. A visible
 *   "Clear rating" button next to it (never a hidden "tap the same value to clear"); 1.53.0 (audit V4): the button keeps its slot while
 *   unrated (hidden, not removed), so the stars never move on the first click. On a touch screen each half is a 20 x 40 target, so a
 *   whole star is 40 x 40.
 * - `stars`: the five stars read-only (`role="img"`, "Rated 3.5 of 5"); nothing when not rated.
 * - `badge`: one star and the number ("4.5") for cards and list rows; nothing when not rated. `overlay` places it on a cover, just above
 *   the favourite star's corner (bottom-right), so it never covers the card's title.
 *
 * Optimistic: an edit shows at once, saves through `RatingsStateService` (only the latest wanted score is sent while a save is in
 * flight) and reverts on error. Every instance of the same node follows `changed$`, so a rating set in the Info sheet updates the
 * card's badge in place.
 */
@Component({
  selector: 'app-rating-stars',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.overlay]': 'overlay()',
    '[class.is-badge]': "mode() === 'badge'",
  },
  template: `
    @switch (mode()) {
      @case ('edit') {
        <div class="edit">
          <!-- One focusable slider; its halves are pointer targets only (the keyboard drives the slider itself). -->
          <div class="stars" role="slider" tabindex="0" data-testid="rating-slider"
               [attr.aria-label]="label()"
               aria-valuemin="1" aria-valuemax="10"
               [attr.aria-valuenow]="value() ?? null"
               [attr.aria-valuetext]="valueText()"
               [attr.aria-disabled]="disabled() || null"
               (keydown)="onKey($event)" (click)="onClick($event)">
            @for (star of starList; track star) {
              <span class="star" [attr.data-star]="star">
                <mat-icon class="glyph" [class.on]="icon(star) !== 'star_border'">{{ icon(star) }}</mat-icon>
                <span class="half left" [attr.data-score]="star * 2 - 1" [attr.data-testid]="'rating-half-' + (star * 2 - 1)"></span>
                <span class="half right" [attr.data-score]="star * 2" [attr.data-testid]="'rating-half-' + star * 2"></span>
              </span>
            }
          </div>
          <span class="value" aria-hidden="true">{{ value() ? starsLabel() : 'Not rated' }}</span>
          <!-- 1.53.0 (audit V4): the button is always there and only hidden while unrated, so the row never changes size when you rate. -->
          <button mat-button type="button" class="clear" data-testid="rating-clear" [class.unrated]="!value()"
                  [attr.aria-hidden]="value() ? null : 'true'"
                  [disabled]="disabled() || !value()" (click)="clear($event)">
            Clear rating
          </button>
        </div>
      }
      @case ('stars') {
        @if (value()) {
          <span class="stars readonly" role="img" [attr.aria-label]="rated()" [matTooltip]="rated()">
            @for (star of starList; track star) {
              <mat-icon class="glyph" [class.on]="icon(star) !== 'star_border'">{{ icon(star) }}</mat-icon>
            }
          </span>
        }
      }
      @case ('badge') {
        @if (value()) {
          <span class="badge" role="img" data-testid="rating-badge" [attr.aria-label]="rated()" [matTooltip]="rated()">
            <mat-icon class="badge-star">star</mat-icon>{{ starsLabel() }}
          </span>
        }
      }
    }
  `,
  styles: [`
    :host { display: inline-flex; min-width: 0; }
    .edit { display: inline-flex; align-items: center; flex-wrap: wrap; gap: 4px 8px; }
    .stars { display: inline-flex; align-items: center; border-radius: 6px; outline: none; cursor: pointer; }
    .stars:focus-visible { box-shadow: 0 0 0 2px var(--mp-accent-strong); }
    .stars[aria-disabled="true"] { cursor: default; opacity: 0.6; }
    .star { position: relative; display: inline-flex; width: 28px; height: 28px; align-items: center; justify-content: center; }
    .glyph { font-size: 26px; width: 26px; height: 26px; color: var(--mp-text-dim); pointer-events: none; }
    .glyph.on { color: var(--mp-warn-strong); }
    .half { position: absolute; top: 0; bottom: 0; width: 50%; }
    .half.left { left: 0; }
    .half.right { right: 0; }
    .value { font-size: 13px; color: var(--mp-text-muted); min-width: 2.5em; }
    .clear { flex: none; }
    .clear.unrated { visibility: hidden; }
    .readonly { cursor: default; }
    .readonly .glyph { font-size: 18px; width: 18px; height: 18px; }
    .badge {
      display: inline-flex; align-items: center; gap: 2px; padding: 1px 6px 1px 3px; border-radius: 10px;
      font-size: 12px; font-weight: 600; line-height: 18px; white-space: nowrap;
      background: var(--mp-surface-high); color: var(--mp-text);
    }
    .badge-star { font-size: 15px; width: 15px; height: 15px; color: var(--mp-warn-strong); }
    /* On a cover (cards): above the favourite star's bottom-right corner, on the always-dark disc the cover badges use. */
    :host(.overlay) { position: absolute; right: 6px; bottom: 40px; z-index: 3; pointer-events: none; }
    :host(.overlay) .badge { background: rgb(var(--mp-shade-rgb) / 0.62); color: var(--mp-on-scrim); }
    /* theme-exempt: the rating star over a cover keeps the favourite star's amber on the always-dark disc (--mp-warn-strong turns brown in light themes) */
    :host(.overlay) .badge-star { color: #ffc107; }
    /* Touch screens: each half star a 20 x 40 target - a whole star 40 x 40 (portrait and landscape). */
    @media (pointer: coarse) {
      .star { width: 40px; height: 40px; }
      .glyph { font-size: 32px; width: 32px; height: 32px; }
      .clear { min-height: 40px; }
      :host(.overlay) { bottom: 48px; }
    }
  `],
})
export class RatingStarsComponent {
  private readonly ratings = inject(RatingsStateService);

  /** The rated node (a folder or an archive). */
  readonly nodeId = input.required<string>();
  /** The rating from the server (1-10), or null / undefined when not rated. */
  readonly rating = input<number | null | undefined>(null);
  readonly mode = input<RatingStarsMode>('edit');
  /** The slider's accessible name (edit mode). */
  readonly label = input<string>('Your rating');
  /** Badge on a cover (cards). */
  readonly overlay = input<boolean>(false);
  /** No edits (e.g. while the node's rating is still loading). */
  readonly disabled = input<boolean>(false);

  readonly starList = Array.from({ length: STAR_COUNT }, (_, i) => i + 1);

  /** The shown score: seeded from the input, changed optimistically and by other instances. */
  readonly value = signal<number | null>(null);
  readonly valueText = computed(() => valueText(this.value()));
  readonly starsLabel = computed(() => (isScore(this.value()) ? starsText(this.value()!) : ''));
  readonly rated = computed(() => (isScore(this.value()) ? ratedLabel(this.value()!) : ''));

  /** The score the server holds (the last confirmed one), for a revert. */
  private confirmed: number | null = null;
  private saving = false;
  /** The latest score wanted while a save is in flight (undefined = none queued). */
  private queued: number | null | undefined = undefined;

  constructor() {
    effect(() => {
      const r = this.rating();
      untracked(() => {
        const next = isScore(r) ? r : null;
        this.value.set(next);
        this.confirmed = next;
      });
    });
    this.ratings.changed$.pipe(takeUntilDestroyed()).subscribe((change) => {
      if (change.nodeId !== this.nodeId()) return;
      this.confirmed = change.score;
      if (!this.saving) this.value.set(change.score);
    });
  }

  icon(star: number): string {
    return starIcon(star, this.value());
  }

  /** A click or tap on a half star (the halves carry their score). */
  onClick(event: MouseEvent): void {
    event.stopPropagation();
    event.preventDefault();
    if (this.disabled()) return;
    const half = (event.target as Element | null)?.closest?.('[data-score]');
    const score = Number(half?.getAttribute('data-score'));
    if (isScore(score)) this.change(score);
    else {
      // A click on the star itself (no half under the pointer, e.g. its padding): by the side of the star's middle.
      const star = (event.target as Element | null)?.closest?.('[data-star]');
      const n = Number(star?.getAttribute('data-star'));
      if (!star || !Number.isInteger(n)) return;
      const box = star.getBoundingClientRect();
      this.change(scoreOf(n, event.clientX < box.left + box.width / 2 ? 'left' : 'right'));
    }
  }

  onKey(event: KeyboardEvent): void {
    const next = keyScore(event.key, this.value());
    if (next === undefined) return;
    event.preventDefault();
    event.stopPropagation();
    if (this.disabled() || next === this.value()) return;
    this.change(next);
  }

  clear(event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    if (!this.disabled()) this.change(null);
  }

  private change(score: number | null): void {
    if (score === this.value() && !this.saving) return;
    this.value.set(score);
    if (this.saving) {
      this.queued = score;
      return;
    }
    this.save(score);
  }

  private save(score: number | null): void {
    if (score === this.confirmed) {
      this.saving = false;
      return;
    }
    this.saving = true;
    this.ratings.setRating(this.nodeId(), score).subscribe({
      next: () => this.afterSave(),
      error: () => {
        this.queued = undefined;
        this.saving = false;
        this.value.set(this.confirmed); // revert to what the server holds
      },
    });
  }

  private afterSave(): void {
    const queued = this.queued;
    this.queued = undefined;
    if (queued === undefined) {
      this.saving = false;
      return;
    }
    this.save(queued);
  }
}
