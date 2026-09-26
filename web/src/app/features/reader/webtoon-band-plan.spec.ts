import {
  planBands, webtoonEnhanceGate, deviceScale, bandHeightFor, bandRowsFor, bandCssHeight, poolSize,
  estimatePipelineBytes, estimateCanvasBytes, estimateBytes, haloRows, defaultBandRows, seamRows,
  minBandRows, maxBandRows, poolCap, bytesPerTilePixel, tileProfileFor,
} from './webtoon-band-plan';

const MB = 1e6;
const round1 = (bytes: number) => Math.round((bytes / MB) * 10) / 10;

describe('planBands', () => {
  const pages: [number, number][] = [[800, 1280], [800, 12000], [800, 433], [800, 432], [800, 431],
    [690, 1000], [1080, 2560], [800, 768], [800, 769], [800, 5000]];

  it.each(pages)('%ix%i: bands cover every page row, with no gaps', (w, h) => {
    const covered = new Uint8Array(h);
    for (const b of planBands(w, h)) {
      for (let y = b.bandY; y < b.bandY + Math.min(b.bandRows, h - b.bandY); y++) covered[y] = 1;
    }
    expect(covered.every(v => v === 1)).toBe(true);
  });

  it.each(pages)('%ix%i: tile windows stay inside the page and hold their band + seam', (w, h) => {
    for (const b of planBands(w, h)) {
      expect(b.tileY).toBeGreaterThanOrEqual(0);
      expect(b.tileY + b.tileRows).toBeLessThanOrEqual(h);
      expect(b.cropY).toBe(b.bandY - b.tileY);
      expect(b.cropY + b.drawRows).toBeLessThanOrEqual(b.tileRows);
      expect(b.drawRows).toBeGreaterThanOrEqual(Math.min(b.bandRows, h - b.bandY));
      expect(b.bandY + b.drawRows).toBeLessThanOrEqual(h);
    }
  });

  it('every tile of a page taller than one tile has the same size (one pipeline key)', () => {
    const bands = planBands(800, 12000);
    expect(bands.length).toBe(Math.ceil(12000 / defaultBandRows));
    expect(new Set(bands.map(b => b.tileRows))).toEqual(new Set([defaultBandRows + 2 * haloRows]));
    expect(new Set(bands.map(b => b.bandRows))).toEqual(new Set([defaultBandRows]));
  });

  it('the last band is shifted up to end at the page bottom and overlaps its predecessor', () => {
    const bands = planBands(800, 1000);
    const last = bands[bands.length - 1];
    expect(last.bandY + last.bandRows).toBe(1000);
    expect(last.bandY).toBeLessThan(bands[bands.length - 2].bandY + defaultBandRows);
    expect(last.drawRows).toBe(defaultBandRows); // nothing below it to seam into
  });

  it('crop offsets: top band starts the tile, middle bands sit one halo in, the bottom band is clamped', () => {
    const bands = planBands(800, 2000);
    expect(bands[0]).toMatchObject({ bandY: 0, tileY: 0, cropY: 0, drawRows: defaultBandRows + seamRows });
    expect(bands[2]).toMatchObject({ bandY: 768, tileY: 768 - haloRows, cropY: haloRows });
    const last = bands[bands.length - 1];
    expect(last).toMatchObject({ bandY: 2000 - 384, tileY: 2000 - 432, cropY: 432 - 384 });
  });

  it('a page no taller than one tile is a single whole-page tile (the second pipeline key)', () => {
    expect(planBands(800, 432)).toEqual([{ index: 0, bandY: 0, bandRows: 432, tileY: 0, tileRows: 432, cropY: 0, drawRows: 432 }]);
    expect(planBands(800, 300)).toHaveLength(1);
    expect(planBands(800, 433).length).toBe(2);
  });

  it('degenerate input plans nothing', () => {
    expect(planBands(0, 1000)).toEqual([]);
    expect(planBands(800, 0)).toEqual([]);
    expect(planBands(NaN, 1000)).toEqual([]);
    expect(planBands(800, 1000, 0)).toEqual([]);
  });
});

