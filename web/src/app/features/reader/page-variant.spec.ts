import { targetMaxDim, variantLadder, withMaxDim } from './page-variant';

/**
 * The variant-targeting rule is pure, so it gets the exhaustive treatment: every
 * fit mode, both paired states, the ladder edges, the guard rails, and the exact
 * jsdom-vs-device `devicePixelRatio` cases the reader will hit in the wild.
 */
describe('targetMaxDim', () => {
  it('mirrors the server ladder', () => {
    expect(variantLadder).toEqual([1080, 1440, 2160]);
  });

  describe('fit-screen (viewport box)', () => {
    it('snaps a 1024x768 desktop viewport at dpr 1 up to the first bucket', () => {
      expect(targetMaxDim(1024, 768, 1, 'screen', false, 100)).toBe(1080);
    });

    it('uses the LONGEST edge of the box, not the width', () => {
      // Portrait phone: the height is what needs the pixels.
      expect(targetMaxDim(390, 844, 1, 'screen', false, 100)).toBe(1080);
    });

    it('multiplies by devicePixelRatio', () => {
      // 844 * 3 = 2532 > 2160 -> above the ladder -> full size.
      expect(targetMaxDim(390, 844, 3, 'screen', false, 100)).toBe(0);
      // 844 * 2 = 1688 -> next rung up.
      expect(targetMaxDim(390, 844, 2, 'screen', false, 100)).toBe(2160);
    });

    it('halves the width for a paired double-page spread', () => {
      // Paired: box is 1200x900; longest edge is still the (unhalved) height.
      expect(targetMaxDim(2400, 900, 1, 'screen', true, 100)).toBe(1440);
      // Unpaired the same viewport needs 2400 -> above the ladder.
      expect(targetMaxDim(2400, 900, 1, 'screen', false, 100)).toBe(0);
    });
  });

  describe('fit-height (viewport box, same rule as fit-screen)', () => {
    it('treats the viewport box exactly like fit-screen', () => {
      expect(targetMaxDim(1440, 1000, 1, 'height', false, 100))
        .toBe(targetMaxDim(1440, 1000, 1, 'screen', false, 100));
    });

    it('halves the width when paired', () => {
      // Paired box 1100x1000 -> 1100 -> the 1440 rung.
      expect(targetMaxDim(2200, 1000, 1, 'height', true, 100)).toBe(1440);
    });
  });

  describe('fit-width (unbounded height, aspect from the manifest)', () => {
    it('derives the height from the page aspect, ignoring the viewport height', () => {
      // 800 wide, aspect 1.5 -> painted 800x1200 -> 1200 -> 1440 bucket.
      expect(targetMaxDim(800, 200, 1, 'width', false, 100, 1.5)).toBe(1440);
    });

    it('falls back to a square box when the aspect is unknown', () => {
      expect(targetMaxDim(800, 200, 1, 'width', false, 100, 0)).toBe(1080);
    });

    it('halves the width when paired, which also halves the derived height', () => {
      // 1600 viewport -> 800 per page -> 800 * 1.5 = 1200 -> 1440.
      expect(targetMaxDim(1600, 200, 1, 'width', true, 100, 1.5)).toBe(1440);
    });

    it('goes full-size when the derived height exceeds the top bucket', () => {
      // 1500 * 1.5 = 2250 > 2160.
      expect(targetMaxDim(1500, 200, 1, 'width', false, 100, 1.5)).toBe(0);
    });
  });

  describe('original fit', () => {
    it('always asks for the full-size transcode', () => {
      expect(targetMaxDim(390, 844, 1, 'original', false, 100)).toBe(0);
      expect(targetMaxDim(390, 844, 3, 'original', true, 50, 1.5)).toBe(0);
      expect(targetMaxDim(100, 100, 1, 'original', false, 100, 1.5)).toBe(0);
    });
  });

  describe('webtoon (percentage of the viewport width)', () => {
    it('scales the box by the width percentage', () => {
      // 1000 * 70% = 700 wide, aspect 1.5 -> 1050 -> 1080.
      expect(targetMaxDim(1000, 800, 1, 'webtoon', false, 70, 1.5)).toBe(1080);
    });

    it('a narrower column needs a smaller variant', () => {
      // 1000 * 20% = 200 wide -> 300 tall -> 1080 (the lowest rung).
      expect(targetMaxDim(1000, 800, 1, 'webtoon', false, 20, 1.5)).toBe(1080);
    });

    it('a full-width column on a retina phone can exceed the ladder', () => {
      // 430 * 100% * 3 = 1290 wide, height 1290 * 1.5 = 1935 -> 2160.
      expect(targetMaxDim(430, 932, 3, 'webtoon', false, 100, 1.5)).toBe(2160);
      // A taller page (aspect 2.0) needs 2580 -> above the ladder.
      expect(targetMaxDim(430, 932, 3, 'webtoon', false, 100, 2.0)).toBe(0);
    });

    it('ignores the paired flag (there are no pairs in webtoon)', () => {
      expect(targetMaxDim(1000, 800, 1, 'webtoon', true, 70, 1.5))
        .toBe(targetMaxDim(1000, 800, 1, 'webtoon', false, 70, 1.5));
    });

    it('clamps a nonsense width percentage into 1..100', () => {
      expect(targetMaxDim(1000, 800, 1, 'webtoon', false, 0, 1.5)).toBe(1080);
      // Clamped to 100%: 1000 wide -> 1500 tall -> the 2160 rung.
      expect(targetMaxDim(1000, 800, 1, 'webtoon', false, 400, 1.5)).toBe(2160);
      expect(targetMaxDim(1000, 800, 1, 'webtoon', false, Number.NaN, 1.5)).toBe(2160);
    });
  });

  describe('ladder edges', () => {
    it('a box landing exactly on a rung takes that rung, not the next', () => {
      expect(targetMaxDim(1080, 500, 1, 'screen', false, 100)).toBe(1080);
      expect(targetMaxDim(1440, 500, 1, 'screen', false, 100)).toBe(1440);
      expect(targetMaxDim(2160, 500, 1, 'screen', false, 100)).toBe(2160);
    });

    it('one pixel over a rung snaps up to the next', () => {
      expect(targetMaxDim(1081, 500, 1, 'screen', false, 100)).toBe(1440);
      expect(targetMaxDim(1441, 500, 1, 'screen', false, 100)).toBe(2160);
    });

    it('one pixel over the top rung means full size', () => {
      expect(targetMaxDim(2161, 500, 1, 'screen', false, 100)).toBe(0);
    });

    it('rounds a fractional need UP before snapping', () => {
      // 1080 * 1.0001 = 1080.108 -> ceil 1081 -> next rung.
      expect(targetMaxDim(1080, 500, 1.0001, 'screen', false, 100)).toBe(1440);
    });
  });

  describe('guard rails', () => {
    it('an unmeasured viewport asks for the full-size transcode', () => {
      expect(targetMaxDim(0, 0, 1, 'screen', false, 100)).toBe(0);
      expect(targetMaxDim(0, 768, 1, 'screen', false, 100)).toBe(0);
      expect(targetMaxDim(1024, 0, 1, 'screen', false, 100)).toBe(0);
      expect(targetMaxDim(-5, -5, 1, 'screen', false, 100)).toBe(0);
    });

    it('a non-finite viewport asks for the full-size transcode', () => {
      expect(targetMaxDim(Number.NaN, 768, 1, 'screen', false, 100)).toBe(0);
      expect(targetMaxDim(1024, Number.POSITIVE_INFINITY, 1, 'screen', false, 100)).toBe(0);
    });

    it('a missing / bogus devicePixelRatio reads as 1 (jsdom safety)', () => {
      const expected = targetMaxDim(1024, 768, 1, 'screen', false, 100);
      expect(targetMaxDim(1024, 768, Number.NaN, 'screen', false, 100)).toBe(expected);
      expect(targetMaxDim(1024, 768, 0, 'screen', false, 100)).toBe(expected);
      expect(targetMaxDim(1024, 768, -2, 'screen', false, 100)).toBe(expected);
      expect(targetMaxDim(1024, 768, Number.POSITIVE_INFINITY, 'screen', false, 100)).toBe(expected);
    });

    it('a negative / non-finite page aspect reads as unknown', () => {
      const unknown = targetMaxDim(800, 200, 1, 'width', false, 100, 0);
      expect(targetMaxDim(800, 200, 1, 'width', false, 100, -1.5)).toBe(unknown);
      expect(targetMaxDim(800, 200, 1, 'width', false, 100, Number.NaN)).toBe(unknown);
    });

    it('never returns a value off the ladder', () => {
      for (const w of [1, 320, 390, 768, 1024, 1280, 1440, 1920, 2560, 3840]) {
        for (const dpr of [1, 1.5, 2, 3]) {
          for (const paired of [false, true]) {
            const n = targetMaxDim(w, Math.round(w * 0.6), dpr, 'screen', paired, 100);
            expect(n === 0 || variantLadder.includes(n)).toBe(true);
          }
        }
      }
    });
  });
});

describe('withMaxDim', () => {
  const base = '/api/v1/items/item-1/pages/p0';

  it('appends the query when a bucket was chosen', () => {
    expect(withMaxDim(base, 1080)).toBe(`${base}?maxDim=1080`);
    expect(withMaxDim(base, 2160)).toBe(`${base}?maxDim=2160`);
  });

  it('leaves the URL alone for 0 (full-size transcode)', () => {
    expect(withMaxDim(base, 0)).toBe(base);
  });

  it('leaves the URL alone for a negative / non-finite value', () => {
    expect(withMaxDim(base, -1)).toBe(base);
    expect(withMaxDim(base, Number.NaN)).toBe(base);
  });

  it('rounds a fractional bucket so the URL stays cache-stable', () => {
    expect(withMaxDim(base, 1080.4)).toBe(`${base}?maxDim=1080`);
  });

  it('returns an empty URL untouched', () => {
    expect(withMaxDim('', 1080)).toBe('');
  });
});
