/**
 * Enhance blank-output guard (1.43.0) - shared by the paged (`anime4k-renderer.ts`) and the webtoon
 * (`anime4k-tile-renderer.ts`) WebGPU renderers; imported only by them, so it lives in their lazy chunk.
 *
 * WebKit's WebGPU (iPad, HTTPS) now and then hands back an all-black chain output for a page or a band
 * (owner reports since 1.25.0; root cause unknown, no local repro). The blit forces alpha 1, so such a
 * frame paints an opaque black block over a page that is not black. The guard does not need to know why:
 *
 *  1. OUTPUT PROBE (every render, on the GPU): the renderer draws the SAME blit (same pipeline, bind group
 *     and crop) into a tiny private `probeColumns x probeRows` target and copies it into a mappable buffer,
 *     in the same submission as the last chain slice. `probeColumns * 4 = 256` bytes, so the copy's
 *     `bytesPerRow` is exactly the 256-byte alignment WebGPU requires. The target uses the canvas format
 *     (`bgra8unorm` or `rgba8unorm`); only the three colour bytes are read, whose order does not matter here.
 *  2. Only when EVERY probe pixel is near black (`outputDarkMax`), the SOURCE is sampled on the CPU at the
 *     same grid points: one 1-row `drawImage` per probe row from the page `<img>` (never from the bitmap the GPU
 *     got - if that bitmap is the broken part, the source check must not share its fault), then `getImageData`.
 *  3. Blank = output all near black AND at least `minLitPoints` source points lit (`sourceLitMin`). A genuinely
 *     black page or band (few or no lit points) is never blank, so it shows enhanced as before.
 *
 * Anything the guard cannot decide (no OffscreenCanvas, a tainted image, a validation error around the
 * probe) is "not blank": the frame shows as it did before 1.43.0. Nothing is logged, sent or stored; the
 * caller only counts the verdicts (`UpscaleSupportService.blankFrames`).
 */

/** Probe grid: 64 columns x 16 rows = 1024 points; 64 * 4 bytes = 256 = WebGPU's `bytesPerRow` alignment. */
export const probeColumns = 64;
export const probeRows = 16;
export const probeBytesPerRow = probeColumns * 4;
export const probeBytes = probeBytesPerRow * probeRows;

/** An output probe point is near black when its brightest colour channel is at most this (of 255). */
export const outputDarkMax = 24;
/** A source point is lit when its brightest colour channel is at least this (of 255). */
export const sourceLitMin = 64;
/** The source must have at least this many lit points (of 1024, ~1.6%) for a black output to count as blank. */
export const minLitPoints = 16;

/** The source rows a render shows, in natural pixels (full width). */
export interface SourceRows {
  readonly y: number;
  readonly height: number;
}

/** Samples `source` at the probe grid: `probeColumns * probeRows` RGBA pixels, or null when it cannot. */
export type SourceSampler = (source: HTMLImageElement, rows: SourceRows) => ArrayLike<number> | null;

/** Top source row of probe row `i` (the centre of its band of rows, matching the GPU probe's sample point). */
export function probeRow(i: number, rows: SourceRows): number {
  return rows.y + Math.min(rows.height - 1, Math.floor(((i + 0.5) * rows.height) / probeRows));
}

/** Source column of probe column `j`. */
export function probeColumn(j: number, width: number): number {
  return Math.min(width - 1, Math.floor(((j + 0.5) * width) / probeColumns));
}

/** The real sampler: an OffscreenCanvas `width x probeRows`, one 1-row draw per probe row. */
export function sampleSourceRows(source: HTMLImageElement, rows: SourceRows): Uint8ClampedArray | null {
  try {
    const width = source.naturalWidth;
    if (!(width > 0) || !(rows.height > 0) || typeof OffscreenCanvas !== 'function') return null;
    const canvas = new OffscreenCanvas(width, probeRows);
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    if (!ctx) return null;
    ctx.imageSmoothingEnabled = false;
    for (let i = 0; i < probeRows; i++) ctx.drawImage(source, 0, probeRow(i, rows), width, 1, 0, i, width, 1);
    const strip = ctx.getImageData(0, 0, width, probeRows).data; // throws on a tainted (cross-origin) image
    const out = new Uint8ClampedArray(probeColumns * probeRows * 4);
    for (let i = 0; i < probeRows; i++) {
      for (let j = 0; j < probeColumns; j++) {
        const from = (i * width + probeColumn(j, width)) * 4;
        out.set(strip.subarray(from, from + 4), (i * probeColumns + j) * 4);
      }
    }
    canvas.width = 0; // let WebKit drop the backing store now
    return out;
  } catch {
    return null;
  }
}

