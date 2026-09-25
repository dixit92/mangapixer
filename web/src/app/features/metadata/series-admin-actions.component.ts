import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable } from 'rxjs';

import { MetadataPrecedence, SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';

/**
 * Admin menu for one node's series metadata (1.24.0), shared by the overlay and the
 * series page. Acts on the node that was asked about (`info.nodeId`):
 * - Identify... - DISABLED slot reserved for lane B2 (web lookup). B2 enables it; no
 *   network code exists in B1.
 * - Don't match / Clear Don't match - the node is not one series; nothing is inherited.
 * - Unlink - removes the node's own web link (inheritance resumes).
 * - Source precedence (folders): inherit / web first / ComicInfo first.
 * Emits `changed` after a successful change so the host re-resolves the info.
 */
@Component({
  selector: 'app-series-admin-actions',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatDividerModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button mat-stroked-button type="button" [matMenuTriggerFor]="adminMenu" [disabled]="busy()"
            aria-label="Series admin actions" data-testid="series-admin-menu">
      <mat-icon>admin_panel_settings</mat-icon> Admin
    </button>
    <mat-menu #adminMenu="matMenu">
      <!-- Lane B2 slot: web identify. Disabled until the network half ships. -->
      <button mat-menu-item disabled data-slot="identify"
              matTooltip="Looking series up on the web is not available yet">
        <mat-icon>travel_explore</mat-icon> Identify…
      </button>
      <mat-divider />
      @if (ownDontMatch()) {
        <button mat-menu-item (click)="clearDontMatch()" data-testid="clear-dont-match">
          <mat-icon>undo</mat-icon> Clear "Don't match"
        </button>
      } @else {
        <button mat-menu-item (click)="dontMatch()" data-testid="dont-match"
                matTooltip="Not one series: nothing is inherited from above">
          <mat-icon>block</mat-icon> Don't match
        </button>
      }
      @if (ownLink()) {
        <button mat-menu-item (click)="unlink()" data-testid="unlink">
          <mat-icon>link_off</mat-icon> Unlink
        </button>
      }
      @if (isFolder()) {
        <mat-divider />
        <span class="caption">Source precedence</span>
        <button mat-menu-item (click)="setPrecedence(null)" data-testid="precedence-inherit">
          <mat-icon>vertical_align_top</mat-icon> Inherit
        </button>
        <button mat-menu-item (click)="setPrecedence('WebFirst')" data-testid="precedence-web">
          <mat-icon>public</mat-icon> Web first
        </button>
        <button mat-menu-item (click)="setPrecedence('ComicInfoFirst')" data-testid="precedence-comicinfo">
          <mat-icon>description</mat-icon> ComicInfo first
        </button>
      }
    </mat-menu>
  `,
  styles: [`
    :host { display: inline-flex; }
    button mat-icon { margin-right: 4px; }
    .caption {
      display: block; padding: 6px 16px 2px; font-size: 11px; font-weight: 600;
      text-transform: uppercase; letter-spacing: 0.5px; color: #8a8a99;
    }
  `],
})
export class SeriesAdminActionsComponent {
  private readonly api = inject(MetadataApiService);
  private readonly snackBar = inject(MatSnackBar);

  readonly info = input.required<SeriesInfoDto>();
  readonly changed = output<void>();

  readonly busy = signal(false);

  /** The node's OWN row (not inherited) is a Don't match. */
  readonly ownDontMatch = computed(() => {
    const link = this.info().link;
    return !!link && !link.inherited && link.state === 'DontMatch';
  });

  /** The node's OWN row is a web link. */
  readonly ownLink = computed(() => {
    const link = this.info().link;
    return !!link && !link.inherited && link.state !== 'DontMatch';
  });

  readonly isFolder = computed(() => this.info().nodeKind === 'Folder');

  dontMatch(): void {
    this.run(this.api.setDontMatch(this.info().nodeId), "Marked Don't match");
  }

  clearDontMatch(): void {
    this.run(this.api.clearDontMatch(this.info().nodeId), "Don't match cleared");
  }

  unlink(): void {
    this.run(this.api.unlink(this.info().nodeId), 'Series link removed');
  }

  setPrecedence(precedence: MetadataPrecedence | null): void {
    const id = this.info().nodeId;
    const call: Observable<unknown> = precedence
      ? this.api.setFolderPrecedence(id, precedence)
      : this.api.clearFolderPrecedence(id);
    this.run(call, precedence ? 'Source precedence set' : 'Source precedence cleared');
  }

  run(call: Observable<unknown>, message: string): void {
    this.busy.set(true);
    call.subscribe({
      next: () => {
        this.busy.set(false);
        this.snackBar.open(message, 'Close', { duration: 2500 });
        this.changed.emit();
      },
      error: (err: { message?: string }) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 4000 });
      },
    });
  }
}
