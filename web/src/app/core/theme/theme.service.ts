import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';

import { ApiService } from '../api/api.service';
import { AuthService } from '../auth/auth.service';
import {
  DEFAULT_THEME_ACCENT,
  DEFAULT_THEME_BASE,
  ResolvedThemeBase,
  ThemeAccent,
  ThemeBase,
  isThemeAccent,
  isThemeBase,
} from './theme-vocabulary';

/** The localStorage keys of the device copy (read before Angular boots by the inline script in index.html). */
export const THEME_STORAGE_KEY = 'mangapixer-theme';
export const ACCENT_STORAGE_KEY = 'mangapixer-accent';

const PREFERS_LIGHT = '(prefers-color-scheme: light)';

/**
 * The theme engine (1.40.0). Holds the user's base theme and accent and paints them on `<html>`:
 * `data-theme` (the RESOLVED base - `system` becomes light or dark from the device, followed live), `data-accent`, and
 * `<meta name="theme-color">` from the painted surface. `color-scheme` (which Material and native controls follow) comes from
 * `styles.scss`, keyed on `data-theme` (dark / black -> dark, light / sepia -> light), so whatever sets the attribute - this
 * service, the boot script, a test or a screenshot tool - gets the matching scheme; nothing writes it inline.
 *
 * Storage: per user on the server (`GET /reading/preferences`, `PUT /reading/preferences/appearance`) with a device copy
 * in localStorage, which the inline script in `index.html` applies before Angular boots (no flash of dark before light).
 * On start the device copy is applied at once; once a user is signed in the server's copy wins and refreshes the device
 * copy. Signing out keeps the device copy, so the login page shows the last look used on this device (else the default).
 * Unknown stored values fall back to the defaults (dark / violet).
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);

  private readonly _base = signal<ThemeBase>(DEFAULT_THEME_BASE);
  private readonly _accent = signal<ThemeAccent>(DEFAULT_THEME_ACCENT);
  private readonly prefersLight = signal(false);

  /** The chosen base theme (may be `system`). */
  readonly base = this._base.asReadonly();
  /** The chosen accent. */
  readonly accent = this._accent.asReadonly();
  /** The base as painted on this device: `system` resolved through `prefers-color-scheme`. */
  readonly resolved = computed<ResolvedThemeBase>(() => {
    const base = this._base();
    return base === 'system' ? (this.prefersLight() ? 'light' : 'dark') : base;
  });

  /** True while a change is being saved to the server. */
  readonly saving = signal(false);
  /** The last save error (null after a successful save). The change stays applied on this device. */
  readonly saveError = signal<string | null>(null);

  /** Bumped on every local choice, so a server answer that was requested before it cannot undo it. */
  private localVersion = 0;
  private loadedFor: string | null = null;
  private started = false;

  constructor() {
    this._base.set(readStored(THEME_STORAGE_KEY, isThemeBase, DEFAULT_THEME_BASE));
    this._accent.set(readStored(ACCENT_STORAGE_KEY, isThemeAccent, DEFAULT_THEME_ACCENT));

    const query = typeof window !== 'undefined' && typeof window.matchMedia === 'function' ? window.matchMedia(PREFERS_LIGHT) : null;
    if (query) {
      this.prefersLight.set(query.matches);
      const onChange = (e: MediaQueryListEvent): void => {
        this.prefersLight.set(e.matches);
        this.apply();
      };
      query.addEventListener('change', onChange);
      inject(DestroyRef).onDestroy(() => query.removeEventListener('change', onChange));
    }
    this.apply();

    // The server copy: load it whenever a (different) user is signed in.
    effect(() => {
      const user = this.auth.currentUser();
      const id = user ? user.id : null;
      untracked(() => {
        if (id === null) {
          this.loadedFor = null;
        } else if (id !== this.loadedFor) {
          this.loadedFor = id;
          this.loadFromServer();
        }
      });
    });
  }

  /** Called once by the app initializer: paints the device copy (the effect above brings the server copy later). */
  start(): void {
    if (this.started) return;
    this.started = true;
    this.apply();
  }

  /** Chooses a base theme: applies it at once, keeps the device copy and saves it for the signed-in user. */
  setBase(base: ThemeBase): void {
    if (!isThemeBase(base)) return;
    this.choose({ theme: base });
  }

  /** Chooses an accent: applies it at once, keeps the device copy and saves it for the signed-in user. */
  setAccent(accent: ThemeAccent): void {
    if (!isThemeAccent(accent)) return;
    this.choose({ accent });
  }

  private choose(change: { theme?: ThemeBase; accent?: ThemeAccent }): void {
    this.localVersion++;
    if (change.theme) this._base.set(change.theme);
    if (change.accent) this._accent.set(change.accent);
    this.storeLocal();
    this.apply();

    if (!this.auth.isAuthenticated()) return;
    this.saving.set(true);
    this.saveError.set(null);
    this.api.setAppearance(change).subscribe({
      next: () => this.saving.set(false),
      error: (err: { message?: string }) => {
        this.saving.set(false);
        this.saveError.set(err?.message || 'Could not save the appearance; it applies on this device only.');
      },
    });
  }

  private loadFromServer(): void {
    const requestedAt = this.localVersion;
    this.api.getPreferences().subscribe({
      next: (prefs) => {
        if (this.localVersion !== requestedAt) return; // the user chose meanwhile: keep that
        this._base.set(isThemeBase(prefs?.theme) ? prefs.theme : DEFAULT_THEME_BASE);
        this._accent.set(isThemeAccent(prefs?.accent) ? prefs.accent : DEFAULT_THEME_ACCENT);
        this.storeLocal();
        this.apply();
      },
      error: () => {
        /* keep the device copy */
      },
    });
  }

  private storeLocal(): void {
    try {
      localStorage.setItem(THEME_STORAGE_KEY, this._base());
      localStorage.setItem(ACCENT_STORAGE_KEY, this._accent());
    } catch {
      /* storage unavailable (private mode): the theme still applies for this page */
    }
  }

  /**
   * Reads the CURRENT token values of other base / accent combinations, for the Appearance card's previews: the attributes
   * on `<html>` are switched, the tokens read and the attributes restored within one task, so nothing is painted in between
   * and whatever values the theme files define (light here, black / sepia / accents in `src/themes/`) show up as they are.
   * Returns '' for a token the document does not define (e.g. in a test DOM without the stylesheet).
   */
  samplePalettes(requests: readonly { base: ResolvedThemeBase; accent: ThemeAccent }[]): PaletteSample[] {
    if (typeof document === 'undefined' || typeof getComputedStyle !== 'function') {
      return requests.map(() => ({ surface: '', text: '', accent: '', accentStrong: '' }));
    }
    const root = document.documentElement;
    const theme = root.getAttribute('data-theme');
    const accent = root.getAttribute('data-accent');
    try {
      return requests.map((r) => {
        root.setAttribute('data-theme', r.base);
        root.setAttribute('data-accent', r.accent);
        const style = getComputedStyle(root);
        const read = (name: string): string => style.getPropertyValue(name).trim();
        return {
          surface: read('--mp-surface'),
          text: read('--mp-text'),
          accent: read('--mp-accent'),
          accentStrong: read('--mp-accent-strong'),
        };
      });
    } finally {
      restore(root, 'data-theme', theme);
      restore(root, 'data-accent', accent);
    }
  }

  /** Paints the current choice on `<html>` and the browser chrome. */
  private apply(): void {
    if (typeof document === 'undefined') return;
    const root = document.documentElement;
    const resolved = this.resolved();
    root.setAttribute('data-theme', resolved);
    root.setAttribute('data-accent', this._accent());
    this.updateThemeColor();
  }

  /**
   * `<meta name="theme-color">` = the colour the page is painted with, so the browser / OS bars match it: the body's background
   * when it paints one, else the page colour on <html> (`--mp-page`, per base - the black and sepia pages included).
   */
  private updateThemeColor(): void {
    const meta = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
    if (!meta || typeof getComputedStyle !== 'function') return;
    const body = document.body ? getComputedStyle(document.body).backgroundColor : '';
    const root = getComputedStyle(document.documentElement);
    const color = isOpaque(body)
      ? body
      : isOpaque(root.backgroundColor)
        ? root.backgroundColor
        : root.getPropertyValue('--mp-page').trim();
    if (color) meta.setAttribute('content', color);
  }
}

/** Token values of one base / accent combination (see {@link ThemeService.samplePalettes}). */
export interface PaletteSample {
  surface: string;
  text: string;
  accent: string;
  accentStrong: string;
}

function restore(el: HTMLElement, name: string, value: string | null): void {
  if (value === null) el.removeAttribute(name);
  else el.setAttribute(name, value);
}

function readStored<T extends string>(key: string, valid: (v: unknown) => v is T, fallback: T): T {
  try {
    const value = localStorage.getItem(key);
    return valid(value) ? value : fallback;
  } catch {
    return fallback;
  }
}

/** False for '' / 'transparent' / a fully transparent colour (an unstyled body in a test DOM). */
export function isOpaque(color: string): boolean {
  const c = color.trim();
  if (!c || c === 'transparent') return false;
  const fn = /^rgba?\(([^)]*)\)$/i.exec(c);
  if (!fn) return true;
  const parts = fn[1].split(/[\s,/]+/).filter(Boolean);
  if (parts.length < 4) return true;
  const alpha = parts[3];
  return (alpha.endsWith('%') ? parseFloat(alpha) / 100 : parseFloat(alpha)) > 0;
}
