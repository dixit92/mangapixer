/**
 * A fake WebGL2 context for jsdom specs (test-only; imported by specs, never by
 * app code). Just enough of `WebGL2RenderingContext` for `webgl-upscaler.ts` and
 * the `upscale-engine.ts` probe, recording every texture and framebuffer so a
 * spec can assert they are all deleted, every draw (with the pass it ran), and
 * failing loudly (`violations`) when a deleted object is used or a draw would be
 * a feedback loop (the target texture also bound to a sampler the program reads).
 */

export interface FakeGlOptions {
  /** `EXT_color_buffer_float` exposed (Enhance's RGBA16F targets). Default true. */
  floatTargets?: boolean;
  maxTextureSize?: number;
  /** Fragment shaders whose source matches fail to compile. */
  failCompile?: RegExp;
}

export class FakeGlObject {
  deleted = false;
  constructor(readonly kind: string, readonly id: number) {}
}

export class FakeGlTexture extends FakeGlObject {
  width = 0;
  height = 0;
  format = 0;
  uploads = 0;
}

export class FakeGlFramebuffer extends FakeGlObject {
  texture: FakeGlTexture | null = null;
}

class FakeShader extends FakeGlObject {
  source = '';
  constructor(id: number, readonly type: number) { super('shader', id); }
}

class FakeProgram extends FakeGlObject {
  fragment = '';
  /** Sampler uniform name -> texture unit. */
  readonly samplers = new Map<string, number>();
  readonly uniforms = new Map<string, unknown>();
}

export interface FakeDraw {
  /** What ran: `easu`, `rcas`, `present`, or the Anime4K pass DESC. */
  readonly pass: string;
  /** Target texture (null = the canvas's drawing buffer). */
  readonly target: FakeGlTexture | null;
  readonly width: number;
  readonly height: number;
  readonly uniforms: ReadonlyMap<string, unknown>;
}

/** Which pass a fragment shader is (for draw records). */
function passOf(fragment: string): string {
  if (fragment.includes('FsrEasuTapF')) return 'easu';
  if (fragment.includes('FsrRcasLoadF')) return 'rcas';
  if (fragment.includes('uniform vec2 crop;')) return 'present';
  return /\/\/ (Anime4K[^\n]*)/.exec(fragment)?.[1] ?? 'unknown';
}

let nextId = 1;

