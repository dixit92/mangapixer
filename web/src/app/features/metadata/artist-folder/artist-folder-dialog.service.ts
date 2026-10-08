import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

import { SetArtistFolderRequest } from '../../../core/api/api-types';
import { isPhone } from '../series-info-overlay.service';

/**
 * Opens the "Artist folder" dialog (1.37.0): a centered dialog on desktop, full width on phone. The component is imported on first
 * use. Resolves the artist to declare, or undefined when cancelled.
 */
@Injectable({ providedIn: 'root' })
export class ArtistFolderDialogService {
  private readonly dialog = inject(MatDialog);

  async open(folderName: string): Promise<SetArtistFolderRequest | undefined> {
    const { ArtistFolderDialogComponent } = await import('./artist-folder-dialog.component');
    const phone = isPhone();
    const ref = this.dialog.open(ArtistFolderDialogComponent, {
      data: { folderName },
      width: phone ? '100vw' : '480px',
      maxWidth: '100vw',
      maxHeight: phone ? '100vh' : '90vh',
      ariaLabel: 'Artist folder',
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
    return await firstValueFrom(ref.afterClosed());
  }
}
