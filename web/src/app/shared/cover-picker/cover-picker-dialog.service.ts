import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

import { CoverStateDto } from '../../core/api/api-types';

/** Data handed to the cover picker. */
export interface CoverPickerDialogData {
  nodeId: string;
  displayName: string;
}

/** What the picker reports when it closes: the node's new cover state, or undefined when nothing changed. */
export type CoverPickerDialogResult = CoverStateDto | undefined;

/** Phone breakpoint of the dialogs (full-screen below it). */
function isPhoneWidth(): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia('(max-width: 599.98px)').matches;
}

/**
 * Opens the admin "Choose cover..." picker (1.29.0) for one node: centered on desktop, full-screen on phone. The
 * component is imported on first use, so the surfaces that offer it (browse selection bar, series admin menu) only carry
 * this small service.
 */
@Injectable({ providedIn: 'root' })
export class CoverPickerDialogService {
  private readonly dialog = inject(MatDialog);

  async open(nodeId: string, displayName: string): Promise<CoverPickerDialogResult> {
    const { CoverPickerDialogComponent } = await import('./cover-picker-dialog.component');
    const phone = isPhoneWidth();
    const ref = this.dialog.open<unknown, CoverPickerDialogData, CoverPickerDialogResult>(CoverPickerDialogComponent, {
      data: { nodeId, displayName },
      width: phone ? '100vw' : '720px',
      maxWidth: '100vw',
      height: phone ? '100vh' : undefined,
      maxHeight: phone ? '100vh' : '90vh',
      panelClass: phone ? 'cover-picker-fullscreen' : 'cover-picker-panel',
      ariaLabel: 'Choose cover',
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
    return await firstValueFrom(ref.afterClosed());
  }
}
