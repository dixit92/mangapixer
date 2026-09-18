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
