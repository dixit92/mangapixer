import { DestroyRef, Injectable, effect, inject, untracked } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { filter } from 'rxjs/operators';

import { AuthService } from '../../core/auth/auth.service';
import { WhatsNewDto } from '../../core/api/api-types';
import { WhatsNewApiService } from './whats-new-api.service';
import { WhatsNewDialogComponent, WhatsNewDialogData } from './whats-new-dialog.component';

/** The immersive reader - the dialog never opens over it. */
export function isReaderUrl(url: string): boolean {
  return url.split(/[?#]/)[0].startsWith('/reader');
}

/**
 * "What's new" (1.53.0, admins only). After an update, an admin's first page in the app asks the server for the release notes since the
 * version they last saw and, when there are any, shows them once in a dialog; closing it - "Got it", Escape or a tap outside - tells the
 * server they were seen. A failed request shows nothing (asked again at the next sign-in). Never over the reader: when the first page is
 * the reader, the check waits for the next page that is not. `openCurrent()` (the account menu's "What's new") shows the running
 * version's notes at any time.
 *
 * Created by the app shell (`LayoutComponent` injects it for the menu entry), so the check runs in every signed-in session.
 */
@Injectable({ providedIn: 'root' })
export class WhatsNewService {
  private readonly auth = inject(AuthService);
  private readonly api = inject(WhatsNewApiService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);

  /** The user whose check already ran in this app session (reset on sign-out, so the next sign-in asks again). */
  private checkedFor: string | null = null;
  /** Notes that arrived while the reader was open: shown on the next page that is not the reader. */
  private deferred: WhatsNewDto | null = null;
  private open: MatDialogRef<WhatsNewDialogComponent> | null = null;

  constructor() {
    effect(() => {
      const user = this.auth.currentUser();
      untracked(() => {
        if (!user) {
          this.checkedFor = null;
          this.deferred = null;
          return;
        }
        // Before the first navigation the URL is not known yet (a deep link to the reader reads as "/"); NavigationEnd checks then.
        if (this.router.navigated) this.check();
      });
    });
    const sub = this.router.events.pipe(filter((e) => e instanceof NavigationEnd)).subscribe(() => this.check());
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  /** The account menu's "What's new": the running version's notes, seen or not. Closing it changes nothing on the server. */
  openCurrent(): void {
    this.api.get(true).subscribe({
      next: (dto) => this.show({ current: dto.current, sections: dto.sections }, false),
      error: () => this.show({ current: '', sections: [], error: true }, false),
    });
  }

  private check(): void {
    const user = this.auth.currentUser();
    if (!user?.isAdmin || user.forcePasswordChange) return;
    if (isReaderUrl(this.router.url)) return;
    if (this.deferred) {
      const dto = this.deferred;
      this.deferred = null;
      this.show(dto, true);
      return;
    }
    if (this.checkedFor === user.id) return;
    this.checkedFor = user.id;
    this.api.get().subscribe({
      next: (dto) => {
        if (dto.sections.length === 0 || this.auth.currentUser()?.id !== user.id) return;
        if (isReaderUrl(this.router.url)) this.deferred = dto;
        else this.show(dto, true);
      },
      error: () => {
        // Nothing shows; the next sign-in asks again.
      },
    });
  }

  private show(data: WhatsNewDialogData, markSeen: boolean): void {
    if (this.open) return;
    const ref = this.dialog.open(WhatsNewDialogComponent, {
      data,
      width: '560px',
      maxWidth: 'calc(100vw - 32px)',
      maxHeight: 'calc(100dvh - 32px)',
      autoFocus: 'first-tabbable',
      restoreFocus: true,
      panelClass: 'whats-new-dialog',
    });
    this.open = ref;
    ref.afterClosed().subscribe(() => {
      this.open = null;
      if (markSeen) this.api.markSeen().subscribe({ error: () => undefined });
    });
  }
}
