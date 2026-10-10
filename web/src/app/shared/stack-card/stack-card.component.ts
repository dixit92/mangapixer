import { ChangeDetectionStrategy, Component, ViewEncapsulation, input } from '@angular/core';

/**
 * The stacked-paper cover card (1.29.0), shared by Home ("New chapters", the Favorites row) and the Favorites page -
 * and by the Volumes view's virtual volume stacks. It renders the 2:3 cover frame and, when `stacked`, two offset
 * sheets behind it; the cover's content (image, fallback icon, badges, (i) and star) is projected by the caller, which
 * keeps its own badge styles. Hover the enclosing link to outline the cover.
 *
 * Styles are not encapsulated (projected nodes carry the CALLER's style scope, so a scoped `.cover img` rule of this
 * component would never match them); every rule is namespaced under the `app-stack-card` element instead.
 */
@Component({
  selector: 'app-stack-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  template: `
    <div class="stack" [class.stacked]="stacked()">
      <div class="cover"><ng-content /></div>
    </div>
  `,
  styles: [`
    app-stack-card { display: block; }
    app-stack-card > .stack { position: relative; }
    app-stack-card > .stack > .cover {
      position: relative; z-index: 1; width: 100%; aspect-ratio: 2 / 3; border-radius: 8px;
      overflow: hidden; background: rgb(var(--mp-ink-rgb) / 0.06);
      display: flex; align-items: center; justify-content: center;
    }
    app-stack-card > .stack > .cover > img { width: 100%; height: 100%; object-fit: cover; position: relative; z-index: 1; }
    app-stack-card > .stack.stacked::before, app-stack-card > .stack.stacked::after {
      content: ''; position: absolute; inset: 0; border-radius: 8px; z-index: 0;
      background: rgb(var(--mp-ink-rgb) / 0.1); border: 1px solid rgb(var(--mp-ink-rgb) / 0.08);
    }
    app-stack-card > .stack.stacked::before { transform: translate(4px, -4px); }
    app-stack-card > .stack.stacked::after { transform: translate(8px, -8px); opacity: 0.55; }
    a:hover > app-stack-card > .stack > .cover, a:focus-visible > app-stack-card > .stack > .cover {
      outline: 2px solid rgb(var(--mp-accent-strong-rgb) / 0.6); outline-offset: 1px;
    }
  `],
})
export class StackCardComponent {
  /** Draw the paper sheets behind the cover (a stack of more than one). */
  readonly stacked = input(true);
}
