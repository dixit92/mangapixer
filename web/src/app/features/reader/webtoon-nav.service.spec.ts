import { TestBed } from '@angular/core/testing';

import {
  WebtoonNavPreferencesService, WEBTOON_TAP_STEPS, webtoonTapZone, webtoonScrollTarget,
} from './webtoon-nav.service';

/**
 * 1.11.0 webtoon tap-to-scroll: the pure zone / step maths and the per-device
 * preference. The reader-level behaviour (taps, swipes, reduced motion) is
 * covered through `ReaderComponent`'s public handlers in reader.component.spec.
 */
describe('webtoonTapZone', () => {
  it('splits the viewport by thirds: top = back, middle = toggle, bottom = forward', () => {
    expect(webtoonTapZone(0)).toBe('back');
    expect(webtoonTapZone(0.33)).toBe('back');
    expect(webtoonTapZone(1 / 3)).toBe('toggle');
    expect(webtoonTapZone(0.5)).toBe('toggle');
    expect(webtoonTapZone(0.66)).toBe('toggle');
    expect(webtoonTapZone(2 / 3)).toBe('forward');
    expect(webtoonTapZone(1)).toBe('forward');
  });
});

describe('webtoonScrollTarget', () => {
  it('moves by the step percentage of the visible height, in either direction', () => {
    expect(webtoonScrollTarget(1000, 600, 5000, 90, 1)).toBe(1540);
    expect(webtoonScrollTarget(1000, 600, 5000, 90, -1)).toBe(460);
    expect(webtoonScrollTarget(1000, 600, 5000, 100, 1)).toBe(1600);
    expect(webtoonScrollTarget(1000, 600, 5000, 80, 1)).toBe(1480);
  });

  it('clamps to the strip: never above 0, never past scrollHeight - clientHeight', () => {
    expect(webtoonScrollTarget(200, 600, 5000, 90, -1)).toBe(0);
    expect(webtoonScrollTarget(4200, 600, 5000, 90, 1)).toBe(4400);
    expect(webtoonScrollTarget(0, 600, 300, 90, 1)).toBe(0); // strip shorter than the viewport
  });

  it('rounds to whole pixels', () => {
    expect(webtoonScrollTarget(0, 601, 5000, 90, 1)).toBe(541); // 540.9 -> 541
  });
});

describe('WebtoonNavPreferencesService', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => localStorage.clear());

  it('defaults to 90% (tap zones on) when nothing is stored', () => {
    const s = TestBed.inject(WebtoonNavPreferencesService);
    expect(s.tapStep()).toBe(90);
    expect(s.tapZonesEnabled()).toBe(true);
  });

  it('round-trips every legal step through localStorage and treats 0 as off', () => {
    const s = TestBed.inject(WebtoonNavPreferencesService);
    for (const step of WEBTOON_TAP_STEPS) {
      s.setTapStep(step);
      expect(localStorage.getItem(WebtoonNavPreferencesService.TapStepKey)).toBe(String(step));
      expect(s.tapStep()).toBe(step);
      expect(s.tapZonesEnabled()).toBe(step > 0);
    }
  });

  it('ignores a stored value outside the allowed set and falls back to the default', () => {
    localStorage.setItem(WebtoonNavPreferencesService.TapStepKey, '55');
    expect(TestBed.inject(WebtoonNavPreferencesService).tapStep()).toBe(90);
    TestBed.resetTestingModule();
    localStorage.setItem(WebtoonNavPreferencesService.TapStepKey, 'off');
    expect(TestBed.inject(WebtoonNavPreferencesService).tapStep()).toBe(90);
  });

  it('reads a stored choice back on the next instance (per device, survives reloads)', () => {
    localStorage.setItem(WebtoonNavPreferencesService.TapStepKey, '0');
    const s = TestBed.inject(WebtoonNavPreferencesService);
    expect(s.tapStep()).toBe(0);
    expect(s.tapZonesEnabled()).toBe(false);
  });
});
