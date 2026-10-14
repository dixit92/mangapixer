import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

import { trackerName } from './tracker-texts';

/**
 * Opens "Set MyAnimeList id" / "Set MangaUpdates id" (1.43.0) for the series a folder is linked to: a centered dialog on desktop,
 * full width on a phone. The dialog is imported on first use, so the series Admin menu only carries this small service.
 * Resolves true when the id changed.
 */
@Injectable({ providedIn: 'root' })
export class TrackerIdsDialogService {
  private readonly dialog = inject(MatDialog);

  async open(nodeId: string, slug: string): Promise<boolean> {
    const { TrackerIdsDialogComponent } = await import('./tracker-ids-dialog.component');
    const phone = globalThis.matchMedia?.('(max-width: 599.98px)').matches ?? false;
    const ref = this.dialog.open(TrackerIdsDialogComponent, {
      data: { nodeId, slug },
      width: phone ? '100vw' : '480px',
      maxWidth: '100vw',
      ariaLabel: `Set ${trackerName(slug)} id`,
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
    return (await firstValueFrom(ref.afterClosed())) === true;
  }
}
