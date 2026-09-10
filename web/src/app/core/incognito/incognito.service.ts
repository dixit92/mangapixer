import { Injectable, signal } from '@angular/core';

/**
 * Holds the client-side "Incognito" mode state for the current session.
 *
 * Design (owner decision, 2026-09-10 — see the vault's "Post-1.3.0 release
 * planning" entry):
 * - Incognito is **session state that defaults ON at every launch**. It is held
 *   in memory only and deliberately NOT persisted: a fresh page load starts
 *   incognito again, which is exactly the "reset to incognito each session"
 *   behaviour — the user's action is to *unhide* (turn it off) for the session.
 * - The only durable piece is the per-user set of **Private** libraries, which
 *   is stored server-side (owned by the incognito-and-continue-data backend lane).
 * - When incognito is on, the {@link incognitoInterceptor} attaches an
 *   `X-Incognito` header so the server filters Private libraries out of the
 *   listing/discovery surfaces (continue-reading, search, browse-root, library
 *   list). Hiding is enforced server-side, not in the UI, so Private titles
 *   never reach the browser while incognito.
 */
@Injectable({ providedIn: 'root' })
export class IncognitoService {
  // Default ON each launch: sessions start private until the user unhides.
  private readonly _incognito = signal(true);

  /** Reactive incognito state. True = Private libraries are hidden. */
  readonly isIncognito = this._incognito.asReadonly();

  /** Flips incognito on/off (the user-dropdown quick toggle). */
  toggle(): void {
    this._incognito.update((on) => !on);
  }

  /** Sets incognito explicitly. */
  setIncognito(on: boolean): void {
    this._incognito.set(on);
  }
}
