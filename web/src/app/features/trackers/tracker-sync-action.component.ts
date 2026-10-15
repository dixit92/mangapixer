import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';

import { CatalogNodeDto } from '../../core/api/api-types';
import { TrackerStateService } from './tracker-state.service';
import { TrackersApiService } from './trackers-api.service';
import { failureText } from './tracker-texts';
import { provideTrackerTooltips } from './tracker-tooltips';

/**
 * The browse selection's "Sync to trackers" (1.43.0, owner Q3), for every user with a connected tracker: On / Off / As default
 * for the selected folders and series (`PUT /api/v1/trackers/sync-overrides`). On also syncs inside a Private library and
 * doujinshi; Off stops sync anywhere; As default removes the switch. A folder's switch covers everything inside it; the nearest
 * switch wins. Hidden for a user without a connected tracker (the overview is loaded once, when the selection bar opens).
 */
@Component({
  selector: 'app-tracker-sync-action',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  providers: [provideTrackerTooltips()],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (state.hasConnected()) {
      <button mat-button type="button" [matMenuTriggerFor]="syncMenu" [disabled]="disabled() || busy() || chosen().length === 0"
              matTooltip="Sync the selection to your trackers" data-testid="tracker-sync-action">
        <mat-icon>sync</mat-icon><span class="lbl">Sync</span>
      </button>
      <mat-menu #syncMenu="matMenu" class="tracker-sync-menu">
        <span class="caption" data-testid="tracker-sync-caption">Sync to trackers ({{ chosen().length }})</span>
        <button mat-menu-item (click)="apply(true)" data-testid="tracker-sync-on">
          <mat-icon>sync</mat-icon>
          <span>On <span class="sub">- also in a Private library and for doujinshi</span></span>
        </button>
        <button mat-menu-item (click)="apply(false)" data-testid="tracker-sync-off">
          <mat-icon>sync_disabled</mat-icon>
          <span>Off <span class="sub">- never synced</span></span>
        </button>
        <button mat-menu-item (click)="apply(null)" data-testid="tracker-sync-default">
          <mat-icon>settings_backup_restore</mat-icon>
          <span>As default</span>
        </button>
        <p class="note" data-testid="tracker-sync-note">
          A folder's setting covers everything inside it. By default your Private libraries and doujinshi are not synced; turn
          sync on here for the folders or series you want.
        </p>
      </mat-menu>
    }
  `,
  styles: [`
    :host { display: contents; }
    mat-icon { margin-right: 4px; }
    @media (max-width: 599.98px) { .lbl { display: none; } mat-icon { margin-right: 0; } }
    .caption { display: block; padding: 6px 16px 2px; font-size: 11px; font-weight: 600; text-transform: uppercase;
               letter-spacing: 0.5px; color: var(--mp-text-dim); }
    .sub { color: var(--mp-text-muted); font-size: 12px; }
    .note { margin: 4px 16px 8px; max-width: 260px; font-size: 12px; line-height: 1.4; color: var(--mp-text-muted); white-space: normal; }
  `],
})
export class TrackerSyncActionComponent implements OnInit {
  private readonly api = inject(TrackersApiService);
  private readonly snackBar = inject(MatSnackBar);
  readonly state = inject(TrackerStateService);

  /** Every node currently listed. */
  readonly nodes = input.required<CatalogNodeDto[]>();
  /** Ids of the selected nodes (the host leaves volume stacks out: they are not stored nodes). */
  readonly selected = input.required<ReadonlySet<string>>();
  /** The host's own busy state. */
  readonly disabled = input(false);

  readonly busy = signal(false);

  /** The selected folders and archives. */
  readonly chosen = computed(() => this.nodes().filter((n) => this.selected().has(n.id) && (n.kind === 'Folder' || n.kind === 'Archive')));

  ngOnInit(): void {
    this.state.ensureLoaded();
  }

  apply(enabled: boolean | null): void {
    const ids = this.chosen().map((n) => n.id);
    if (ids.length === 0 || this.busy()) return;
    this.busy.set(true);
    this.api.setSyncOverrides(ids, enabled).subscribe({
      next: () => {
        this.busy.set(false);
        const what = ids.length === 1 ? '1 item' : `${ids.length} items`;
        const text = enabled === true ? `Sync on for ${what}` : enabled === false ? `Sync off for ${what}` : `${what} back to the default`;
        this.snackBar.open(text, 'Close', { duration: 3000 });
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${failureText(err)}`, 'Close', { duration: 4000 });
      },
    });
  }
}
