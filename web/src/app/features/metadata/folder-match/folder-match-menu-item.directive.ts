import { Directive, HostListener, Injectable, Injector, inject, input } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';

import { CatalogNodeDto } from '../../../core/api/api-types';
import { DeclaredFactsApiService } from '../declared/declared-facts-api.service';
import { MetadataStateService } from '../metadata-state.service';
import { isPhone } from '../series-info-overlay.service';
import type { FolderMatchDialogResult } from './folder-match-dialog.component';
import { appliedMessage } from './folder-match-labels';

/** The most folders one run takes (the server's limit, like the review bulk). */
export const MAX_FOLDER_MATCH = 200;

/**
 * Opens "Match folders by name" (1.38.0) for the selected nodes: the FOLDERS among them (at most 200; archives are ignored and counted).
 * The dialog component is imported on first use. Resolves what was marked, or undefined.
 */
@Injectable({ providedIn: 'root' })
export class FolderMatchDialogService {
  private readonly dialog = inject(MatDialog);

  async open(nodes: readonly CatalogNodeDto[]): Promise<FolderMatchDialogResult | undefined> {
    const folders = nodes.filter((n) => n.kind === 'Folder').slice(0, MAX_FOLDER_MATCH)
      .map((n) => ({ id: n.id, displayName: n.displayName }));
    if (folders.length === 0) return undefined;
    const { FolderMatchDialogComponent } = await import('./folder-match-dialog.component');
    const phone = isPhone();
    const ref = this.dialog.open(FolderMatchDialogComponent, {
      data: { folders, ignored: nodes.filter((n) => n.kind !== 'Folder').length },
      width: phone ? '100vw' : '720px',
      maxWidth: '100vw',
      maxHeight: phone ? '100vh' : '90vh',
      ariaLabel: 'Match folders by name',
      autoFocus: 'first-tabbable',
      restoreFocus: true,
      disableClose: true,
    });
    return await firstValueFrom(ref.afterClosed());
  }
}

/**
 * The browse Series menu's "Match folders by name..." item (1.38.0): put on a `mat-menu-item` with the selected nodes. Opens the dialog,
 * then refreshes the marked folders' series information and says what was done (after the dialog closed, so nothing covers it).
 */
@Directive({ selector: '[appFolderMatch]', standalone: true })
export class FolderMatchMenuItemDirective {
  /** The selected nodes (folders and archives). */
  readonly appFolderMatch = input.required<readonly CatalogNodeDto[]>();

  private readonly dialogs = inject(FolderMatchDialogService);
  private readonly metadataState = inject(MetadataStateService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly injector = inject(Injector);

  @HostListener('click')
  async onClick(): Promise<void> {
    const result = await this.dialogs.open(this.appFolderMatch());
    if (!result) return;
    result.marked.forEach((id) => this.metadataState.refresh(id));
    if (result.kind === 'Artists' && result.marked.length) this.injector.get(DeclaredFactsApiService).version.update((v) => v + 1);
    this.snackBar.open(appliedMessage(result.kind, result.marked.length, result.failed, result.queued), 'Close', { duration: 5000 });
  }
}
