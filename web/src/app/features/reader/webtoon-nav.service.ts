import { Injectable, computed, signal } from '@angular/core';

/**
 * Webtoon (vertical) tap-to-scroll step, as a percentage of the viewport height
 * (1.11.0). `0` means OFF: the vertical reader progresses by free scroll only and
 * a tap anywhere toggles the chrome (the pre-1.11.0 behaviour). Any other value
 * turns on the tap zones (top third = back a screen, bottom third = forward,
 * centre = toggle chrome) and the horizontal swipe, both moving by this step.
 *
 * Under 100% the step leaves a small overlap between consecutive screens so a
 * panel straddling the fold is never skipped - the established webtoon reader
 * default (Mihon scrolls ~3/4 of a screen; the owner asked for ~90%).
 */
export type WebtoonTapStep = 0 | 80 | 90 | 100;
export const WEBTOON_TAP_STEPS: readonly WebtoonTapStep[] = [0, 80, 90, 100];

/** The tap zone a vertical fraction (0 = top of the viewport, 1 = bottom) falls in. */
export type WebtoonTapZone = 'back' | 'toggle' | 'forward';

/**
 * Per-DEVICE webtoon navigation preference kept in `localStorage`, like the
 * webtoon width and the page-transition prefs (never a backend preference: it
 * describes this device's screen and the reader's hand, not the content).
 * Storage access is wrapped in try/catch so private-mode / disabled storage
 * degrades to the in-memory default, matching `ReaderPreferencesService`.
 */
@Injectable({ providedIn: 'root' })
export class WebtoonNavPreferencesService {
  static readonly TapStepKey = 'mangapixer-webtoon-tap-step';
  static readonly DefaultTapStep: WebtoonTapStep = 90;

  /** Current tap/swipe step (0 = off); reactive so the reader re-evaluates live. */
  readonly tapStep = signal<WebtoonTapStep>(this.loadTapStep());
  /** Tap zones + swipe are on whenever a non-zero step is chosen. */
  readonly tapZonesEnabled = computed(() => this.tapStep() > 0);

  setTapStep(step: WebtoonTapStep): void {
    this.tapStep.set(step);
    try {
      localStorage.setItem(WebtoonNavPreferencesService.TapStepKey, String(step));
    } catch {
      /* storage unavailable (private mode) - keep the in-memory value */
    }
  }

  private loadTapStep(): WebtoonTapStep {
    try {
      const raw = localStorage.getItem(WebtoonNavPreferencesService.TapStepKey);
      const n = raw === null ? NaN : Number(raw);
      if ((WEBTOON_TAP_STEPS as readonly number[]).includes(n)) return n as WebtoonTapStep;
    } catch {
      /* storage unavailable - fall through to the default */
    }
    return WebtoonNavPreferencesService.DefaultTapStep;
  }
}

/**
 * Resolve a tap's vertical position within the webtoon viewport into a zone,
 * by thirds: top third goes back a screen, bottom third forward, the middle
 * toggles the chrome. Pure, so it is unit-testable without the DOM.
 */
export function webtoonTapZone(fraction: number): WebtoonTapZone {
  if (fraction < 1 / 3) return 'back';
  if (fraction >= 2 / 3) return 'forward';
  return 'toggle';
}

/**
 * The scroll offset one tap/swipe step away from `scrollTop`, clamped to the
 * scroller's range. The step is `stepPct` of the visible height, so under 100%
 * consecutive screens overlap slightly and nothing is skipped.
 */
export function webtoonScrollTarget(
  scrollTop: number, clientHeight: number, scrollHeight: number, stepPct: number, direction: 1 | -1,
): number {
  const step = Math.round(clientHeight * stepPct / 100);
  const max = Math.max(0, scrollHeight - clientHeight);
  return Math.min(max, Math.max(0, scrollTop + direction * step));
}

/** True when the OS/browser asks for reduced motion (instant scroll and no page-turn ghost). */
export function prefersReducedMotion(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}
