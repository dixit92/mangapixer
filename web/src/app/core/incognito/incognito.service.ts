import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'mangapixer.incognito';

/**
 * Holds the client-side "Incognito" mode state for the current browser session.
 *
 * Design:
 * - Incognito **defaults ON for a new session** (a freshly opened tab/window):
 *   sessions start private until the user unhides for the session.
 * - The choice is persisted in **sessionStorage**, so it survives a page reload
 *   within the same tab but resets to ON when a new tab/window is opened. This is
 *   what lets the toggle drive a full reload (see LayoutComponent.toggleIncognito):
 *   the reload re-fetches every view with the correct `X-Incognito` header while
 *   the toggle itself is preserved. (Earlier this was in-memory only, which made a
 *   reload silently snap incognito back ON — the source of the "toggle needs a
 *   manual refresh / navigate away and back" bug.)
 * - The only server-durable piece is the per-user set of **Private** libraries.
 * - When incognito is on, the {@link incognitoInterceptor} attaches an
 *   `X-Incognito` header so the server hides Private libraries from the
 *   listing/discovery surfaces (continue-reading, search, browse-root, library
 *   list). Hiding is enforced server-side, so Private titles never reach the
 *   browser while incognito.
 */
@Injectable({ providedIn: 'root' })
export class IncognitoService {
  private readonly _incognito = signal(readInitial());

  /** Reactive incognito state. True = Private libraries are hidden. */
  readonly isIncognito = this._incognito.asReadonly();

  /** Flips incognito on/off (the user-dropdown quick toggle). */
  toggle(): void {
    this.setIncognito(!this._incognito());
  }

  /** Sets incognito explicitly and persists it for the session. */
  setIncognito(on: boolean): void {
    this._incognito.set(on);
    try {
      sessionStorage.setItem(STORAGE_KEY, on ? '1' : '0');
    } catch {
      /* sessionStorage unavailable (private mode, etc.) — state stays in memory */
    }
  }
}

/** Reads the persisted session choice; a new session (no value) defaults to ON. */
function readInitial(): boolean {
  try {
    const v = sessionStorage.getItem(STORAGE_KEY);
    return v === null ? true : v === '1';
  } catch {
    return true;
  }
}
