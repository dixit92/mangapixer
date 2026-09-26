/**
 * Anime4K on WebGL2 (1.25.0): the PURE half - turns the vendored upstream mpv
 * hook shaders (`anime4k-glsl-m.ts`) into WebGL2 fragment shaders and plans the
 * chain's draws and textures. No GL calls here; `webgl-upscaler.ts` executes the
 * plan. Only that lazy module imports this file.
 *
 * mpv's hook format, as used by Anime4K: a file is a list of passes, each
 * introduced by `//!DESC` and followed by directives - `//!BIND NAME` (read a
 * texture), `//!SAVE NAME` (name the output), `//!WIDTH` / `//!HEIGHT` (an RPN
 * size expression such as `conv2d_last_tf.w 2 *`; default: the hooked texture's
 * size) - and a GLSL body defining `vec4 hook()`. For every bound texture NAME
 * the body may use `NAME_pos` (this output pixel, normalised), `NAME_size`,
 * `NAME_pt` (1 / size), `NAME_tex(pos)` and `NAME_texOff(offset in texels)`;
 * `HOOKED` is the hooked texture, which mpv ALSO exposes under the hook's name
 * (`//!HOOK MAIN` + `//!BIND HOOKED` lets the body read `MAIN_texOff`, as
 * Clamp_Highlights does). Those become the macros below over one `sampler2D` +
 * size uniform per binding, so the upstream bodies run unchanged.
 * `//!WHEN` is ignored: this module decides which stages run.
 *
 * The chain (Anime4K mode A's first half, M network):
 *  1. `Clamp_Highlights` statistics: two separable 5-tap max-luma passes over the
 *     INPUT (`STATSMAX`);
 *  2. `Restore_CNN_M`: 7 x (3x3 conv, 4 channels) + a 1x1 residual -> MAIN;
 *  3. `Upscale_CNN_x2_M` (only when enlarging): 7 conv + a 1x1 + depth-to-space
 *     onto a bilinear 2x of MAIN -> MAIN at 2x;
 *  4. the present pass (ours): resample MAIN to the canvas, apply the highlight
 *     clamp against `STATSMAX` there - where mpv runs it (a PREKERNEL hook, i.e.
 *     after every MAIN hook) - and flip into the bottom-up canvas.
 * Intermediates are signed (CReLU is applied on read), so every saved texture is
 * `RGBA16F`; only the input is `RGBA8`.
 */

import { clampHighlightsGlsl, restoreCnnMGlsl, upscaleCnnX2MGlsl } from './anime4k-glsl-m';

export interface HookPass {
  readonly desc: string;
  /** The hooked texture's name (`//!HOOK`, MAIN in every Anime4K pass). */
  readonly hook: string;
  readonly binds: readonly string[];
  readonly save: string | null;
  readonly width: string | null;
  readonly height: string | null;
  readonly body: string;
}

/** Split an mpv hook file into passes (text before the first `//!DESC` is the license header). */
export function parseHooks(source: string): HookPass[] {
  const passes: HookPass[] = [];
  let current: {
    desc: string; hook: string; binds: string[]; save: string | null; width: string | null; height: string | null; body: string[];
  } | null = null;
  const flush = () => {
    if (current) passes.push({ ...current, body: current.body.join('\n').trim() });
  };
  for (const line of source.split('\n')) {
    const directive = /^\/\/!(\w+)\s*(.*)$/.exec(line.trim());
    if (directive) {
      const [, key, value] = directive;
      if (key === 'DESC') {
        flush();
        current = { desc: value.trim(), hook: 'MAIN', binds: [], save: null, width: null, height: null, body: [] };
        continue;
      }
      if (!current) continue;
      if (key === 'HOOK') current.hook = value.trim();
      else if (key === 'BIND') current.binds.push(value.trim());
      else if (key === 'SAVE') current.save = value.trim();
      else if (key === 'WIDTH') current.width = value.trim();
      else if (key === 'HEIGHT') current.height = value.trim();
      continue; // COMPONENTS, WHEN: decided by the chain, not the file
    }
    current?.body.push(line);
  }
  flush();
  return passes;
}

