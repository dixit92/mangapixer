import { clampHighlightsGlsl, restoreCnnMGlsl, upscaleCnnX2MGlsl } from './anime4k-glsl-m';
import { evalSize, hookFragmentSource, parseHooks, planAnime4kChain, planBytes, presentFragmentSource } from './anime4k-webgl';
import { bytesPerTilePixel } from './webtoon-band-plan';
import { easuCon0, easuGlsl, rcasGlsl, rcasSharpness } from './fsr1-glsl';

/**
 * The pure half of WebGL2 Enhance: the vendored upstream files parse into the
 * expected passes, the mpv macros are generated, and the chain plan allocates
 * the memory the band planner budgets for, with no pass ever writing a texture
 * it reads.
 */
describe('vendored Anime4K M shaders', () => {
  it('keep their upstream MIT headers', () => {
    for (const src of [clampHighlightsGlsl, restoreCnnMGlsl, upscaleCnnX2MGlsl]) {
      expect(src.startsWith('// MIT License')).toBe(true);
      expect(src).toContain('Copyright (c) 2019-2021 bloc97');
    }
  });

  it('parse into 3 clamp, 8 restore and 9 x2 passes with their directives', () => {
    const clamp = parseHooks(clampHighlightsGlsl);
    const restore = parseHooks(restoreCnnMGlsl);
    const x2 = parseHooks(upscaleCnnX2MGlsl);
    expect(clamp.map((p) => p.save)).toEqual(['STATSMAX', 'STATSMAX', null]);
    expect(restore.length).toBe(8);
    expect(x2.length).toBe(9);
    expect(restore[0]).toMatchObject({ desc: 'Anime4K-v4.0-Restore-CNN-(M)-Conv-4x3x3x3', binds: ['MAIN'], save: 'conv2d_tf', width: 'MAIN.w' });
    expect(restore[7].binds).toEqual(['MAIN', 'conv2d_tf', 'conv2d_1_tf', 'conv2d_2_tf', 'conv2d_3_tf', 'conv2d_4_tf', 'conv2d_5_tf', 'conv2d_6_tf']);
    expect(restore[7].save).toBe('MAIN');
    expect(x2[8]).toMatchObject({ desc: 'Anime4K-v3.2-Upscale-CNN-x2-(M)-Depth-to-Space', width: 'conv2d_last_tf.w 2 *' });
    for (const p of [...restore, ...x2]) expect(p.body).toMatch(/vec4 hook\(\)/);
    // The license header is not part of any pass.
    expect(restore[0].body).not.toContain('MIT License');
  });
});

describe('evalSize', () => {
  const sizes = (name: string) => ({ MAIN: { width: 300, height: 400 }, conv2d_last_tf: { width: 300, height: 400 } }[name]!);
  it('evaluates mpv RPN size expressions', () => {
    expect(evalSize('MAIN.w', sizes)).toBe(300);
    expect(evalSize('conv2d_last_tf.h 2 *', sizes)).toBe(800);
    expect(evalSize('MAIN.w 2 / 1 +', sizes)).toBe(151);
  });
  it('throws on a malformed expression', () => {
    expect(() => evalSize('MAIN.w *', sizes)).toThrow();
  });
});

describe('hookFragmentSource', () => {
  it('wraps the upstream body in GLSL ES 3.00 with mpv texture macros per binding', () => {
    const src = hookFragmentSource(parseHooks(restoreCnnMGlsl)[0]);
    expect(src.startsWith('#version 300 es\n')).toBe(true);
    expect(src).toContain('uniform sampler2D MAIN_raw;');
    expect(src).toContain('#define MAIN_texOff(off) MAIN_tex(MAIN_pos + MAIN_pt * (off))');
    expect(src).toContain('#define MAIN_pos (gl_FragCoord.xy / mp_out_size)');
    expect(src).toContain('void main() { mp_frag = hook(); }');
    // The upstream weights are untouched.
    expect(src).toContain('mat4(-0.09991986, 0.13782342, -0.031251684, -0.06356843');
  });

  it('the present pass ports the De-Ring clamp and flips into the canvas', () => {
    expect(presentFragmentSource).toContain('min(current_luma, texture(stats, uv).x)');
    expect(presentFragmentSource).toContain('crop.x + (1.0 - f.y) * crop.y');
  });
});

describe('planAnime4kChain', () => {
  it('plans 2 + 8 + 9 draws with 13 textures for a 2x chain, the x2 stage reusing the restore stage\'s convs', () => {
    const plan = planAnime4kChain(100, 60, true);
    expect(plan.draws.length).toBe(19);
    expect(plan.textures.length).toBe(13);
    expect(plan.textures[plan.input]).toMatchObject({ width: 100, height: 60, float: false });
    expect(plan.textures[plan.output]).toMatchObject({ width: 200, height: 120, float: true });
    expect(plan.textures[plan.stats]).toMatchObject({ width: 100, height: 60 });
    // Restore draw k and x2 draw k (k = 0..6) write the same texture.
    for (let k = 0; k < 7; k++) expect(plan.draws[10 + k].output).toBe(plan.draws[2 + k].output);
  });

  it('never lets a pass write a texture it reads, nor clobber a texture a later pass still needs', () => {
    for (const x2 of [false, true]) {
      const plan = planAnime4kChain(64, 64, x2);
      plan.draws.forEach((draw, i) => {
        expect(draw.inputs.map((input) => input.texture)).not.toContain(draw.output);
        // Every input is the LAST value written to that texture before this pass.
        for (const input of draw.inputs) {
          const lastWriter = plan.draws.slice(0, i).map((d, j) => ({ d, j })).filter(({ d }) => d.output === input.texture).pop();
          if (input.texture === plan.input) expect(lastWriter).toBeUndefined();
          else expect(lastWriter, `${draw.program} reads ${input.name}`).toBeDefined();
        }
      });
      // The statistics survive until the present pass.
      expect(plan.draws.slice(2).some((d) => d.output === plan.stats)).toBe(false);
    }
  });

  it('restores only (10 draws) without x2', () => {
    const plan = planAnime4kChain(100, 60, false);
    expect(plan.draws.length).toBe(10);
    expect(plan.textures[plan.output]).toMatchObject({ width: 100, height: 60 });
  });

  it('allocates 124 B per input pixel; with the 2x drawing buffer that is the band planner\'s gl-m 140', () => {
    const plan = planAnime4kChain(600, 432, true);
    expect(planBytes(plan)).toBe(124 * 600 * 432);
    expect(124 + 16).toBe(bytesPerTilePixel['gl-m']);
  });
});

describe('FSR 1 port', () => {
  it('keeps the reference constants and approximations', () => {
    expect(easuGlsl).toContain('0x7ef07ebbu'); // APrxLoRcpF1
    expect(easuGlsl).toContain('0x5f347d74u'); // APrxLoRsqF1
    expect(rcasGlsl).toContain('0x7ef19fffu'); // APrxMedRcpF1
    expect(rcasGlsl).toContain('(0.25 - (1.0 / 16.0))'); // FSR_RCAS_LIMIT
    expect(easuGlsl).toContain('(1.0 / 32768.0)');
  });

  it('FsrEasuCon con0 and FsrRcasCon', () => {
    expect(easuCon0(100, 50, 250, 125)).toEqual([0.4, 0.4, 0.5 * 0.4 - 0.5, 0.5 * 0.4 - 0.5]);
    expect(rcasSharpness(0)).toBe(1);
    expect(rcasSharpness(1)).toBe(0.5);
  });
});
