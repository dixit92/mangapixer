import { Injectable, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { RatingSharingDto } from '../../core/api/api-types';

/**
 * Shared ratings on the client (1.53.0): whether the administrator's "Shared ratings" switch is on - browse offers the "Server rating"
 * sort only then - and the user's own "Share my ratings" choice. Read once per session from `GET ratings/sharing` (`ensureLoaded`),
 * refreshed by Settings and by the admin card after a change. Off until known: nothing that depends on it shows before the answer.
 */
@Injectable({ providedIn: 'root' })
export class RatingSharingService {
  private readonly api = inject(ApiService);
  private requested = false;

  /** The admin switch (false until read, or when the read failed). */
  readonly serverEnabled = signal(false);
  /** The user's own choice (on by default). */
  readonly share = signal(true);
  readonly loaded = signal(false);

  /** Reads the state once (later calls do nothing). */
  ensureLoaded(): void {
    if (this.requested) return;
    this.requested = true;
    this.reload().subscribe({ error: () => { this.requested = false; } });
  }

  /** Reads the state again (Settings on open, the admin card after a change). */
  reload(): Observable<RatingSharingDto> {
    return this.api.getRatingSharing().pipe(tap((dto) => this.apply(dto)));
  }

  /** Saves the user's choice; the signal follows on success. */
  setShare(share: boolean): Observable<void> {
    return this.api.setRatingSharing(share).pipe(tap(() => this.share.set(share)));
  }

  /** The admin card changed the switch: follow it without a read. */
  serverChanged(enabled: boolean): void {
    this.serverEnabled.set(enabled);
  }

  private apply(dto: RatingSharingDto): void {
    this.serverEnabled.set(dto.serverEnabled ?? false);
    this.share.set(dto.share ?? true);
    this.loaded.set(true);
  }
}
