/**
 * iOS/iPadOS detection for the reader's fullscreen handling (IPAD-FULLSCREEN).
 *
 * iPadOS Safari's Fullscreen API paints a persistent system "close" ([X])
 * control over the top-left of the page and keeps the status bar visible;
 * the page has no way to hide either. The reader already has its own in-page
 * immersive mode (`isFullscreen` driving `.immersive` on the toolbar and nav
 * bar), so on Apple touch platforms `toggleFullscreen()` uses that instead of
 * calling `requestFullscreen()`.
 *
 * Detection has two cases:
 *  - iPhone/iPad/iPod report themselves plainly in `navigator.platform` and/or
 *    `navigator.userAgent` ("iPhone", "iPad", "iPod").
 *  - iPadOS Safari masquerades as a Mac (`navigator.platform === 'MacIntel'`,
 *    the same desktop-class UA as macOS Safari) to get desktop-site behavior,
 *    but a real Mac never reports touch points, so `maxTouchPoints > 1`
 *    disambiguates it from an actual Mac.
 *
 * Pure and DOM-free (takes the bits of `Navigator` it needs as a parameter)
 * so it is trivial to unit test and to call with the real `navigator` from
 * component code.
 */
export function isApplePlatformTouch(nav: Pick<Navigator, 'platform' | 'maxTouchPoints' | 'userAgent'>): boolean {
  const iosDeviceRe = /iP(hone|ad|od)/;
  if (iosDeviceRe.test(nav.platform) || iosDeviceRe.test(nav.userAgent)) return true;
  return nav.platform === 'MacIntel' && nav.maxTouchPoints > 1;
}

/**
 * True when the app is running with no browser chrome to hide in the first
 * place: installed as a standalone PWA/home-screen app (1.20.0 owner request).
 * Two independent signals, either one is enough:
 *  - the standard `display-mode: standalone` media feature, true once the app
 *    is launched from an OS-level install (most installed PWAs, incl. Android
 *    and desktop Chrome/Edge);
 *  - `navigator.standalone === true`, Safari's older, non-standard boolean for
 *    an iOS/iPadOS home-screen launch (`display-mode` there does not reliably
 *    report standalone on all versions, so this is the belt to that braces).
 *
 * Pure and DOM-free like `isApplePlatformTouch` above: takes the
 * window/navigator-like object it needs so it is trivial to unit test and to
 * call with the real `window` from component code. jsdom does not implement
 * `matchMedia` at all, so a missing `matchMedia` reads as "not standalone"
 * rather than throwing.
 *
 * The `navigator` sub-type carries `userAgent` alongside the non-standard
 * `standalone` flag purely so TypeScript sees it as structurally related to
 * the real (lib.dom) `Navigator` type `window.navigator` is typed as -
 * `standalone` alone would make it a "weak type" with nothing in common with
 * `Navigator` (which does not declare `standalone`), which TS refuses to
 * assign a real `Navigator` value to.
 */
export function isStandaloneDisplay(
  win: {
    matchMedia?: (query: string) => { matches: boolean };
    navigator?: { userAgent?: string; standalone?: boolean };
  },
): boolean {
  if (win.navigator?.standalone === true) return true;
  if (typeof win.matchMedia !== 'function') return false;
  return win.matchMedia('(display-mode: standalone)').matches;
}
