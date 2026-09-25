import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { catchError, of, switchMap } from 'rxjs';

import { SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';
import { hasSeriesContent } from './series-info-labels';
import { SeriesInfoOverlayService } from './series-info-overlay.service';

/**
 * Browse top-bar "Series info" button (1.24.0). The current folder's series is
 * usually INHERITED by every chapter card inside it, so instead of an (i) on each of
 * them this one button opens the overlay for the folder being viewed. Shown when the
 * folder resolves to series information (own or inherited) - for admins and readers
 * alike. Not every folder in a folder-native library is a series (author, magazine,
 * `Volumes/` folders), so a folder without information shows no button; admins reach
 * Identify / Don't match / precedence for any folder from the selection "Series" menu
 * (owner decision, 1.24.0). Phone: icon only.
 */
@Component({
  selector: 'app-series-info-button',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visible()) {
      <button mat-stroked-button type="button" class="series-btn"
              matTooltip="Series info for this folder" aria-label="Series info"
              data-testid="series-info-button"
              (click)="open()">
        <mat-icon>info_outline</mat-icon><span class="lbl">Series info</span>
      </button>
    }
  `,
  styles: [`
    :host { display: contents; }
    .series-btn mat-icon { margin-right: 4px; }
    @media (max-width: 599.98px) {
      .series-btn .lbl { display: none; }
      .series-btn mat-icon { margin-right: 0; }
      .series-btn { min-width: 0; padding: 0 8px; }
    }
  `],
})
export class SeriesInfoButtonComponent {
  private readonly api = inject(MetadataApiService);
  private readonly overlayService = inject(SeriesInfoOverlayService);

  /** The folder currently being browsed. */
  readonly nodeId = input.required<string>();

  private readonly info = toSignal<SeriesInfoDto | null>(
    toObservable(this.nodeId).pipe(
      switchMap((id) => this.api.getSeriesInfo(id).pipe(catchError(() => of(null)))),
    ),
    { initialValue: null },
  );

  readonly visible = computed(() => hasSeriesContent(this.info()));

  open(): void {
    void this.overlayService.open(this.nodeId());
  }
}
