import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ComponentType } from '@angular/cdk/portal';
import { catchError, firstValueFrom, of } from 'rxjs';

import { isPhone } from '../../series-info-overlay.service';
import { InfoApiService } from '../info-api.service';
import { CreatorDto } from '../info-types';

/** What every creator admin dialog is opened with: the creator (as lane F answered it) and its name as the chip showed it. */
export interface CreatorDialogData {
  creator: CreatorDto;
  display: string;
}

/** What a creator chip hands the admin actions: its tag value id, its name as shown and (1.47.0) its creator's id. */
export interface CreatorChipRef {
  id: string;
  display: string;
  creatorId?: string | null;
}

/**
 * Admin creator actions from a creator chip's menu (1.45.0, D1 2.2 "Admin merge / split"): Rename, Merge into..., Split.... The creator
 * is read first - 1.47.0: by the chip's `creatorId` (`GET creators/{id}`); only an answer without it falls back to finding the creator
 * that holds the chip's value by the chip's name - then its dialog (imported on first use) asks, confirms and sends. Resolves to the
 * creator the server answered with (renamed, the merge target, or the new split creator), or null when nothing was changed.
 */
@Injectable({ providedIn: 'root' })
export class CreatorAdminService {
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly api = inject(InfoApiService);

  async rename(chip: CreatorChipRef): Promise<CreatorDto | null> {
    const { CreatorRenameDialogComponent } = await import('./creator-rename-dialog.component');
    return this.open(CreatorRenameDialogComponent, chip, 'Rename creator');
  }

  async merge(chip: CreatorChipRef): Promise<CreatorDto | null> {
    const { CreatorMergeDialogComponent } = await import('./creator-merge-dialog.component');
    return this.open(CreatorMergeDialogComponent, chip, 'Merge creator');
  }

  async split(chip: CreatorChipRef): Promise<CreatorDto | null> {
    const { CreatorSplitDialogComponent } = await import('./creator-split-dialog.component');
    return this.open(CreatorSplitDialogComponent, chip, 'Split creator');
  }

  private async open<C>(component: ComponentType<C>, chip: CreatorChipRef, ariaLabel: string): Promise<CreatorDto | null> {
    const display = chip.display;
    const creator = await firstValueFrom(this.api.creatorForChip(chip).pipe(catchError(() => of(null))));
    if (!creator) {
      this.snackBar.open('This creator could not be found. Open the panel again in a moment.', 'OK', { duration: 6000 });
      return null;
    }
    const phone = isPhone();
    const ref = this.dialog.open<C, CreatorDialogData, CreatorDto | undefined>(component, {
      data: { creator, display },
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
