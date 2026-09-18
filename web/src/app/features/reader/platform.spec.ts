import { isApplePlatformTouch } from './platform';

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
