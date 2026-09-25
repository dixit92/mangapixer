import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, forkJoin } from 'rxjs';

import { CatalogNodeDto, MetadataPrecedence } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';
import { PRECEDENCE_LABELS } from './series-info-labels';

/**
 * Browse selection-bar "Series" menu for admins (1.24.0), mirroring the reading-
 * direction action: mark the selected nodes "Don't match" (or clear it), and set /
 * clear the source precedence on the selected FOLDERS. Lane B2 adds "Identify" here
 * for a single selected node.
 */
@Component({
  selector: 'app-series-selection-actions',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatDividerModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button mat-button type="button" [matMenuTriggerFor]="seriesMenu"
            [disabled]="disabled() || busy() || selectedNodes().length === 0"
            matTooltip="Series metadata for the selection" data-testid="series-selection-menu">
      <mat-icon>info_outline</mat-icon><span class="lbl">Series</span>
    </button>
    <mat-menu #seriesMenu="matMenu">
      <button mat-menu-item (click)="dontMatch(true)" data-testid="bulk-dont-match">
        <mat-icon>block</mat-icon> Don't match
      </button>
      <button mat-menu-item (click)="dontMatch(false)" data-testid="bulk-clear-dont-match">
        <mat-icon>undo</mat-icon> Clear "Don't match"
      </button>
      <mat-divider />
      <span class="caption">Source precedence (folders)</span>
      <button mat-menu-item [disabled]="selectedFolders().length === 0" (click)="precedence('WebFirst')" data-testid="bulk-precedence-web">
        <mat-icon>public</mat-icon> Web first
      </button>
      <button mat-menu-item [disabled]="selectedFolders().length === 0" (click)="precedence('ComicInfoFirst')" data-testid="bulk-precedence-comicinfo">
        <mat-icon>description</mat-icon> ComicInfo first
      </button>
      <button mat-menu-item [disabled]="selectedFolders().length === 0" (click)="precedence(null)" data-testid="bulk-precedence-inherit">
        <mat-icon>vertical_align_top</mat-icon> Inherit (clear)
      </button>
    </mat-menu>
  `,
  styles: [`
    :host { display: contents; }
    mat-icon { margin-right: 4px; }
    .caption {
      display: block; padding: 6px 16px 2px; font-size: 11px; font-weight: 600;
      text-transform: uppercase; letter-spacing: 0.5px; color: #8a8a99;
    }
    @media (max-width: 599.98px) {
      .lbl { display: none; }
      mat-icon { margin-right: 0; }
    }
  `],
})
export class SeriesSelectionActionsComponent {
  private readonly api = inject(MetadataApiService);
  private readonly snackBar = inject(MatSnackBar);

  /** Every node currently listed. */
  readonly nodes = input.required<CatalogNodeDto[]>();

  /** Ids of the selected nodes. */
  readonly selected = input.required<ReadonlySet<string>>();

  /** The host's own busy state (bulk read marks etc.). */
  readonly disabled = input(false);

  readonly busy = signal(false);

  readonly selectedNodes = computed(() => this.nodes().filter((n) => this.selected().has(n.id)));
  readonly selectedFolders = computed(() => this.selectedNodes().filter((n) => n.kind === 'Folder'));

  dontMatch(on: boolean): void {
    const nodes = this.selectedNodes();
    const calls = nodes.map((n) => (on ? this.api.setDontMatch(n.id) : this.api.clearDontMatch(n.id)));
    this.runAll(calls, `${on ? "Don't match set" : "Don't match cleared"} on ${plural(nodes.length, 'item')}`);
  }

  precedence(value: MetadataPrecedence | null): void {
    const folders = this.selectedFolders();
    const calls: Observable<unknown>[] = folders.map((f) =>
      value ? this.api.setFolderPrecedence(f.id, value) : this.api.clearFolderPrecedence(f.id));
    const label = value ? PRECEDENCE_LABELS[value] : 'Inherit';
    this.runAll(calls, `Source precedence (${label}) on ${plural(folders.length, 'folder')}`);
  }

  private runAll(calls: Observable<unknown>[], message: string): void {
    if (calls.length === 0) return;
    this.busy.set(true);
    forkJoin(calls).subscribe({
      next: () => {
        this.busy.set(false);
        this.snackBar.open(message, 'Close', { duration: 2500 });
      },
      error: (err: { message?: string }) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 4000 });
      },
    });
  }
}

function plural(n: number, noun: string): string {
  return `${n} ${noun}${n === 1 ? '' : 's'}`;
}
