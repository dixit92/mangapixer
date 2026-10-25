import { Injectable, computed, signal } from '@angular/core';

/**
 * Webtoon (vertical) tap-to-scroll step, as a percentage of the viewport height
 * (1.11.0). `0` means OFF: the vertical reader progresses by free scroll only and
 * a tap anywhere toggles the chrome (the pre-1.11.0 behaviour). Any other value
 * turns on the tap zones (since 1.48.0: the two full-height side edges = back /
 * forward, the centre column's short top band = back and bottom band = forward,
 * the rest of the centre = toggle chrome) and the horizontal swipe, all moving by
 * this step.
 *
 * Under 100% the step leaves a small overlap between consecutive screens so a
 * panel straddling the fold is never skipped - the established webtoon reader
 * default (Mihon scrolls ~3/4 of a screen; the owner asked for ~90%).
 */
export type WebtoonTapStep = 0 | 80 | 90 | 100;
export const WEBTOON_TAP_STEPS: readonly WebtoonTapStep[] = [0, 80, 90, 100];

/** What a tap on the webtoon viewport does: step back a screen, step forward, or show / hide the controls. */
export type WebtoonTapZone = 'back' | 'toggle' | 'forward';

/**
 * Which side edge steps FORWARD (1.41.0, owner 2026-10-11): `right` (the default, like the paged
 * reader read left to right) or `left`. The other edge steps back. Per device, like the step; the vertical view
 * keeps ignoring the series' reading direction. The horizontal swipe follows it too.
 */
export type WebtoonForwardEdge = 'right' | 'left';
export const WEBTOON_FORWARD_EDGES: readonly WebtoonForwardEdge[] = ['right', 'left'];

/** Width of each full-height side edge, as a fraction of the viewport width (owner: 20 %). */
export const WebtoonEdgeFraction = 0.2;

/**
 * Height of the centre column's top (back) and bottom (forward) bands, each as a fraction of the visible height
 * (1.48.0, owner 2026-10-24: "remove top and bottom from the left and right edge"; the bands were a third each
 * across the full width before). 15 % of a 844 px phone screen is ~127 px, a comfortable thumb target; the rest of
 * the centre column (70 %) shows / hides the controls.
 */
export const WebtoonBandFraction = 0.15;

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
  static readonly ForwardEdgeKey = 'mangapixer-webtoon-forward-edge';
  static readonly DefaultForwardEdge: WebtoonForwardEdge = 'right';

  /** Current tap/swipe step (0 = off); reactive so the reader re-evaluates live. */
  readonly tapStep = signal<WebtoonTapStep>(this.loadTapStep());
  /** Tap zones (edges included) + swipe are on whenever a non-zero step is chosen. */
  readonly tapZonesEnabled = computed(() => this.tapStep() > 0);
  /** The middle band's edge that steps forward, and the swipe that does (1.41.0). */
  readonly forwardEdge = signal<WebtoonForwardEdge>(this.loadForwardEdge());

  setTapStep(step: WebtoonTapStep): void {
    this.tapStep.set(step);
    this.store(WebtoonNavPreferencesService.TapStepKey, String(step));
  }

  setForwardEdge(edge: WebtoonForwardEdge): void {
    this.forwardEdge.set(edge);
    this.store(WebtoonNavPreferencesService.ForwardEdgeKey, edge);
  }

  private store(key: string, value: string): void {
    try {
      localStorage.setItem(key, value);
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

  private loadForwardEdge(): WebtoonForwardEdge {
    try {
      const raw = localStorage.getItem(WebtoonNavPreferencesService.ForwardEdgeKey);
      if ((WEBTOON_FORWARD_EDGES as readonly (string | null)[]).includes(raw)) return raw as WebtoonForwardEdge;
    } catch {
      /* storage unavailable - fall through to the default */
    }
    return WebtoonNavPreferencesService.DefaultForwardEdge;
  }
}

/**
 * Resolve a tap's position within the webtoon viewport (fractions: x 0 = left edge, 1 = right; y 0 = top, 1 =
 * bottom) into a zone (1.48.0, owner 2026-10-24). Two full-height side edges, each `WebtoonEdgeFraction` (20 %) of
 * the width, corners included, step forward on `forwardEdge` and back on the other. In the centre column between
 * them a short top band (`WebtoonBandFraction`, 15 % of the height) goes back a screen and a bottom band of the same
 * height forward; everything else in the centre shows / hides the controls. Each zone holds its lower boundary:
 * x = 0.2 is the centre, x = 0.8 the right edge, y = 0.15 the toggle, y = 0.85 the bottom band. Pure, so it is
 * unit-testable without the DOM.
 */
export function webtoonTapZone(xFraction: number, yFraction: number, forwardEdge: WebtoonForwardEdge): WebtoonTapZone {
  if (xFraction < WebtoonEdgeFraction) return forwardEdge === 'left' ? 'forward' : 'back';
  if (xFraction >= 1 - WebtoonEdgeFraction) return forwardEdge === 'right' ? 'forward' : 'back';
  if (yFraction < WebtoonBandFraction) return 'back';
  if (yFraction >= 1 - WebtoonBandFraction) return 'forward';
  return 'toggle';
}

/**
 * The screen step a horizontal swipe asks for (1.41.0): the finger moves AWAY from the forward edge, like turning a
 * page - with "Right goes forward" a leftward swipe steps forward (the 1.11.0 rule), with "Left goes forward" a
 * rightward one. The other direction steps back.
 */
export function webtoonSwipeStep(side: 'leftward' | 'rightward', forwardEdge: WebtoonForwardEdge): 1 | -1 {
  return (side === 'leftward') === (forwardEdge === 'right') ? 1 : -1;
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
