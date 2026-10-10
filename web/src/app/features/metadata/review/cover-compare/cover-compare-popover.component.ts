import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The review rows' cover comparison (1.28.0, owner): the folder's or archive's own cover next to the
 * series record's cover, large enough to spot a wrong link at a glance. Images are bound as URLs of the
 * server's own endpoints only (the record's stored poster, or a candidate image by server-side token).
 */
@Component({
  selector: 'app-cover-compare-popover',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="pair" data-testid="cover-compare">
      <figure>
        @if (localUrl(); as url) { <img [src]="url" alt="Your cover"> } @else { <div class="none">No cover</div> }
        <figcaption>Your cover</figcaption>
      </figure>
      <figure>
        @if (remoteUrl(); as url) { <img [src]="url" alt="Series cover"> } @else { <div class="none">No cover</div> }
        <figcaption>{{ remoteLabel() }}</figcaption>
      </figure>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .pair { display: flex; gap: 12px; padding: 10px; background: var(--mp-surface-raised); border: 1px solid rgb(var(--mp-ink-rgb) / 0.12);
      border-radius: 10px; box-shadow: 0 10px 30px rgb(var(--mp-shade-rgb) / 0.5); }
    figure { margin: 0; display: flex; flex-direction: column; align-items: center; gap: 6px; }
    img, .none { width: min(200px, 40vw); height: min(284px, 57vw); object-fit: contain; border-radius: 6px; background: var(--mp-surface-high); }
    .none { display: flex; align-items: center; justify-content: center; color: var(--mp-text-dim); font-size: 13px; }
    figcaption { font-size: 12px; color: var(--mp-text-secondary); }
  `],
})
export class CoverComparePopoverComponent {
  readonly localUrl = input<string | null>(null);
  readonly remoteUrl = input<string | null>(null);
  readonly remoteLabel = input('Series cover');
}