describe('webtoonEnhanceGate (per page, device pixels)', () => {
  // cssW = viewport CSS width * slider%, device px = cssW * dpr.
  it('Android DPR 3 at 100% (412 CSS): an 800 source is enhanced (s ~1.55)', () => {
    expect(deviceScale(412, 3, 800)).toBeCloseTo(1.545, 2);
    expect(webtoonEnhanceGate(412, 3, 800)).toBe(true);
  });

  it('Android: a 1350 bucket delivery is not enhanced (s ~0.92, the server already covers it)', () => {
    expect(webtoonEnhanceGate(412, 3, 1350)).toBe(false);
  });

  it('the 1.2 boundary is exclusive', () => {
    expect(webtoonEnhanceGate(960, 1, 800)).toBe(false); // exactly 1.2
    expect(webtoonEnhanceGate(961, 1, 800)).toBe(true);
  });

  it.each([
    // dpr, viewport CSS width, slider %, natural width, expected
    [1, 3840, 15, 800, false],
    [1, 3840, 40, 800, true],
    [1, 3840, 40, 1350, false],
    [1, 3840, 100, 1080, true],
    [2, 1024, 15, 690, false],
    [2, 1024, 40, 690, false],  // s 1.19, just under
    [2, 1024, 45, 690, true],
    [2, 1024, 100, 800, true],   // owner's iPad Air 13" M4 portrait: s 2.56
    [2, 1024, 100, 1350, true],  // s 1.52
    [3, 412, 15, 800, false],
    [3, 412, 40, 800, false],    // s 0.62
    [3, 412, 100, 1080, false],  // s 1.14
    [3, 412, 100, 690, true],
  ])('dpr %i, viewport %i CSS, slider %i pct, natural %i -> %s', (dpr, vw, pct, nw, expected) => {
    expect(webtoonEnhanceGate((vw * pct) / 100, dpr, nw)).toBe(expected);
  });

  it('a bad devicePixelRatio reads as 1 and an unmeasured page is never enhanced', () => {
    expect(deviceScale(1000, 0, 800)).toBe(1.25);
    expect(deviceScale(1000, NaN, 800)).toBe(1.25);
    expect(webtoonEnhanceGate(0, 2, 800)).toBe(false);
    expect(webtoonEnhanceGate(500, 2, 0)).toBe(false);
  });
});

describe('band height and pool size', () => {
  it('bandHeightFor follows the budget formula, snapped to 32 and clamped to [256, 768]', () => {
    // M, 800 wide, 96 MB: tileRows 526 -> (526 - 48) / 32 = 14.9 -> 448
    expect(bandHeightFor(800, 228, 96e6)).toBe(448);
    // VL, 800 wide, 160 MB: tileRows 537 -> 489 -> 480
    expect(bandHeightFor(800, 372, 160e6)).toBe(480);
    expect(bandHeightFor(800, 228, 1e12)).toBe(maxBandRows);
    expect(bandHeightFor(4000, 372, 96e6)).toBe(minBandRows);
    expect(bandHeightFor(0, 228, 96e6)).toBe(minBandRows);
  });

  it('bandRowsFor keeps the design default for typical sources and shrinks bands for wide ones', () => {
    expect(bandRowsFor(800, 'm', true)).toBe(defaultBandRows);
    expect(bandRowsFor(800, 'm', false)).toBe(defaultBandRows);
    expect(bandRowsFor(1600, 'm', true)).toBe(minBandRows); // 96 MB / (228 * 1600) = 263 rows
    expect(bandRowsFor(2000, 'm', false)).toBe(288); // 160 MB / (228 * 2000) = 350 rows
  });

  it('poolSize = ceil(2 * viewport / band) + 1, capped', () => {
    expect(poolSize(915, bandCssHeight(384, 800, 412))).toBe(11);   // Android
    expect(poolSize(1366, bandCssHeight(384, 800, 1024))).toBe(7);  // iPad Air 13" portrait
    expect(poolSize(1024, bandCssHeight(384, 800, 1366))).toBe(5);  // iPad Air 13" landscape
    expect(poolSize(4000, 50)).toBe(poolCap);
    expect(poolSize(0, 100)).toBe(poolCap);
  });
});

