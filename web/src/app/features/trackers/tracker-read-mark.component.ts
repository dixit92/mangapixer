import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { trackerName } from './tracker-texts';

/**
 * The card mark of tracker sync (1.43.0, the owner's V2 of 2026-10-15, replacing the top strip C): a gold ring inside the whole
 * cover when the user's tracker counts that chapter / volume as read (`CatalogNodeDto.trackerRead`, display only - the tracker's
 * numbers as MangaPixer last saw them). `--mp-tracker-mark-width` (2 px on cards, 1 px on the list thumbnail) of `--mp-tracker-mark`
 * with a 1 px `--mp-tracker-mark-edge` inside it. The ring's outer side meets the page, so the gold passes 3:1 against the page in
 * every theme; the edge keeps its inner side visible on light covers in the dark themes. Inset, so it never meets the selection
 * outline (3 px outside the card) or the Volumes hover ring. No grey state: a card without a ring is "not reached on the tracker".
 * The host is the cover box (`position: relative; overflow: hidden`), in card and list mode alike; the ring takes the cover's radius
 * (`--mp-tracker-mark-radius`) and never the pointer (it covers the whole cover, which must stay clickable), so the trackers' names
 * reach screen readers through `aria-label` only. Nothing is rendered when no tracker counts the item.
 */
@Component({
  selector: 'app-tracker-read-mark',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (label(); as text) {
      <span class="tracker-mark" role="img" [attr.aria-label]="text" data-testid="tracker-read-mark" [attr.data-trackers]="joined()"></span>
    }
  `,
  styles: [`
    :host { display: contents; }
    .tracker-mark {
      position: absolute; inset: 0; z-index: 2; pointer-events: none;
      border-radius: var(--mp-tracker-mark-radius);
      box-shadow:
        inset 0 0 0 var(--mp-tracker-mark-width) var(--mp-tracker-mark),
        inset 0 0 0 calc(var(--mp-tracker-mark-width) + 1px) var(--mp-tracker-mark-edge);
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
