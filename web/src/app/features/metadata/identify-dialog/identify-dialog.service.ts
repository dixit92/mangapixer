import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

import { isPhone } from '../series-info-overlay.service';

/** Data handed to the identify dialog. */
export interface IdentifyDialogData {
  nodeId: string;
  /**
   * `collection` (1.34.0): pick the series a folder of works is ABOUT (fan works) - the action marks the folder "Collection about"
   * that series instead of linking it. Default `link`.
   */
  mode?: IdentifyMode;
}

export type IdentifyMode = 'link' | 'collection';

/** What the dialog reports when it closes: true when a link was made. */
export type IdentifyDialogResult = boolean | undefined;

/**
 * Opens the admin identify dialog (1.24.0, lane B2) for one node: a centered dialog on
 * desktop, full-screen on phone. The component is imported on first use, so surfaces
 * that offer "Identify..." only carry this small service. Resolves true after a link.
 */
@Injectable({ providedIn: 'root' })
export class IdentifyDialogService {
  private readonly dialog = inject(MatDialog);

  async open(nodeId: string, mode: IdentifyMode = 'link'): Promise<boolean> {
    const { IdentifyDialogComponent } = await import('./identify-dialog.component');
    const data: IdentifyDialogData = { nodeId, mode };
    const phone = isPhone();
    const ref = this.dialog.open<unknown, IdentifyDialogData, IdentifyDialogResult>(IdentifyDialogComponent, {
      data,
      width: phone ? '100vw' : '760px',
      maxWidth: '100vw',
      height: phone ? '100vh' : undefined,
      maxHeight: phone ? '100vh' : '90vh',
      panelClass: phone ? 'identify-dialog-fullscreen' : 'identify-dialog-panel',
      ariaLabel: mode === 'collection' ? 'Collection about a series' : 'Identify series',
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
    return (await firstValueFrom(ref.afterClosed())) === true;
  }
}