export interface Size { readonly width: number; readonly height: number }

/** Evaluate an mpv RPN size expression (`NAME.w`, `NAME.h`, numbers, + - * /). */
export function evalSize(expr: string, sizeOf: (name: string) => Size): number {
  const stack: number[] = [];
  for (const token of expr.split(/\s+/).filter(Boolean)) {
    const ref = /^(\w+)\.(w|h|width|height)$/.exec(token);
    if (ref) {
      const size = sizeOf(ref[1]);
      stack.push(ref[2].startsWith('w') ? size.width : size.height);
    } else if ('+-*/'.includes(token) && token.length === 1) {
      const b = stack.pop() ?? NaN;
      const a = stack.pop() ?? NaN;
      stack.push(token === '+' ? a + b : token === '-' ? a - b : token === '*' ? a * b : a / b);
    } else {
      stack.push(Number(token));
    }
  }
  const value = stack.pop();
  if (value === undefined || !Number.isFinite(value) || stack.length > 0) throw new Error(`bad size expression: ${expr}`);
  return Math.round(value);
}

/**
 * The names a pass's body can read: its bindings, plus the hook name when it
 * binds HOOKED (mpv binds the hooked texture under both).
 */
export function passInputs(pass: HookPass): string[] {
  const names = [...new Set(pass.binds)];
  if (names.includes('HOOKED') && !names.includes(pass.hook)) names.push(pass.hook);
  return names;
}

/** Uniform carrying the output size (`NAME_pos` is `gl_FragCoord.xy / mp_out_size`). */
export const outSizeUniform = 'mp_out_size';

/** A WebGL2 fragment shader for one hook pass: the upstream body under mpv's texture macros. */
export function hookFragmentSource(pass: HookPass): string {
  const lines = [
    '#version 300 es',
    'precision highp float;',
    'precision highp int;',
    'precision highp sampler2D;',
    `uniform vec2 ${outSizeUniform};`,
    'out vec4 mp_frag;',
  ];
  for (const name of passInputs(pass)) {
    lines.push(
      `uniform sampler2D ${name}_raw;`,
      `uniform vec2 ${name}_size;`,
      `#define ${name}_pos (gl_FragCoord.xy / ${outSizeUniform})`,
      `#define ${name}_pt (vec2(1.0) / ${name}_size)`,
      `#define ${name}_tex(pos) texture(${name}_raw, pos)`,
      `#define ${name}_texOff(off) ${name}_tex(${name}_pos + ${name}_pt * (off))`,
    );
  }
  lines.push(`// ${pass.desc}`, pass.body, 'void main() { mp_frag = hook(); }', '');
  return lines.join('\n');
}

/**
 * The present pass: resample the chain's output onto the canvas (linear), apply
 * Anime4K's highlight clamp (the `Anime4K-v4.0-De-Ring-Clamp` pass of
 * `Anime4K_Clamp_Highlights.glsl`, same arithmetic) and flip into the canvas's
 * bottom-up rows. `crop` maps the canvas's 0..1 rows onto the source rows:
 * (offset, scale) - (0, 1) for a page, the band's rows for a webtoon tile.
 */
export const presentFragmentSource = /* glsl */ `#version 300 es
precision highp float;
precision highp sampler2D;
uniform sampler2D image;
uniform sampler2D stats;
uniform vec2 outSize;
uniform vec2 crop;
out vec4 frag;

float get_luma(vec4 rgba) {
  return dot(vec4(0.299, 0.587, 0.114, 0.0), rgba);
}

void main() {
  vec2 f = gl_FragCoord.xy / outSize;
  vec2 uv = vec2(f.x, crop.x + (1.0 - f.y) * crop.y);
  vec4 c = texture(image, uv);
  float current_luma = get_luma(c);
  float new_luma = min(current_luma, texture(stats, uv).x);
  c -= (current_luma - new_luma);
  frag = vec4(clamp(c.rgb, 0.0, 1.0), 1.0);
}
`;

