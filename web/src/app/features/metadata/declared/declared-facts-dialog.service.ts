import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

import { DeclaredFactsScopeDto } from '../../../core/api/api-types';
import { isPhone } from '../series-info-overlay.service';
import { DeclaredScope } from './declared-facts-api.service';

/**
 * Opens the declared-facts editor (1.28.0) for a folder or a library: a centered dialog on desktop,
 * full width on phone. The component is imported on first use, so hosts (the folder admin menu, the
 * admin Libraries card) only carry this small service. Resolves the saved scope, or undefined.
 */
@Injectable({ providedIn: 'root' })
export class DeclaredFactsDialogService {
  private readonly dialog = inject(MatDialog);

  async open(scope: DeclaredScope): Promise<DeclaredFactsScopeDto | undefined> {
    const { DeclaredFactsDialogComponent } = await import('./declared-facts-dialog.component');
    const phone = isPhone();
    const ref = this.dialog.open(DeclaredFactsDialogComponent, {
      data: scope,
      width: phone ? '100vw' : '520px',
      maxWidth: '100vw',
      maxHeight: phone ? '100vh' : '90vh',
      ariaLabel: 'Declared facts',
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
    return await firstValueFrom(ref.afterClosed());
  }
}
