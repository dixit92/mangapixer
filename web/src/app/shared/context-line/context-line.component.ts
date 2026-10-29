import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * The archive context line (1.50.0, lane W): a muted second line under an ARCHIVE's own name with the name of the folder it belongs to
 * (the server's `contextName`: the nearest ancestor that is not a plain Volumes / Chapters / Extras kind folder). A renamer or a library
 * layout that drops the series title from file names (`Berserk/Vol. 012.cbz`) leaves a found archive unrecognisable outside its folder;
 * this says which series it is. Shown on search results, quick results, Continue reading and Favorites - not in browse (the page is the
 * context).
 *
 * One line, ellipsis, the whole name in the `title` tooltip. Renders nothing for an empty / missing name (folders, root archives, a
 * server that predates the field), so a mount needs no condition. Theming: `--mp-*` tokens only. The host is a block with `min-width: 0`
 * so it never widens a grid cell or a strip card (phone layout: no wrapping, no overflow).
 */
@Component({
  selector: 'app-context-line',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (text(); as t) {
      <div class="ctx" [title]="t" data-testid="context-line">{{ t }}</div>
    }
  `,
  styles: [`
    :host { display: block; min-width: 0; max-width: 100%; }
    .ctx {
      margin-top: 2px; font-size: 12px; line-height: 1.3; color: var(--mp-text-muted);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
  `],
})
export class ContextLineComponent {
  /** The folder name (`contextName`); null / undefined / blank renders nothing. */
  readonly name = input<string | null | undefined>(null);

  readonly text = computed(() => this.name()?.trim() || null);
}
