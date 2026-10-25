import { TestBed } from '@angular/core/testing';

import {
  WebtoonNavPreferencesService, WEBTOON_TAP_STEPS, WebtoonEdgeFraction, WebtoonBandFraction, WebtoonForwardEdge, webtoonTapZone, webtoonScrollTarget, webtoonSwipeStep,
} from './webtoon-nav.service';

/**
 * 1.11.0 webtoon tap-to-scroll: the pure zone / step maths and the per-device
 * preference. The reader-level behaviour (taps, swipes, reduced motion) is
 * covered through `ReaderComponent`'s public handlers in reader.component.spec
 * and webtoon-edge-zones.spec (1.41.0 edges).
 */
describe('webtoonTapZone (1.48.0 geometry)', () => {
  it('names the proportions: 20 % full-height edges, 15 % bands in the centre column', () => {
    expect(WebtoonEdgeFraction).toBe(0.2);
    expect(WebtoonBandFraction).toBe(0.15);
  });

  it('the centre column: a short top band = back, a short bottom band = forward, the rest = toggle', () => {
    for (const edge of ['right', 'left'] as const) {
      expect(webtoonTapZone(0.5, 0, edge)).toBe('back');
      expect(webtoonTapZone(0.5, 0.14, edge)).toBe('back');
      expect(webtoonTapZone(0.5, 0.15, edge)).toBe('toggle');
      expect(webtoonTapZone(0.5, 0.3, edge)).toBe('toggle');
      expect(webtoonTapZone(0.5, 0.5, edge)).toBe('toggle');
      expect(webtoonTapZone(0.5, 0.7, edge)).toBe('toggle');
      expect(webtoonTapZone(0.5, 0.8499, edge)).toBe('toggle');
      expect(webtoonTapZone(0.5, 0.85, edge)).toBe('forward');
      expect(webtoonTapZone(0.5, 1, edge)).toBe('forward');
    }
  });

  /**
   * Every cell of the 3 x 5 grid (left edge / centre / right edge x top band, upper, middle, lower, bottom band): the
   * edges are full height, so only the centre column changes with the row.
   */
  const rows = [0.05, 0.25, 0.5, 0.75, 0.95];
  const grid: Record<WebtoonForwardEdge, string[][]> = {
    //        left edge   centre     right edge
    right: [['back',    'back',    'forward'],   // top band row
            ['back',    'toggle',  'forward'],
            ['back',    'toggle',  'forward'],
            ['back',    'toggle',  'forward'],
            ['back',    'forward', 'forward']],  // bottom band row
    left:  [['forward', 'back',    'back'],
            ['forward', 'toggle',  'back'],
            ['forward', 'toggle',  'back'],
            ['forward', 'toggle',  'back'],
            ['forward', 'forward', 'back']],
  };
  for (const edge of ['right', 'left'] as const) {
    it(`"${edge === 'right' ? 'Right' : 'Left'} goes forward": every cell of the grid`, () => {
      const xs = [0.1, 0.5, 0.9];
      rows.forEach((y, row) => xs.forEach((x, col) => {
        expect(webtoonTapZone(x, y, edge), `x ${x}, y ${y}`).toBe(grid[edge][row][col]);
      }));
    });

    it(`"${edge}": the four corners belong to the edges, never to a band`, () => {
      const leftEdge = edge === 'right' ? 'back' : 'forward';
      const rightEdge = edge === 'right' ? 'forward' : 'back';
      expect(webtoonTapZone(0, 0, edge)).toBe(leftEdge);
      expect(webtoonTapZone(0, 1, edge)).toBe(leftEdge);
      expect(webtoonTapZone(1, 0, edge)).toBe(rightEdge);
      expect(webtoonTapZone(1, 1, edge)).toBe(rightEdge);
      // Just inside the corner, at the band's height, still the edge.
      expect(webtoonTapZone(0.05, 0.05, edge)).toBe(leftEdge);
      expect(webtoonTapZone(0.95, 0.95, edge)).toBe(rightEdge);
    });
  }

  it('the exact boundaries: 20 % and 80 % of the width, 15 % and 85 % of the height', () => {
    // Left edge: [0, 0.2); centre: [0.2, 0.8); right edge: [0.8, 1] - the same at every height.
    for (const y of [0, 0.1, 0.5, 0.9, 1]) {
      expect(webtoonTapZone(0.1999, y, 'right'), `y ${y}`).toBe('back');
      expect(webtoonTapZone(0.8, y, 'right'), `y ${y}`).toBe('forward');
      expect(webtoonTapZone(0.1999, y, 'left'), `y ${y}`).toBe('forward');
      expect(webtoonTapZone(0.8, y, 'left'), `y ${y}`).toBe('back');
    }
    expect(webtoonTapZone(0.2, 0.5, 'right')).toBe('toggle');
    expect(webtoonTapZone(0.7999, 0.5, 'right')).toBe('toggle');
    // At x = 0.2 / 0.7999 the centre column's bands apply.
    expect(webtoonTapZone(0.2, 0, 'right')).toBe('back');
    expect(webtoonTapZone(0.7999, 1, 'right')).toBe('forward');
    expect(webtoonTapZone(0.2, 0.15, 'right')).toBe('toggle');
    expect(webtoonTapZone(0.2, 0.85, 'right')).toBe('forward');
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
