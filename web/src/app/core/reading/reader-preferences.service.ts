import { Injectable, signal } from '@angular/core';

/**
 * Page-turn transition for the PAGED / DOUBLE-SPREAD reader (1.9.0). This is a
 * presentation nicety only — it never changes which page is shown, only how the
 * new page appears:
 *  - `slide`   — the incoming page eases in from the direction of travel
 *                (reading-direction aware), replacing the 1.8.x "snap back then
 *                 swap" jank on a page turn. The tasteful default.
 *  - `reveal`  — a quick cross-fade of the incoming page (no horizontal motion).
 *  - `none`    — instant swap (the pre-1.9.0 behaviour).
 *
 * The webtoon (vertical scroll) view is never affected: its page changes are
 * driven by native scrolling, not a discrete swap, so a transition would fight
 * the scroll. `prefers-reduced-motion` is honoured in CSS (the animation is
 * disabled there regardless of this setting), so this remains a pure preference.
 */
export type PageAnimation = 'slide' | 'reveal' | 'none';

/**
 * Per-DEVICE reader preferences kept in `localStorage` (never a backend/EF
 * preference — deliberately device-local, like the existing webtoon-width and
 * page-mode prefs in the reader). Covers:
 *  - the page-navigation animation (above), and
 *  - the one-shot onboarding "help seen" flag that auto-shows the reader help
 *    overlay the first time this device opens the reader.
 *
 * Every access is wrapped in try/catch so the service degrades gracefully when
 * storage is unavailable (private-mode Safari, storage disabled), exactly as the
 * reader component already does for its other localStorage-backed prefs.
 */
@Injectable({ providedIn: 'root' })
export class ReaderPreferencesService {
  static readonly PageAnimationKey = 'mangaplex-reader-page-animation';
  static readonly HelpSeenKey = 'mangaplex-reader-help-seen';

  /** Default page-turn transition (owner request 1.9.0): a tasteful slide. */
  static readonly DefaultPageAnimation: PageAnimation = 'slide';

  /** Current page-turn transition; reactive so the reader re-evaluates live. */
  readonly pageAnimation = signal<PageAnimation>(this.loadPageAnimation());

  setPageAnimation(mode: PageAnimation): void {
    this.pageAnimation.set(mode);
    try {
      localStorage.setItem(ReaderPreferencesService.PageAnimationKey, mode);
    } catch {
      /* storage unavailable (private mode) — keep the in-memory value */
    }
  }

  private loadPageAnimation(): PageAnimation {
    try {
      const raw = localStorage.getItem(ReaderPreferencesService.PageAnimationKey);
      if (raw === 'slide' || raw === 'reveal' || raw === 'none') return raw;
    } catch {
      /* storage unavailable — fall through to the default */
    }
    return ReaderPreferencesService.DefaultPageAnimation;
  }

  /**
   * Has the reader help overlay already been auto-shown (onboarding) on this
   * device? A missing/unreadable flag reads as "not seen", so a fresh device (or
   * cleared storage) triggers the one-time auto-show.
   */
  hasSeenHelp(): boolean {
    try {
      return localStorage.getItem(ReaderPreferencesService.HelpSeenKey) === '1';
    } catch {
      /* storage unavailable — treat as unseen, but markHelpSeen() will also fail,
         so the overlay simply auto-shows again next time rather than never */
      return false;
    }
  }

  /** Record that onboarding help has been shown, so it never auto-shows again. */
  markHelpSeen(): void {
    try {
      localStorage.setItem(ReaderPreferencesService.HelpSeenKey, '1');
    } catch {
      /* storage unavailable — nothing to persist */
    }
  }
}
