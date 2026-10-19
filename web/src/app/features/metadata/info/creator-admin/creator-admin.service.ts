import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { ComponentType } from '@angular/cdk/portal';
import { firstValueFrom } from 'rxjs';

import { isPhone } from '../../series-info-overlay.service';
import { CreatorDto } from '../info-types';

/** What every creator admin dialog is opened with: the chip's creator and its name as shown. */
export interface CreatorDialogData {
  creatorId: string;
  display: string;
}

/**
 * Admin creator actions from a creator chip's menu (1.45.0, D1 2.2 "Admin merge / split"): Rename, Merge into..., Split.... Each opens
 * its dialog (imported on first use), which asks, confirms and sends; the promise resolves to the creator the server answered with
 * (renamed, the merge target, or the new split creator), or null when the admin cancelled.
 */
@Injectable({ providedIn: 'root' })
export class CreatorAdminService {
  private readonly dialog = inject(MatDialog);

  async rename(creatorId: string, display: string): Promise<CreatorDto | null> {
    const { CreatorRenameDialogComponent } = await import('./creator-rename-dialog.component');
    return this.open(CreatorRenameDialogComponent, { creatorId, display }, 'Rename creator');
  }

  async merge(creatorId: string, display: string): Promise<CreatorDto | null> {
    const { CreatorMergeDialogComponent } = await import('./creator-merge-dialog.component');
    return this.open(CreatorMergeDialogComponent, { creatorId, display }, 'Merge creator');
  }

  async split(creatorId: string, display: string): Promise<CreatorDto | null> {
    const { CreatorSplitDialogComponent } = await import('./creator-split-dialog.component');
    return this.open(CreatorSplitDialogComponent, { creatorId, display }, 'Split creator');
  }

  private async open<C>(component: ComponentType<C>, data: CreatorDialogData, ariaLabel: string): Promise<CreatorDto | null> {
    const phone = isPhone();
    const ref = this.dialog.open<C, CreatorDialogData, CreatorDto | undefined>(component, {
      data,
      width: phone ? '100vw' : '520px',
      maxWidth: '100vw',
      maxHeight: phone ? '100vh' : '90vh',
      ariaLabel,
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
    return (await firstValueFrom(ref.afterClosed())) ?? null;
  }
}
