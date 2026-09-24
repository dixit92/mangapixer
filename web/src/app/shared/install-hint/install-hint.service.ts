import { Injectable, InjectionToken, inject, signal } from '@angular/core';
import { isApplePlatformTouch, isStandaloneDisplay } from '../../features/reader/platform';

/** What made the hint appear: the first reader open on this device, or a Fullscreen tap. */
export type InstallHintReason = 'reader-open' | 'fullscreen';

/** The two platform questions the hint asks. A token so tests can answer them without faking `navigator`. */
export interface InstallHintEnv {
  isAppleTouch(): boolean;
  isStandalone(): boolean;
}

export const INSTALL_HINT_ENV = new InjectionToken<InstallHintEnv>('INSTALL_HINT_ENV', {
  providedIn: 'root',
  factory: () => ({
    isAppleTouch: () => isApplePlatformTouch(navigator),
    isStandalone: () => isStandaloneDisplay(window),
  }),
});

/**
 * "Add to Home Screen" hint for iPhone/iPad Safari users reading in a browser tab (1.23.0).
 *
 * In a Safari tab the reader's Fullscreen only hides MangaPixer's own bars (the browser chrome
 * stays); the installed home-screen app is the true full-screen experience, and iOS has no
 * `beforeinstallprompt` to offer it. So on Apple touch devices that are NOT already running
 * standalone, this service raises a dismissible hint at two moments (owner-decided):
 *  - the first reader open on this device (`onReaderOpened`), once, and
 *  - every Fullscreen tap (`onFullscreenRequested`) until the user says "Don't show again".
 *
 * "Not now" silences it for this page load only; "Don't show again" persists per device in
 * `localStorage`. Every storage access is try/catch'd (private-mode Safari can throw): the hint
 * still works, it just falls back to in-memory memory of the choice for the page load.
 *
 * The reader calls the two `on*` methods; `InstallHintComponent` (mounted once in the app shell)
 * renders `visible()`.
 */
@Injectable({ providedIn: 'root' })
export class InstallHintService {
  static readonly DismissedKey = 'mangapixer-install-hint-dismissed';
  static readonly ReaderOpenSeenKey = 'mangapixer-install-hint-reader-seen';

  private readonly env = inject(INSTALL_HINT_ENV);

  private readonly _visible = signal(false);
  private readonly _reason = signal<InstallHintReason | null>(null);
  readonly visible = this._visible.asReadonly();
  readonly reason = this._reason.asReadonly();

  // In-memory mirrors so the choices hold for this page load even when storage throws.
  private sessionDismissed = false;
  private foreverDismissed = false;
  private readerOpenHandled = false;

  /** Reader opened (call once per reader instance; further calls in the same page load are no-ops). */
  onReaderOpened(): void {
    if (this.readerOpenHandled) return;
    this.readerOpenHandled = true;
    if (!this.eligible()) return;
    if (this.readFlag(InstallHintService.ReaderOpenSeenKey)) return;
    this.writeFlag(InstallHintService.ReaderOpenSeenKey);
    this.show('reader-open');
  }

  /** The user tapped Fullscreen in the reader (call before/regardless of what Fullscreen does). */
  onFullscreenRequested(): void {
    if (!this.eligible()) return;
    this.show('fullscreen');
  }

  /** "Not now": hide, and stay quiet until the page is reloaded. */
  dismissForSession(): void {
    this.sessionDismissed = true;
    this.hide();
  }

  /** "Don't show again": hide and remember on this device. */
  dismissForever(): void {
    this.foreverDismissed = true;
    this.writeFlag(InstallHintService.DismissedKey);
    this.hide();
  }

  /** Hide without recording a choice (e.g. the user navigated away from the reader). */
  hide(): void {
    this._visible.set(false);
    this._reason.set(null);
  }

  private eligible(): boolean {
    if (this.sessionDismissed || this.foreverDismissed) return false;
    if (!this.env.isAppleTouch() || this.env.isStandalone()) return false;
    return !this.readFlag(InstallHintService.DismissedKey);
  }

  private show(reason: InstallHintReason): void {
    this._reason.set(reason);
    this._visible.set(true);
  }

  private readFlag(key: string): boolean {
    try {
      return localStorage.getItem(key) === '1';
    } catch {
      return false;
    }
  }

  private writeFlag(key: string): void {
    try {
      localStorage.setItem(key, '1');
    } catch {
      /* storage unavailable: the in-memory flags cover this page load */
    }
  }
}
