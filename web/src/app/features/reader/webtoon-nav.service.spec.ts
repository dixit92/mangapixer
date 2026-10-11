import { TestBed } from '@angular/core/testing';

import {
  WebtoonNavPreferencesService, WEBTOON_TAP_STEPS, WebtoonForwardEdge, webtoonTapZone, webtoonScrollTarget, webtoonSwipeStep,
} from './webtoon-nav.service';

/**
 * 1.11.0 webtoon tap-to-scroll: the pure zone / step maths and the per-device
 * preference. The reader-level behaviour (taps, swipes, reduced motion) is
 * covered through `ReaderComponent`'s public handlers in reader.component.spec
 * and webtoon-edge-zones.spec (1.41.0 edges).
 */
describe('webtoonTapZone', () => {
  it('splits the viewport by thirds: top = back, middle = toggle, bottom = forward (centre column)', () => {
    for (const edge of ['right', 'left'] as const) {
      expect(webtoonTapZone(0.5, 0, edge)).toBe('back');
      expect(webtoonTapZone(0.5, 0.33, edge)).toBe('back');
      expect(webtoonTapZone(0.5, 1 / 3, edge)).toBe('toggle');
      expect(webtoonTapZone(0.5, 0.5, edge)).toBe('toggle');
      expect(webtoonTapZone(0.5, 0.66, edge)).toBe('toggle');
      expect(webtoonTapZone(0.5, 2 / 3, edge)).toBe('forward');
      expect(webtoonTapZone(0.5, 1, edge)).toBe('forward');
    }
  });

  /**
   * 1.41.0 (owner 2026-10-11): every cell of the 3 x 3 grid - top and bottom bands keep the full width, the middle
   * band's 20 % side edges step a screen (which one goes forward is the setting), its centre toggles the controls.
   */
  const grid: Record<WebtoonForwardEdge, string[][]> = {
    //        left edge   centre     right edge
    right: [['back',    'back',    'back'],      // top band
            ['back',    'toggle',  'forward'],   // middle band
            ['forward', 'forward', 'forward']],  // bottom band
    left:  [['back',    'back',    'back'],
            ['forward', 'toggle',  'back'],
            ['forward', 'forward', 'forward']],
  };
  for (const edge of ['right', 'left'] as const) {
    it(`"${edge === 'right' ? 'Right' : 'Left'} goes forward": every cell of the grid`, () => {
      const xs = [0.1, 0.5, 0.9];
      const ys = [1 / 6, 0.5, 5 / 6];
      ys.forEach((y, row) => xs.forEach((x, col) => {
        expect(webtoonTapZone(x, y, edge), `x ${x}, y ${y.toFixed(2)}`).toBe(grid[edge][row][col]);
      }));
    });

    it(`"${edge}": the four corners follow their band, never the edge`, () => {
      expect(webtoonTapZone(0, 0, edge)).toBe('back');
      expect(webtoonTapZone(1, 0, edge)).toBe('back');
      expect(webtoonTapZone(0, 1, edge)).toBe('forward');
      expect(webtoonTapZone(1, 1, edge)).toBe('forward');
      // The band's last pixel above / first pixel below the middle band, right at a side.
      expect(webtoonTapZone(0.05, 0.3333, edge)).toBe('back');
      expect(webtoonTapZone(0.95, 0.3333, edge)).toBe('back');
      expect(webtoonTapZone(0.05, 2 / 3, edge)).toBe('forward');
      expect(webtoonTapZone(0.95, 2 / 3, edge)).toBe('forward');
    });
  }

  it('the exact boundaries: 20 % and 80 % of the width, a third and two thirds of the height', () => {
    // Left edge: [0, 0.2); centre: [0.2, 0.8); right edge: [0.8, 1].
    expect(webtoonTapZone(0, 0.5, 'right')).toBe('back');
    expect(webtoonTapZone(0.1999, 0.5, 'right')).toBe('back');
    expect(webtoonTapZone(0.2, 0.5, 'right')).toBe('toggle');
    expect(webtoonTapZone(0.7999, 0.5, 'right')).toBe('toggle');
    expect(webtoonTapZone(0.8, 0.5, 'right')).toBe('forward');
    expect(webtoonTapZone(1, 0.5, 'right')).toBe('forward');
    expect(webtoonTapZone(0.1999, 0.5, 'left')).toBe('forward');
    expect(webtoonTapZone(0.8, 0.5, 'left')).toBe('back');
    // The middle band holds y = 1/3 and stops just below y = 2/3, at the edges as in the centre.
    expect(webtoonTapZone(0.1, 1 / 3, 'left')).toBe('forward');
    expect(webtoonTapZone(0.1, 0.6666, 'left')).toBe('forward');
    expect(webtoonTapZone(0.9, 1 / 3, 'right')).toBe('forward');
    expect(webtoonTapZone(0.9, 0.6666, 'left')).toBe('back');
  });
});

describe('webtoonSwipeStep', () => {
  it('the finger moves away from the forward edge: Right goes forward = swipe left forward, Left goes forward = swipe right', () => {
    expect(webtoonSwipeStep('leftward', 'right')).toBe(1);
    expect(webtoonSwipeStep('rightward', 'right')).toBe(-1);
    expect(webtoonSwipeStep('rightward', 'left')).toBe(1);
    expect(webtoonSwipeStep('leftward', 'left')).toBe(-1);
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

describe('WebtoonNavPreferencesService forward edge (1.41.0)', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => localStorage.clear());

  it('defaults to "Right goes forward" when nothing is stored', () => {
    expect(TestBed.inject(WebtoonNavPreferencesService).forwardEdge()).toBe('right');
  });

  it('stores the choice per device and reads it back on the next instance', () => {
    const s = TestBed.inject(WebtoonNavPreferencesService);
    s.setForwardEdge('left');
    expect(s.forwardEdge()).toBe('left');
    expect(localStorage.getItem(WebtoonNavPreferencesService.ForwardEdgeKey)).toBe('left');
    TestBed.resetTestingModule();
    expect(TestBed.inject(WebtoonNavPreferencesService).forwardEdge()).toBe('left');
    TestBed.inject(WebtoonNavPreferencesService).setForwardEdge('right');
    expect(localStorage.getItem(WebtoonNavPreferencesService.ForwardEdgeKey)).toBe('right');
  });

  it('ignores a stored value outside the two choices', () => {
    localStorage.setItem(WebtoonNavPreferencesService.ForwardEdgeKey, 'rtl');
    expect(TestBed.inject(WebtoonNavPreferencesService).forwardEdge()).toBe('right');
  });

  it('is independent of the tap step (Off keeps the stored edge for when taps come back)', () => {
    const s = TestBed.inject(WebtoonNavPreferencesService);
    s.setForwardEdge('left');
    s.setTapStep(0);
    s.setTapStep(80);
    expect(s.forwardEdge()).toBe('left');
  });
});
