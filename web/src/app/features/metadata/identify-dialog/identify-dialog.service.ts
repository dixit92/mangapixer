import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

import { isPhone } from '../series-info-overlay.service';

/** Data handed to the identify dialog. */
export interface IdentifyDialogData {
  nodeId: string;
  /**
   * 1.34.0: several nodes identified one at a time ("1 of N" with Link / Skip / Stop; `nodeId` is the first). Fewer than two
   * ids = the normal single-node dialog.
   */
  nodeIds?: string[];
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
    return this.show({ nodeId, mode });
  }

  /** 1.34.0: identifies several nodes one at a time; resolves true when at least one was linked. */
  async openMany(nodeIds: readonly string[]): Promise<boolean> {
    if (nodeIds.length === 0) return false;
    return this.show({ nodeId: nodeIds[0], nodeIds: [...nodeIds], mode: 'link' });
  }

  private async show(data: IdentifyDialogData): Promise<boolean> {
    const mode = data.mode ?? 'link';
    const { IdentifyDialogComponent } = await import('./identify-dialog.component');
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
