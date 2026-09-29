import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The placeholder of a chapter the provider lists for a volume but the library does not hold (1.29.0), shown inside a
 * volume stack where the chapter belongs. Same 2:3 footprint as a chapter card, dashed and muted, "Ch. 39" large and
 * "Missing" small. Not clickable and not focusable (there is nothing to open); its accessible name reads
 * "Chapter 39, missing" and the tooltip says "Not in your library".
 */
@Component({
  selector: 'app-missing-chapter-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="missing" role="img" [attr.aria-label]="'Chapter ' + chapter() + ', missing'" title="Not in your library"
         data-testid="missing-chapter">
      <span class="num">Ch. {{ chapter() }}</span>
      <span class="tag">Missing</span>
    </div>
    <div class="caption" aria-hidden="true">&nbsp;</div>
  `,
  styles: [`
    :host { display: block; }
    .missing {
      aspect-ratio: 2 / 3; border-radius: 8px; box-sizing: border-box;
      border: 2px dashed rgba(255, 255, 255, 0.22); background: rgba(255, 255, 255, 0.03);
      display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4px;
      color: #8a8a99; cursor: default; user-select: none;
    }
    .num { font-size: 20px; font-weight: 600; }
    .tag { font-size: 11px; text-transform: uppercase; letter-spacing: 0.5px; }
    .caption { margin-top: 6px; font-size: 13px; }
  `],
})
export class MissingChapterCardComponent {
  /** The missing chapter's number ("39"). */
  readonly chapter = input.required<string>();
}
