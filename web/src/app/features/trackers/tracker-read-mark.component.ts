import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';

import { trackerName } from './tracker-texts';

/**
 * The card mark of tracker sync (1.43.0, owner's choice C): a thin gold strip along the top edge of a cover when the user's
 * tracker counts that chapter / volume as read (`CatalogNodeDto.trackerRead`, display only - the tracker's numbers as MangaPixer
 * last saw them). 3 px of `--mp-tracker-mark` with a 1 px `--mp-tracker-mark-edge` beneath it, so the strip stays visible on light
 * and dark covers in every theme. It sits under MangaPixer's own read badge row (the badges start 6 px down) and changes nothing
 * about them. The host is the cover box (`position: relative; overflow: hidden`), in card and list mode alike. Nothing is
 * rendered when no tracker counts the item.
 */
@Component({
  selector: 'app-tracker-read-mark',
  standalone: true,
  imports: [MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (label(); as text) {
      <span class="tracker-mark" role="img" [attr.aria-label]="text" [matTooltip]="text" data-testid="tracker-read-mark"
            [attr.data-trackers]="joined()"></span>
    }
  `,
  styles: [`
    :host { display: contents; }
    .tracker-mark {
      position: absolute; top: 0; left: 0; right: 0; z-index: 2;
      height: 3px; border-bottom: 1px solid var(--mp-tracker-mark-edge);
      background: var(--mp-tracker-mark);
    }
  `],
})
export class TrackerReadMarkComponent {
  /** The trackers (`mal`, `mangaupdates`) on which the item is read; null / empty = no mark. */
  readonly trackers = input<readonly string[] | null | undefined>(null);

  readonly joined = computed(() => (this.trackers() ?? []).join(' '));

  /** "Read on MyAnimeList", "Read on MyAnimeList and MangaUpdates", or null. */
  readonly label = computed(() => {
    const names = (this.trackers() ?? []).map((t) => trackerName(t));
    if (names.length === 0) return null;
    return `Read on ${names.length === 1 ? names[0] : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`}`;
  });
}
