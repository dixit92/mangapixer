import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { CatalogNodeDto } from '../../core/api/api-types';
import { FolderCoverPreferenceDialogComponent, FolderCoverPreferenceDialogData } from './folder-cover-preference-dialog.component';

/**
 * The browse selection bar's "Folder covers..." action for admins (1.32.0): with exactly one FOLDER selected it opens the folder's
 * cover preference dialog (Inherit / Web covers when available / File covers). Disabled otherwise. Emits `saved` after a change so the
 * host can reload its list (the cards below may show other covers now).
 */
@Component({
  selector: 'app-folder-cover-preference-action',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button mat-button type="button" [disabled]="disabled() || !folder()" (click)="open()"
            matTooltip="Cover preference for the selected folder and everything below it" data-testid="folder-cover-preference-action">
      <mat-icon>photo_library</mat-icon><span class="lbl">Folder covers…</span>
    </button>
  `,
  styles: [`
    :host { display: contents; }
    mat-icon { margin-right: 4px; }
    @media (max-width: 599.98px) { .lbl { display: none; } mat-icon { margin-right: 0; } }
  `],
})
export class FolderCoverPreferenceActionComponent {
  private readonly dialog = inject(MatDialog);

  /** Every node currently listed. */
  readonly nodes = input.required<CatalogNodeDto[]>();

  /** Ids of the selected nodes. */
  readonly selected = input.required<ReadonlySet<string>>();

  /** The host's own busy state. */
  readonly disabled = input(false);

  /** A change was saved. */
  readonly saved = output<void>();

  /** The one selected folder, or null (none, several, or an archive / stack). */
  readonly folder = computed(() => {
    const chosen = this.nodes().filter((n) => this.selected().has(n.id));
    return chosen.length === 1 && chosen[0].kind === 'Folder' ? chosen[0] : null;
  });

  open(): void {
    const folder = this.folder();
    if (!folder) return;
    const data: FolderCoverPreferenceDialogData = { nodeId: folder.id, name: folder.displayName };
    this.dialog.open<FolderCoverPreferenceDialogComponent, FolderCoverPreferenceDialogData, boolean>(
      FolderCoverPreferenceDialogComponent, { data, width: '460px' })
      .afterClosed().subscribe((changed) => { if (changed) this.saved.emit(); });
  }
}
