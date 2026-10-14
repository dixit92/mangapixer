import { vi } from 'vitest';

import {
  allDark, isBlankOutput, litPoints, minLitPoints, outputDarkMax, probeBytes, probeBytesPerRow, probeColumn, probeColumns,
  probeRow, probeRows, readOutputProbe, sampleSourceRows, setSourceSamplerForTests, sourceLitMin,
} from './blank-guard';
import { FakeBuffer, fakeImage } from './fake-webgpu.testing';

/**
 * The blank-output guard's decision (1.43.0): "the output is near black while the source is not". The GPU half
 * (probe pass + readback) is covered through both renderers' specs against the fake device; here the verdict, its
 * thresholds, the probe grid and the real CPU sampler (against a fake OffscreenCanvas).
 */

/** `n` probe points of one colour, the rest of another. */
function grid(n: number, colour: number[], rest: number[] = [0, 0, 0, 255]): Uint8Array {
  const out = new Uint8Array(probeBytes);
  for (let p = 0; p < probeColumns * probeRows; p++) out.set(p < n ? colour : rest, p * 4);
  return out;
}

const black = grid(0, [0, 0, 0, 255]);
const page = fakeImage(800, 1200);
const rows = { y: 0, height: 1200 };

describe('blank-guard', () => {
  afterEach(() => {
    setSourceSamplerForTests(null);
    vi.unstubAllGlobals();
  });

  it('the probe grid copies with a 256-byte bytesPerRow (WebGPU alignment) and holds 1024 points', () => {
    expect(probeBytesPerRow).toBe(256);
    expect(probeBytesPerRow % 256).toBe(0);
    expect(probeColumns * probeRows).toBe(1024);
    expect(probeBytes).toBe(4096);
  });

  it('blank = every output point near black AND enough lit source points', () => {
    setSourceSamplerForTests(() => grid(1024, [255, 255, 255, 255])); // a white page
    expect(isBlankOutput(black, page, rows)).toBe(true);
  });

  it('a genuinely black page with a black output is NOT blank (no false positive)', () => {
    setSourceSamplerForTests(() => grid(0, [0, 0, 0, 255]));
    expect(isBlankOutput(black, page, rows)).toBe(false);
    // Near black, a dark grey scan: still not lit.
    setSourceSamplerForTests(() => grid(1024, [sourceLitMin - 1, sourceLitMin - 1, sourceLitMin - 1, 255]));
    expect(isBlankOutput(black, page, rows)).toBe(false);
  });

  it(`a mostly black page needs at least ${minLitPoints} lit points of 1024 to trip`, () => {
    setSourceSamplerForTests(() => grid(minLitPoints - 1, [255, 255, 255, 255]));
    expect(isBlankOutput(black, page, rows)).toBe(false);
    setSourceSamplerForTests(() => grid(minLitPoints, [255, 255, 255, 255]));
    expect(isBlankOutput(black, page, rows)).toBe(true);
  });

  it('one output point above the dark threshold means the output is a picture: no source sampling at all', () => {
    const sampler = vi.fn(() => grid(1024, [255, 255, 255, 255]));
    setSourceSamplerForTests(sampler);
    expect(isBlankOutput(grid(1, [outputDarkMax + 1, 0, 0, 255]), page, rows)).toBe(false);
    expect(isBlankOutput(grid(1024, [128, 128, 128, 255]), page, rows)).toBe(false);
    expect(sampler).not.toHaveBeenCalled(); // the normal case costs no CPU work
    expect(isBlankOutput(grid(1024, [outputDarkMax, outputDarkMax, outputDarkMax, 255]), page, rows)).toBe(true);
    expect(sampler).toHaveBeenCalledTimes(1);
  });

  it('reads the colour bytes only: a coloured channel in either byte order is lit, alpha is ignored', () => {
    expect(allDark(grid(1, [0, 0, 200, 255]))).toBe(false); // red in bgra8unorm
    expect(allDark(grid(1024, [0, 0, 0, 0]))).toBe(true);
    expect(litPoints(grid(3, [0, 0, 200, 0]))).toBe(3);
  });

  it('anything it cannot decide is "not blank": unknown output, no sampler result', () => {
    setSourceSamplerForTests(() => grid(1024, [255, 255, 255, 255]));
    expect(isBlankOutput(null, page, rows)).toBe(false);
    expect(isBlankOutput(new Uint8Array(16), page, rows)).toBe(false); // short readback
    setSourceSamplerForTests(() => null);
    expect(isBlankOutput(black, page, rows)).toBe(false);
  });

  it('probe rows and columns sit at the centre of their cells, inside the shown rows', () => {
    const band = { y: 1000, height: 384 };
    expect(probeRow(0, band)).toBe(1000 + Math.floor(0.5 * 384 / probeRows));
    expect(probeRow(probeRows - 1, band)).toBe(1000 + Math.floor((probeRows - 0.5) * 384 / probeRows));
    expect(probeRow(probeRows - 1, band)).toBeLessThan(1000 + 384);
    expect(probeColumn(0, 800)).toBe(6);
    expect(probeColumn(probeColumns - 1, 800)).toBe(793);
    expect(probeRow(0, { y: 5, height: 1 })).toBe(5); // a 1-row band
  });

  it('the real sampler draws one 1-row strip per probe row from the <img> and picks the probe columns', () => {
    const draws: number[][] = [];
    let canvasWidth = 0;
    class FakeOffscreen {
      constructor(public width: number, public height: number) { canvasWidth = width; }
      getContext() {
        return {
          imageSmoothingEnabled: true,
          drawImage: (_src: unknown, ...args: number[]) => { draws.push(args); },
          getImageData: (_x: number, _y: number, w: number, h: number) => {
            const data = new Uint8ClampedArray(w * h * 4);
            // Lit exactly at the probe columns of row 3.
            for (let j = 0; j < probeColumns; j++) data.set([255, 255, 255, 255], (3 * w + probeColumn(j, w)) * 4);
            return { data };
          },
        };
      }
    }
    vi.stubGlobal('OffscreenCanvas', FakeOffscreen);
    const out = sampleSourceRows(fakeImage(800, 3000), { y: 1000, height: 384 })!;
    expect(canvasWidth).toBe(800);
    expect(draws.length).toBe(probeRows);
    expect(draws[0]).toEqual([0, probeRow(0, { y: 1000, height: 384 }), 800, 1, 0, 0, 800, 1]);
    expect(draws.every((d, i) => d[1] === probeRow(i, { y: 1000, height: 384 }) && d[5] === i)).toBe(true);
    expect(litPoints(out)).toBe(probeColumns); // exactly row 3's points
  });

  it('the real sampler answers null where it cannot read pixels (no OffscreenCanvas, tainted image)', () => {
    vi.stubGlobal('OffscreenCanvas', undefined);
    expect(sampleSourceRows(page, rows)).toBeNull();
    class Tainted {
      getContext() {
        return {
          drawImage: () => undefined,
          getImageData: () => { throw new DOMException('tainted', 'SecurityError'); },
        };
      }
    }
    vi.stubGlobal('OffscreenCanvas', Tainted);
    expect(sampleSourceRows(page, rows)).toBeNull();
  });

  it('a readback of a buffer destroyed while mapping is "unknown" (null), never a throw', async () => {
    vi.stubGlobal('GPUMapMode', { READ: 1 });
    const buffer = new FakeBuffer('probe', probeBytes);
    buffer.destroy();
    expect(await readOutputProbe({ target: {} as GPUTexture, buffer: buffer as unknown as GPUBuffer })).toBeNull();
    const live = new FakeBuffer('probe', probeBytes);
    live.data.fill(7);
    const read = await readOutputProbe({ target: {} as GPUTexture, buffer: live as unknown as GPUBuffer });
    expect(read?.length).toBe(probeBytes);
    expect(read?.[0]).toBe(7);
    expect(live.mapped).toBe(false); // unmapped again for the next render
  });
});
