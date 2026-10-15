import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { TRACKER_CALLBACK_PATH } from './tracker-texts';

/**
 * The tracker sign-in callback (1.43.0): MyAnimeList sends the browser back to `/trackers/callback/<slug>?code=..&state=..`
 * (or `?error=..&state=..` when the user said no). The code and the state are one-use secrets: they are taken out of the address
 * bar with `history.replaceState` FIRST - when this module loads, which happens with `app.routes.ts` before the app starts and
 * before its first request - and kept in memory only until the callback page posts them. They are never logged or shown.
 */

/** What the tracker handed back, held until the callback page takes it. */
export interface TrackerCallbackParams {
  slug: string;
  code: string | null;
  state: string | null;
  /** The tracker's `error` (e.g. `access_denied`) - a fixed word, never a secret. */
  error: string | null;
}

let pending: TrackerCallbackParams | null = null;

/** The callback's query names; any of them in the address is removed. */
const CALLBACK_PARAMS = ['code', 'state', 'error', 'error_description', 'hint', 'message'];

/**
 * Moves the callback's parameters from the address bar into memory when the page is a tracker callback. Safe to call more than
 * once (the second call finds a clean address). Returns true when it stripped something.
 */
export function captureTrackerCallback(loc: Pick<Location, 'pathname' | 'search' | 'hash'> | undefined = globalThis.location,
  hist: Pick<History, 'replaceState' | 'state'> | undefined = globalThis.history): boolean {
  if (!loc || !hist || !loc.pathname.startsWith(TRACKER_CALLBACK_PATH)) return false;
  const params = new URLSearchParams(loc.search);
  if (!CALLBACK_PARAMS.some((p) => params.has(p))) return false;
  const slug = loc.pathname.slice(TRACKER_CALLBACK_PATH.length).split('/')[0] ?? '';
  pending = {
    slug,
    code: params.get('code'),
    state: params.get('state'),
    error: params.get('error'),
  };
  for (const p of CALLBACK_PARAMS) params.delete(p);
  const rest = params.toString();
  hist.replaceState(hist.state, '', loc.pathname + (rest ? `?${rest}` : '') + loc.hash);
  return true;
}

/** The parameters for `slug`, once: a second call (or another tracker's page) gets null. */
export function takeTrackerCallback(slug: string): TrackerCallbackParams | null {
  const p = pending;
  pending = null;
  return p && p.slug === slug ? p : null;
}

/**
 * Route guard of the callback page: when the router still sees callback parameters (an in-app navigation, or a load where the
 * module ran too late), they are captured and the page is entered again without them.
 */
export const trackerCallbackGuard: CanActivateFn = (route) => {
  const slug = route.paramMap.get('slug') ?? '';
  const q = route.queryParamMap;
  if (!CALLBACK_PARAMS.some((p) => q.has(p))) return true;
  pending = { slug, code: q.get('code'), state: q.get('state'), error: q.get('error') };
  globalThis.history?.replaceState(globalThis.history.state, '', TRACKER_CALLBACK_PATH + encodeURIComponent(slug));
  return inject(Router).parseUrl(TRACKER_CALLBACK_PATH + encodeURIComponent(slug));
};

// Runs once, when the routes are loaded (before bootstrap): the address bar is clean before anything renders or requests.
captureTrackerCallback();