/** Build a fake context; `canvas` is the element it belongs to (for context-loss events). */
export function createFakeGl(canvas: HTMLCanvasElement | null, options: FakeGlOptions = {}) {
  const floatTargets = options.floatTargets ?? true;
  const textures: FakeGlTexture[] = [];
  const framebuffers: FakeGlFramebuffer[] = [];
  const draws: FakeDraw[] = [];
  const violations: string[] = [];
  const units = new Map<number, FakeGlTexture | null>();
  let activeUnit = 0;
  let boundTexture: FakeGlTexture | null = null;
  let boundFramebuffer: FakeGlFramebuffer | null = null;
  let program: FakeProgram | null = null;
  let lost = false;
  let viewport = [0, 0, 0, 0];
  let extensions: string[] = [];

  const use = (o: FakeGlObject | null, what: string) => {
    if (o?.deleted) violations.push(`${what} uses deleted ${o.kind} #${o.id}`);
  };

  const gl = {
    // --- constants (values as in the real API) ---
    TEXTURE_2D: 0x0de1, RGBA: 0x1908, RGBA8: 0x8058, RGBA16F: 0x881a, UNSIGNED_BYTE: 0x1401,
    FRAMEBUFFER: 0x8d40, COLOR_ATTACHMENT0: 0x8ce0, FRAMEBUFFER_COMPLETE: 0x8cd5,
    TEXTURE_MIN_FILTER: 0x2801, TEXTURE_MAG_FILTER: 0x2800, TEXTURE_WRAP_S: 0x2802, TEXTURE_WRAP_T: 0x2803,
    LINEAR: 0x2601, CLAMP_TO_EDGE: 0x812f, VERTEX_SHADER: 0x8b31, FRAGMENT_SHADER: 0x8b30,
    COMPILE_STATUS: 0x8b81, LINK_STATUS: 0x8b82, TRIANGLES: 0x0004, TEXTURE0: 0x84c0, MAX_TEXTURE_SIZE: 0x0d33,
    SYNC_GPU_COMMANDS_COMPLETE: 0x9117, SYNC_STATUS: 0x9114, SIGNALED: 0x9119,
    UNPACK_FLIP_Y_WEBGL: 0x9240, UNPACK_PREMULTIPLY_ALPHA_WEBGL: 0x9241,

    canvas,
    isContextLost: () => lost,
    getExtension(name: string) {
      if ((name === 'EXT_color_buffer_float' || name === 'EXT_color_buffer_half_float') && !floatTargets) return null;
      extensions = [...extensions, name];
      if (name === 'WEBGL_lose_context') return { loseContext: () => { lost = true; } };
      return {};
    },
    getParameter: (p: number) => (p === 0x0d33 ? options.maxTextureSize ?? 8192 : 0),

    createTexture() { const t = new FakeGlTexture('texture', nextId++); textures.push(t); return t; },
    bindTexture(_target: number, t: FakeGlTexture | null) { use(t, 'bindTexture'); boundTexture = t; units.set(activeUnit, t); },
    activeTexture(unit: number) { activeUnit = unit - 0x84c0; },
    texStorage2D(_t: number, _levels: number, format: number, w: number, h: number) {
      if (!boundTexture) { violations.push('texStorage2D without a texture'); return; }
      // Storage is always fine; RENDERING to RGBA16F needs the extension (see checkFramebufferStatus).
      Object.assign(boundTexture, { width: w, height: h, format });
    },
    texParameteri() { /* recorded implicitly */ },
    texSubImage2D() { use(boundTexture, 'texSubImage2D'); if (boundTexture) boundTexture.uploads++; },
    pixelStorei() { /* noop */ },
    deleteTexture(t: FakeGlTexture | null) { if (t) { if (t.deleted) violations.push(`double delete texture #${t.id}`); t.deleted = true; } },

    createFramebuffer() { const f = new FakeGlFramebuffer('framebuffer', nextId++); framebuffers.push(f); return f; },
    bindFramebuffer(_t: number, f: FakeGlFramebuffer | null) { use(f, 'bindFramebuffer'); boundFramebuffer = f; },
    framebufferTexture2D(_t: number, _a: number, _tt: number, tex: FakeGlTexture) { if (boundFramebuffer) boundFramebuffer.texture = tex; },
    checkFramebufferStatus() {
      const tex = boundFramebuffer?.texture;
      if (tex?.format === 0x881a && !floatTargets) return 0x8cd6; // INCOMPLETE_ATTACHMENT
      return 0x8cd5;
    },
    deleteFramebuffer(f: FakeGlFramebuffer | null) { if (f) { if (f.deleted) violations.push(`double delete framebuffer #${f.id}`); f.deleted = true; } },

    createShader: (type: number) => new FakeShader(nextId++, type),
    shaderSource(s: FakeShader, src: string) { s.source = src; },
    compileShader() { /* noop */ },
    getShaderParameter: (s: FakeShader) => !(s.type === 0x8b30 && options.failCompile?.test(s.source)),
    getShaderInfoLog: () => 'ERROR: 0:1: fake compile error',
    deleteShader() { /* noop */ },
    createProgram: () => new FakeProgram('program', nextId++),
    attachShader(p: FakeProgram, s: FakeShader) { if (s.type === 0x8b30) p.fragment = s.source; },
    linkProgram() { /* noop */ },
    getProgramParameter: () => true,
    getProgramInfoLog: () => '',
    deleteProgram() { /* noop */ },
    getUniformLocation: (p: FakeProgram, name: string) => ({ program: p, name }),
    useProgram(p: FakeProgram) { program = p; },
    uniform1i(loc: { program: FakeProgram; name: string } | null, v: number) {
      if (!loc) return;
      if (/^(src|image|stats)$|_raw$/.test(loc.name)) loc.program.samplers.set(loc.name, v);
      else loc.program.uniforms.set(loc.name, v);
    },
    uniform1f(loc: { program: FakeProgram; name: string } | null, v: number) { loc?.program.uniforms.set(loc.name, v); },
    uniform2f(loc: { program: FakeProgram; name: string } | null, a: number, b: number) { loc?.program.uniforms.set(loc.name, [a, b]); },
    uniform2i(loc: { program: FakeProgram; name: string } | null, a: number, b: number) { loc?.program.uniforms.set(loc.name, [a, b]); },
    uniform4f(loc: { program: FakeProgram; name: string } | null, ...v: number[]) { loc?.program.uniforms.set(loc.name, v); },
    viewport(x: number, y: number, w: number, h: number) { viewport = [x, y, w, h]; },
    drawArrays() {
      if (!program) { violations.push('draw without a program'); return; }
      const target = boundFramebuffer?.texture ?? null;
      use(boundFramebuffer, 'draw');
      for (const [name, unit] of program.samplers) {
        const t = units.get(unit) ?? null;
        use(t, `draw sampler ${name}`);
        if (target && t === target) violations.push(`feedback loop: ${passOf(program.fragment)} samples its own target via ${name}`);
      }
      draws.push({ pass: passOf(program.fragment), target, width: viewport[2], height: viewport[3], uniforms: new Map(program.uniforms) });
    },

    fenceSync: () => ({}),
    flush() { /* noop */ },
    getSyncParameter: () => 0x9119,
    deleteSync() { /* noop */ },
  };

  return {
    gl: gl as unknown as WebGL2RenderingContext,
    textures, framebuffers, draws, violations,
    /** Textures + framebuffers not yet deleted. */
    live: () => [...textures, ...framebuffers].filter((o) => !o.deleted).length,
    /** Simulate a GPU reset: the context reports lost and the canvas fires `webglcontextlost`. */
    loseContext() {
      lost = true;
      canvas?.dispatchEvent(new Event('webglcontextlost'));
    },
  };
}

export type FakeGl = ReturnType<typeof createFakeGl>;

/** A fake 2d context recording what was drawn onto the caller's canvas. */
export function createFake2d(canvas: HTMLCanvasElement) {
  const drawn: { source: unknown; width: number; height: number }[] = [];
  return {
    canvas,
    drawn,
    drawImage(source: HTMLCanvasElement) { drawn.push({ source, width: canvas.width, height: canvas.height }); },
  };
}
