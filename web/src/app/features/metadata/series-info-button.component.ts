import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { catchError, filter, forkJoin, map, merge, of, switchMap } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { IdentifyDialogService } from './identify-dialog/identify-dialog.service';
import { MetadataApiService } from './metadata-api.service';
import { MetadataStateService } from './metadata-state.service';
import { hasSeriesContent } from './series-info-labels';
import { SeriesInfoOverlayService } from './series-info-overlay.service';

/** What the top-bar slot shows for the folder being browsed. */
export type SeriesSlot = 'info' | 'identify' | 'none';

/**
 * Browse top-bar series slot (1.24.0). The current folder's series is usually
 * INHERITED by every chapter card inside it, so instead of an (i) on each of them this
 * one button serves the folder being viewed. The slot rule (owner decision, 1.24.0):
 * - the folder resolves to series information (own or inherited) -> "Series info",
 *   for admins and readers alike (opens the overlay);
 * - no information, the viewer is an admin, series information is shown for this
 *   library and Identify is possible (the no-network identify context says
 *   `fetchAvailable`) -> "Identify..." (opens the identify dialog for THIS folder);
 * - otherwise nothing (not every folder in a folder-native library is a series).
 * A link change for this folder (`MetadataStateService`) re-resolves the slot, so it
 * flips between "Identify..." and "Series info" without a refresh. Phone: icon only.
 */
@Component({
  selector: 'app-series-info-button',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (slot()) {
      @case ('info') {
        <button mat-stroked-button type="button" class="series-btn"
                matTooltip="Series info for this folder" aria-label="Series info"
                data-testid="series-info-button"
                (click)="open()">
          <mat-icon>info_outline</mat-icon><span class="lbl">Series info</span>
        </button>
      }
      @case ('identify') {
        <button mat-stroked-button type="button" class="series-btn"
                matTooltip="Look this folder up on the web" aria-label="Identify this folder"
                data-testid="series-identify-button"
                (click)="identify()">
          <mat-icon>travel_explore</mat-icon><span class="lbl">Identify…</span>
        </button>
      }
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
  private readonly auth = inject(AuthService);
  private readonly overlayService = inject(SeriesInfoOverlayService);
  private readonly identifyDialog = inject(IdentifyDialogService);
  private readonly metadataState = inject(MetadataStateService);

  /** The folder currently being browsed. */
  readonly nodeId = input.required<string>();

  private readonly nodeId$ = toObservable(this.nodeId);

  readonly slot = toSignal<SeriesSlot>(
    merge(
      this.nodeId$,
      this.metadataState.changed$.pipe(filter((c) => c.nodeId === this.nodeId()), map(() => this.nodeId())),
    ).pipe(switchMap((id) => this.resolve(id))),
    { initialValue: 'none' },
  );

  open(): void {
    void this.overlayService.open(this.nodeId());
  }

  identify(): void {
    // A Link announces itself through MetadataStateService, which re-resolves the slot.
    void this.identifyDialog.open(this.nodeId());
  }

  private resolve(id: string) {
    return this.api.getSeriesInfo(id).pipe(
      catchError(() => of(null)),
      switchMap((info) => {
        if (hasSeriesContent(info)) return of<SeriesSlot>('info');
        if (!info || !this.auth.isAdmin()) return of<SeriesSlot>('none');
        return forkJoin({ ctx: this.api.getIdentifyContext(id), settings: this.api.getSettings() }).pipe(
          map(({ ctx, settings }) => {
            const library = settings.libraries.find((l) => l.libraryId === ctx.libraryId);
            const shown = settings.showSeriesInfo && (library?.showSeriesInfo ?? true);
            return shown && ctx.fetchAvailable ? 'identify' as const : 'none' as const;
          }),
          catchError(() => of<SeriesSlot>('none')),
        );
      }),
    );
  }
}
