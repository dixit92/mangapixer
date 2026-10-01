import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { officialReleaseLabel } from '../../features/metadata/progress/series-progress-labels';

/**
 * The official-release mark of a volume held here only as chapters (1.30.0, reach): "Available in English" - the volume is out
 * officially in the preferred language, an upgrade, never "missing". `language` is the stack's `officialRelease` code (nothing
 * renders without one). `overlay` (a card) sits in the cover's bottom-left corner - a stack card has no (i) there; inline (the
 * stack view header, a list row) it is a small chip. Its own styles keep the near-budget browse CSS untouched.
 */
@Component({
  selector: 'app-official-release-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (label(); as text) {
      <span class="official" [class.overlay]="overlay()" [title]="text" data-testid="official-release-badge">{{ text }}</span>
    }
  `,
  styles: [`
    :host { display: contents; }
    .official {
      display: inline-block; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; vertical-align: middle;
      font-size: 11px; font-weight: 600; padding: 1px 7px; border-radius: 10px; background: rgba(124, 77, 255, 0.92); color: #fff;
    }
    .official.overlay { position: absolute; left: 6px; bottom: 6px; z-index: 2; max-width: calc(100% - 48px); }
  `],
})
export class OfficialReleaseBadgeComponent {
  readonly language = input<string | null | undefined>(null);
  readonly overlay = input(true);

  readonly label = computed(() => officialReleaseLabel(this.language()) || null);
}