let sampler: SourceSampler = sampleSourceRows;

/** Test seam: replace the source sampler (jsdom has no 2d canvas); `null` restores the real one. */
export function setSourceSamplerForTests(fn: SourceSampler | null): void {
  sampler = fn ?? sampleSourceRows;
}

/** Brightest colour channel of pixel `p` (bytes 0..2; byte 3 is alpha in both rgba and bgra). */
function brightest(px: ArrayLike<number>, p: number): number {
  return Math.max(px[p], px[p + 1], px[p + 2]);
}

/** Every probe point is near black. */
export function allDark(output: ArrayLike<number>): boolean {
  for (let p = 0; p + 3 < output.length; p += 4) if (brightest(output, p) > outputDarkMax) return false;
  return true;
}

/** How many source points are lit. */
export function litPoints(source: ArrayLike<number>): number {
  let n = 0;
  for (let p = 0; p + 3 < source.length; p += 4) if (brightest(source, p) >= sourceLitMin) n++;
  return n;
}

/**
 * The verdict for one render: true = blank (do not show it). Samples the source only when the output is all
 * near black, so a normal render costs no CPU work here.
 */
export function isBlankOutput(output: ArrayLike<number> | null, source: HTMLImageElement, rows: SourceRows): boolean {
  if (!output || output.length < probeBytes || !allDark(output)) return false;
  const src = sampler(source, rows);
  return !!src && litPoints(src) >= minLitPoints;
}

/** The GPU half: a private probe target + a mappable readback buffer (owned by the caller's tracked device). */
export interface OutputProbe {
  readonly target: GPUTexture;
  readonly buffer: GPUBuffer;
}

/** Allocate the probe through `device` (pass the tracking device, so the pipeline's dispose frees it too). */
export function createOutputProbe(device: GPUDevice, format: GPUTextureFormat): OutputProbe {
  return {
    target: device.createTexture({
      label: 'mp-blank-probe',
      size: [probeColumns, probeRows, 1],
      format,
      usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC,
    }),
    buffer: device.createBuffer({
      label: 'mp-blank-probe-read',
      size: probeBytes,
      usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ,
    }),
  };
}

/** Encode the blit into the probe target and the copy into the readback buffer (after the chain's passes). */
export function encodeOutputProbe(
  encoder: GPUCommandEncoder, probe: OutputProbe, blit: GPURenderPipeline, bindGroup: GPUBindGroup,
): void {
  const pass = encoder.beginRenderPass({
    colorAttachments: [{
      view: probe.target.createView(),
      // theme-exempt: the guard's private probe target (never shown), cleared like the canvas the blit draws into.
      clearValue: { r: 0, g: 0, b: 0, a: 1 },
      loadOp: 'clear',
      storeOp: 'store',
    }],
  });
  pass.setPipeline(blit);
  pass.setBindGroup(0, bindGroup);
  pass.draw(3);
  pass.end();
  encoder.copyTextureToBuffer(
    { texture: probe.target },
    { buffer: probe.buffer, bytesPerRow: probeBytesPerRow, rowsPerImage: probeRows },
    [probeColumns, probeRows, 1],
  );
}

/** Map the readback buffer and copy the probe pixels out (after the submission). Never throws: null = unknown. */
export async function readOutputProbe(probe: OutputProbe): Promise<Uint8Array | null> {
  try {
    await probe.buffer.mapAsync(GPUMapMode.READ);
  } catch {
    return null; // destroyed (released / device lost) while mapping
  }
  try {
    return new Uint8Array(probe.buffer.getMappedRange()).slice();
  } catch {
    return null;
  } finally {
    try { probe.buffer.unmap(); } catch { /* already unmapped */ }
  }
}

/** Validation-error scope around the probe's submission (a probe that could not run is "unknown", never blank). */
export function pushProbeScope(device: GPUDevice): void {
  try { device.pushErrorScope?.('validation'); } catch { /* no error scopes */ }
}

export async function popProbeScope(device: GPUDevice): Promise<boolean> {
  try {
    return (await device.popErrorScope?.()) == null;
  } catch {
    return false;
  }
}
