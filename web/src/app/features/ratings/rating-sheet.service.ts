import { Injectable, inject } from '@angular/core';
import { MatBottomSheet, MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Observable } from 'rxjs';

import { isPhone } from '../metadata/series-info-overlay.service';

/**
 * Opens the reader's Rate sheet (1.52.0): a bottom sheet on a phone, a small centred dialog on a wider screen. The sheet component is
 * imported on first use. Returns when it closes, so the reader can unpin its chrome.
 */
@Injectable({ providedIn: 'root' })
export class RatingSheetService {
  private readonly dialog = inject(MatDialog);
  private readonly bottomSheet = inject(MatBottomSheet);

  async open(archiveId: string): Promise<Observable<unknown>> {
    const { RatingSheetComponent } = await import('./rating-sheet.component');
    const data = { archiveId };
    if (isPhone()) {
      const ref: MatBottomSheetRef = this.bottomSheet.open(RatingSheetComponent, { data, ariaLabel: 'Rate', panelClass: 'rating-bottom-sheet' });
      return ref.afterDismissed();
    }
    const ref: MatDialogRef<unknown> = this.dialog.open(RatingSheetComponent, {
      data, ariaLabel: 'Rate', autoFocus: 'dialog', restoreFocus: true, width: '420px', maxWidth: '100vw',
    });
    return ref.afterClosed();
  }
}
