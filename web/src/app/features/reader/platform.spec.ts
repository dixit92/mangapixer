import { isApplePlatformTouch, isStandaloneDisplay } from './platform';

/**
 * Pure detection rule (IPAD-FULLSCREEN): true for real iOS/iPadOS devices and
 * for iPadOS Safari's Mac-masquerading UA (distinguished from a real Mac by
 * touch points), false for everything else.
 */
describe('isApplePlatformTouch', () => {
  it('is true for an iPhone platform string', () => {
    expect(isApplePlatformTouch({
      platform: 'iPhone', maxTouchPoints: 5,
      userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15',
    })).toBe(true);
  });

  it('is true for an iPad platform string', () => {
    expect(isApplePlatformTouch({
      platform: 'iPad', maxTouchPoints: 5,
      userAgent: 'Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15',
    })).toBe(true);
  });

  it('is true for iPadOS Safari masquerading as a Mac (MacIntel + multiple touch points)', () => {
    expect(isApplePlatformTouch({
      platform: 'MacIntel', maxTouchPoints: 5,
      userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15',
    })).toBe(true);
  });

  it('is false for a real Mac (MacIntel + no touch points)', () => {
    expect(isApplePlatformTouch({
      platform: 'MacIntel', maxTouchPoints: 0,
      userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15',
    })).toBe(false);
  });

  it('is false for Windows (Win32)', () => {
    expect(isApplePlatformTouch({
      platform: 'Win32', maxTouchPoints: 0,
      userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36',
    })).toBe(false);
  });

  it('is false for a Linux/Android UA (armv8l)', () => {
    expect(isApplePlatformTouch({
      platform: 'Linux armv8l', maxTouchPoints: 5,
      userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36',
    })).toBe(false);
  });
});

/**
 * Standalone/installed-app detection (1.20.0 owner request): true with no
 * browser chrome to hide in the first place, via either the standard
 * `display-mode: standalone` media feature or iOS Safari's older
 * `navigator.standalone` flag.
 */
describe('isStandaloneDisplay', () => {
  it('is true when the display-mode: standalone media feature matches', () => {
    expect(isStandaloneDisplay({
      matchMedia: (q) => ({ matches: q === '(display-mode: standalone)' }),
    })).toBe(true);
  });

  it('is true for iOS Safari\'s navigator.standalone, even without matchMedia', () => {
    expect(isStandaloneDisplay({ navigator: { standalone: true } })).toBe(true);
  });

  it('navigator.standalone wins even when matchMedia disagrees', () => {
    expect(isStandaloneDisplay({
      matchMedia: () => ({ matches: false }),
      navigator: { standalone: true },
    })).toBe(true);
  });

  it('is false in an ordinary browser tab (media feature does not match)', () => {
    expect(isStandaloneDisplay({
      matchMedia: (q) => ({ matches: q !== '(display-mode: standalone)' && false }),
      navigator: { standalone: false },
    })).toBe(false);
  });

  it('is false, not throwing, when matchMedia is missing (jsdom-safe)', () => {
    expect(isStandaloneDisplay({})).toBe(false);
    expect(isStandaloneDisplay({ navigator: { standalone: false } })).toBe(false);
  });
});
