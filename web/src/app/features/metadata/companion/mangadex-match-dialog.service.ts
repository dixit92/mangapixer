import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

import { isPhone } from '../series-info-overlay.service';

/**
 * Opens "Change MangaDex match..." (1.29.0) for the series a node is linked to: a centered dialog on desktop, full
 * width on phone. The component is imported on first use, so the admin menu only carries this small service.
 * Resolves true when the companion changed.
 */
@Injectable({ providedIn: 'root' })
export class MangaDexMatchDialogService {
  private readonly dialog = inject(MatDialog);

  async open(nodeId: string): Promise<boolean> {
    const { MangaDexMatchDialogComponent } = await import('./mangadex-match-dialog.component');
    const phone = isPhone();
    const ref = this.dialog.open(MangaDexMatchDialogComponent, {
      data: { nodeId },
      width: phone ? '100vw' : '520px',
      maxWidth: '100vw',
      maxHeight: phone ? '100vh' : '90vh',
      ariaLabel: 'Change MangaDex match',
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
    return (await firstValueFrom(ref.afterClosed())) === true;
  }
}
