import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { MetadataStateService } from '../../features/metadata/metadata-state.service';
import { SeriesInfoOverlayService } from '../../features/metadata/series-info-overlay.service';

/**
 * Card (i) affordance (1.24.0), the sibling of `StarToggleComponent`: a small round
 * `info_outline` button that opens the series-info overlay for its node. Rendered by
 * browse for every card and row (not in select mode - taps select there) and shows
 * itself only while the node has its OWN information: seeded from
 * `CatalogNodeDto.hasSeriesInfo`, then kept in sync in place through
 * `MetadataStateService.changed$` (Link / Undo / Unlink / Don't match), the way the star
 * follows `FavoritesStateService` - no browse reload. With `overlay` it sits
 * in the cover's free BOTTOM-LEFT corner (star top-left, read badge top-right,
 * direction chip bottom-right); in list rows it sits in `.row-markers` after the star.
 * Its own styles keep the near-budget browse CSS untouched.
 */
@Component({
  selector: 'app-info-toggle',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.overlay]': 'overlay()',
    '[class.compact]': 'compact()',
  },
  template: `
    @if (shown()) {
      <button type="button" mat-icon-button class="info-btn"
              aria-label="Series info" matTooltip="Series info"
              data-testid="info-toggle"
              (click)="open($event)">
        <mat-icon>info_outline</mat-icon>
      </button>
    }
  `,
  styles: [`
    :host { display: inline-flex; }
    :host(.overlay) { display: contents; }
    :host(.overlay) .info-btn {
      position: absolute; bottom: 4px; left: 4px; z-index: 3;
      background: rgba(0, 0, 0, 0.45);
    }
    .info-btn { width: 28px; height: 28px; line-height: 28px; padding: 0; }
    .info-btn mat-icon { font-size: 20px; width: 20px; height: 20px; color: #e6e6ee; }
    :host(.compact) .info-btn { width: 32px; height: 32px; }
  `],
})
export class InfoToggleComponent {
  private readonly overlayService = inject(SeriesInfoOverlayService);
  private readonly metadataState = inject(MetadataStateService);

  /** Opaque public id of the node (folder or archive). */
  readonly nodeId = input.required<string>();

  /** The node's own information, from the server (browse responses). */
  readonly hasSeriesInfo = input(false);

  /** Locally tracked visibility: seeded from the input, updated by link changes. */
  readonly shown = signal(false);

  constructor() {
    // Keep in sync with the input when the parent re-fetches (server truth).
    effect(() => this.shown.set(this.hasSeriesInfo()));

    // A link change for this node anywhere (dialog, admin menu, selection bar).
    this.metadataState.changed$.pipe(takeUntilDestroyed()).subscribe((change) => {
      if (change.nodeId === this.nodeId()) this.shown.set(change.hasSeriesInfo);
    });
  }

  /** Absolutely position in the cover's bottom-left corner. */
  readonly overlay = input(false);

  /** Dense list rows. */
  readonly compact = input(false);

  open(event: Event): void {
    // The button sits on top of a card link: never navigate or select.
    event.stopPropagation();
    event.preventDefault();
    void this.overlayService.open(this.nodeId());
  }
}