describe('estimateBytes reproduces the design tables', () => {
  it('tile pipeline table (Wn 800)', () => {
    expect(round1(estimatePipelineBytes(800, 304, 'vl'))).toBe(90.5);
    expect(round1(estimatePipelineBytes(800, 304, 'm'))).toBe(55.4);
    expect(round1(estimatePipelineBytes(800, 432, 'vl'))).toBe(128.6);
    expect(round1(estimatePipelineBytes(800, 432, 'm'))).toBe(78.8);
    expect(round1(estimatePipelineBytes(800, 576, 'vl'))).toBe(171.4);
    expect(round1(estimatePipelineBytes(800, 576, 'm'))).toBe(105.1);
  });

  it('one band canvas is 4.9 MB (1600 x 768 x 4)', () => {
    expect(round1(estimateCanvasBytes(800, 384))).toBe(4.9);
  });

  it.each([
    // label, viewport CSS h, column CSS w, bands, total M min-max, total VL min-max
    ['Desktop 4K slider 40%', 2000, 1536, 7, [113, 148], [163, 197]],
    ['Desktop 4K slider 100%', 2000, 3840, 4, [99, 118], [148, 168]],
    ['iPad 11" portrait', 1180, 820, 7, [113, 148], [163, 197]],
    ['iPad 11" landscape', 820, 1180, 4, [99, 118], [148, 168]],
    ['Android DPR 3', 915, 412, 11, [133, 187], [183, 237]],
    ['iPad Air 13" M4 portrait', 1366, 1024, 7, [113, 148], [163, 197]],
    ['iPad Air 13" M4 landscape', 1024, 1366, 5, [103, 128], [153, 178]],
    ['iPad Air 13" M4 portrait, 70% slider', 1366, 716.8, 9, [123, 167], [173, 217]],
  ] as const)('%s', (_label, vh, cssW, bands, m, vl) => {
    const em = estimateBytes({ nativeWidth: 800, bandRows: 384, chain: 'm', viewportCssHeight: vh, cssWidth: cssW });
    const ev = estimateBytes({ nativeWidth: 800, bandRows: 384, chain: 'vl', viewportCssHeight: vh, cssWidth: cssW });
    expect(em.bands).toBe(bands);
    // The design table rounds pipeline and canvases separately before adding: allow 1 MB.
    const near = (bytes: number, mb: number) => expect(Math.abs(bytes / MB - mb)).toBeLessThanOrEqual(1);
    near(em.totalMin, m[0]); near(em.totalMax, m[1]);
    near(ev.totalMin, vl[0]); near(ev.totalMax, vl[1]);
  });
});

/** 1.25.0: band tiles on the WebGL2 engines (Anime4K M as fragment shaders, FSR 1 Sharp). */
describe('WebGL2 tile profiles (1.25.0)', () => {
  it('bytes per tile pixel: gl-m 140 (124 of chain + the 2x drawing buffer), gl-sharp 36', () => {
    expect(bytesPerTilePixel['gl-m']).toBe(140);
    expect(bytesPerTilePixel['gl-sharp']).toBe(36);
    expect(bytesPerTilePixel.m).toBe(228);
  });

  it('tileProfileFor maps each backend', () => {
    expect(tileProfileFor({ mode: 'enhance', engine: 'webgpu' })).toBe('m');
    expect(tileProfileFor({ mode: 'enhance', engine: 'webgl2' })).toBe('gl-m');
    expect(tileProfileFor({ mode: 'sharp', engine: 'webgl2' })).toBe('gl-sharp');
  });

  it('lighter tiles let wide sources keep taller bands (fewer, cheaper band jobs)', () => {
    expect(bandRowsFor(3000, 'm', false)).toBe(256);
    expect(bandRowsFor(3000, 'gl-m', false)).toBe(320);
    expect(bandRowsFor(3000, 'gl-sharp', false)).toBe(defaultBandRows);
    expect(bandRowsFor(3000, 'gl-sharp', true)).toBe(defaultBandRows);
    // Typical strips (<= 800 wide) keep the design default on every engine.
    for (const profile of ['m', 'gl-m', 'gl-sharp'] as const) expect(bandRowsFor(800, profile, true)).toBe(defaultBandRows);
  });

  it('pipeline memory for an 800-wide strip tile: WebGPU M 78.8 MB, WebGL2 M 48.4 MB, Sharp 12.4 MB', () => {
    const tileRows = defaultBandRows + 2 * haloRows;
    expect(round1(estimatePipelineBytes(800, tileRows, 'm'))).toBe(78.8);
    expect(round1(estimatePipelineBytes(800, tileRows, 'gl-m'))).toBe(48.4);
    expect(round1(estimatePipelineBytes(800, tileRows, 'gl-sharp'))).toBe(12.4);
  });
});
