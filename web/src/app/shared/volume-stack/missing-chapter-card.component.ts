import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * The placeholder of something the library does not hold (1.29.0), in the place it belongs: a chapter (or a part of a split
 * chapter, "5.2") inside a volume stack, or - `kind="volume"`, 1.29.0 RC - a whole volume in the Volumes view. Same 2:3
 * footprint as a card, dashed and muted, "Ch. 39" / "Volume 3" large and "Missing" small. Not clickable and not focusable
 * (there is nothing to open); its accessible name reads "Chapter 39, missing" / "Volume 3, missing" and the tooltip says
 * "Not in your library". `compact` is the list-view row.
 */
@Component({
  selector: 'app-missing-chapter-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.compact]': 'compact()' },
  template: `
    <div class="missing" role="img" [attr.aria-label]="name() + ', missing'" title="Not in your library"
         [attr.data-testid]="kind() === 'volume' ? 'missing-volume' : 'missing-chapter'">
      <span class="num">{{ short() }}</span>
      <span class="tag">Missing</span>
    </div>
    @if (!compact()) {
      <div class="caption" aria-hidden="true">&nbsp;</div>
    }
  `,
  styles: [`
    :host { display: block; }
    .missing {
      aspect-ratio: 2 / 3; border-radius: 8px; box-sizing: border-box;
      border: 2px dashed rgb(var(--mp-ink-rgb) / 0.22); background: rgb(var(--mp-ink-rgb) / 0.03);
      display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4px;
      color: var(--mp-text-dim); cursor: default; user-select: none;
    }
    .num { font-size: 20px; font-weight: 600; text-align: center; }
    .tag { font-size: 11px; text-transform: uppercase; letter-spacing: 0.5px; }
    .caption { margin-top: 6px; font-size: 13px; }
    :host(.compact) .missing { aspect-ratio: auto; flex-direction: row; justify-content: flex-start; gap: 12px; padding: 12px; }
    :host(.compact) .num { font-size: 14px; }
  `],
})
export class MissingChapterCardComponent {
  /** The missing chapter's number ("39", "5.2"), or the missing volume's ("3"). */
  readonly chapter = input.required<string>();

  /** What is missing: a chapter (default) or a whole volume. */
  readonly kind = input<'chapter' | 'volume'>('chapter');

  /** The list-view row: no 2:3 frame. */
  readonly compact = input(false);

  readonly short = computed(() => (this.kind() === 'volume' ? `Volume ${this.chapter()}` : `Ch. ${this.chapter()}`));
  readonly name = computed(() => `${this.kind() === 'volume' ? 'Volume' : 'Chapter'} ${this.chapter()}`);
}
