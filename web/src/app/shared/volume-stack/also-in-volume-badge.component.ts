import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { alsoInVolumeLabel } from '../../features/metadata/progress/series-progress-labels';

/**
 * The overlap mark of a chapter archive (1.30.0, reach): "Also in Volume 10" - a volume FILE of the same series already holds
 * its chapters, so it counts once in what the folder holds. `volume` is the card's `alsoInVolume` key (nothing renders without
 * one). `overlay` (a card) sits at the top of the cover, clear of the corner marks; inline (a list row, a stack slot) it is a
 * small chip. Its own styles keep the near-budget browse CSS untouched.
 */
@Component({
  selector: 'app-also-in-volume-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (label(); as text) {
      <span class="also" [class.overlay]="overlay()" [title]="text" data-testid="also-in-volume-badge">{{ text }}</span>
    }
  `,
  styles: [`
    :host { display: contents; }
    .also {
      display: inline-block; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; vertical-align: middle;
      font-size: 11px; padding: 1px 7px; border-radius: 10px; background: rgb(var(--mp-shade-rgb) / 0.92); color: color-mix(in srgb, var(--mp-on-scrim) 70%, transparent);
      border: 1px solid color-mix(in srgb, var(--mp-on-scrim) 30%, transparent);
    }
    .also.overlay { position: absolute; left: 50%; transform: translateX(-50%); bottom: 34px; z-index: 2; max-width: calc(100% - 12px); }
  `],
})
export class AlsoInVolumeBadgeComponent {
  readonly volume = input<string | null | undefined>(null);
  readonly overlay = input(true);

  readonly label = computed(() => alsoInVolumeLabel(this.volume()) || null);
}