export interface TexturePlan {
  readonly id: number;
  readonly label: string;
  readonly width: number;
  readonly height: number;
  /** `RGBA16F` (true) or `RGBA8` (false: the input only). */
  readonly float: boolean;
}

export interface DrawPlan {
  /** Program cache key (stable per upstream pass). */
  readonly program: string;
  readonly source: string;
  /** Uniform name prefix (the bound NAME) -> texture id. */
  readonly inputs: readonly { readonly name: string; readonly texture: number }[];
  readonly output: number;
  readonly width: number;
  readonly height: number;
}

export interface ChainPlan {
  readonly textures: readonly TexturePlan[];
  readonly draws: readonly DrawPlan[];
  readonly input: number;
  /** The final MAIN (native, or 2x when the chain upscales). */
  readonly output: number;
  readonly stats: number;
}

interface Stage { readonly key: string; readonly passes: readonly HookPass[] }

let stages: { stats: Stage; restore: Stage; upscale: Stage } | null = null;

function loadStages(): { stats: Stage; restore: Stage; upscale: Stage } {
  stages ??= {
    stats: { key: 'clamp', passes: parseHooks(clampHighlightsGlsl).filter((p) => p.save === 'STATSMAX') },
    restore: { key: 'restore-m', passes: parseHooks(restoreCnnMGlsl) },
    upscale: { key: 'x2-m', passes: parseHooks(upscaleCnnX2MGlsl) },
  };
  return stages;
}

/**
 * Plan the M chain for a `width x height` input: restore, plus one x2 when
 * `upscale`. Textures are keyed by (saved name, size), so the x2 stage reuses the
 * restore stage's seven conv textures (same names, same size, no longer needed);
 * a pass that saves a name it also reads (MAIN, STATSMAX) gets a fresh texture.
 */
export function planAnime4kChain(width: number, height: number, upscale: boolean): ChainPlan {
  const { stats, restore, upscale: x2 } = loadStages();
  const textures: TexturePlan[] = [];
  const byKey = new Map<string, number>();
  const current = new Map<string, number>();
  const alloc = (key: string, label: string, w: number, h: number, float: boolean): number => {
    const id = textures.length;
    textures.push({ id, label, width: w, height: h, float });
    byKey.set(key, id);
    return id;
  };
  const input = alloc(`MAIN@${width}x${height}`, 'input', width, height, false);
  current.set('MAIN', input);
  const sizeOf = (name: string): Size => {
    const id = current.get(name === 'HOOKED' ? 'MAIN' : name);
    if (id === undefined) throw new Error(`unbound texture ${name}`);
    return textures[id];
  };

  const draws: DrawPlan[] = [];
  const run = (stage: Stage) => {
    stage.passes.forEach((pass, index) => {
      const hooked = sizeOf('HOOKED');
      const w = pass.width ? evalSize(pass.width, sizeOf) : hooked.width;
      const h = pass.height ? evalSize(pass.height, sizeOf) : hooked.height;
      const inputs = passInputs(pass).map((name) => ({ name, texture: current.get(name === 'HOOKED' ? 'MAIN' : name) ?? -1 }));
      if (inputs.some((i) => i.texture < 0)) throw new Error(`unbound input in ${pass.desc}`);
      const save = pass.save ?? 'MAIN';
      let key = `${save}@${w}x${h}`;
      for (let n = 1; inputs.some((i) => i.texture === byKey.get(key)); n++) key = `${save}@${w}x${h}#${n}`;
      const output = byKey.get(key) ?? alloc(key, save, w, h, true);
      current.set(save, output);
      draws.push({ program: `${stage.key}:${index}`, source: hookFragmentSource(pass), inputs, output, width: w, height: h });
    });
  };
  run(stats);
  const statsTexture = current.get('STATSMAX')!;
  run(restore);
  if (upscale) run(x2);
  return { textures, draws, input, output: current.get('MAIN')!, stats: statsTexture };
}

/** GPU bytes a plan allocates (RGBA16F 8 B/px, RGBA8 4 B/px). */
export function planBytes(plan: ChainPlan): number {
  return plan.textures.reduce((sum, t) => sum + t.width * t.height * (t.float ? 8 : 4), 0);
}
