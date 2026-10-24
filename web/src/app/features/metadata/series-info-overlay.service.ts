import { Injectable, inject } from '@angular/core';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { MatDialog } from '@angular/material/dialog';

/** The phone breakpoint shared with library-browse (Material XSmall). */
export const PHONE_QUERY = '(max-width: 599.98px)';

/**
 * Data handed to the overlay component through MAT_DIALOG_DATA / MAT_BOTTOM_SHEET_DATA: a node, or (1.47.0) a library root - which is
 * not a node, has no series information and shows its tags alone under the library's name.
 */
export type SeriesInfoOverlayData =
  | { nodeId: string; libraryId?: undefined; libraryName?: undefined }
  | { libraryId: string; libraryName: string; nodeId?: undefined };

/**
 * Opens the Info overlay (1.24.0; "Info" since 1.45.0) for a node without navigating away, so the
 * browse scroll position is untouched: a right-side sheet on desktop/tablet (the
 * library sidebar is on the left), a bottom sheet (up to 90vh) on phone. Backdrop
 * click and Esc close both. The overlay component is imported on first use, so the
 * browse chunk only carries this small service.
 */
@Injectable({ providedIn: 'root' })
export class SeriesInfoOverlayService {
  private readonly dialog = inject(MatDialog);
  private readonly bottomSheet = inject(MatBottomSheet);

  async open(nodeId: string): Promise<void> {
    await this.show({ nodeId });
  }

  /** 1.47.0: the Info overlay of a library root (its tags only). */
  async openLibrary(libraryId: string, libraryName: string): Promise<void> {
    await this.show({ libraryId, libraryName });
  }

  private async show(data: SeriesInfoOverlayData): Promise<void> {
    const { SeriesInfoOverlayComponent } = await import('./series-info-overlay.component');
    if (isPhone()) {
      this.bottomSheet.open(SeriesInfoOverlayComponent, {
        data,
        panelClass: 'series-info-bottom-sheet',
        ariaLabel: 'Information',
      });
      return;
    }
    this.dialog.open(SeriesInfoOverlayComponent, {
      data,
      panelClass: 'series-info-side-sheet',
      position: { right: '0', top: '0' },
      height: '100vh',
      width: '420px',
      maxWidth: '100vw',
      ariaLabel: 'Information',
      autoFocus: 'dialog',
      restoreFocus: true,
    });
  }
}

export function isPhone(): boolean {
  return typeof matchMedia === 'function' && matchMedia(PHONE_QUERY).matches;
}
