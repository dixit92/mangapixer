/**
 * AMD FidelityFX Super Resolution 1 (FSR 1) for the "Rendering: Sharp" option
 * (1.25.0), as WebGL2 (GLSL ES 3.00) fragment shaders.
 *
 * PORTED from the reference header `ffx-fsr/ffx_fsr1.h` v1.20210629 of
 * https://github.com/GPUOpen-Effects/FidelityFX-FSR (and the three approximation
 * helpers it uses from `ffx_a.h`), the NON-PACKED 32-bit paths `FsrEasuF` and
 * `FsrRcasF`. MIT License, Copyright (c) 2021 Advanced Micro Devices, Inc. All
 * rights reserved - the full notice is in THIRD-PARTY-NOTICES.md ("Ported shader
 * code") and ships with the app.
 *
 * Differences from the reference, all mechanical:
 *  - WebGL2 has no `textureGather`, so EASU's four gathers become its twelve taps
 *    as `texelFetch` (clamped to the image), named b c e f g h i j k l n o exactly
 *    as in the reference's diagram. `FsrEasuCon`'s con1..con3 only positioned the
 *    gathers, so con0 is the only constant left.
 *  - RCAS reads the EASU output with an optional row offset (a webtoon band draws
 *    only its own rows of a taller tile) and writes into the canvas's default
 *    framebuffer, whose rows run bottom-up, so it flips `y`.
 *  - RCAS guards its two limiter divisions against 0/0 on flat pure black and
 *    pure white, where the reference relies on the GPU's NaN handling in `max`.
 *  - Output alpha is 1: the overlay must be opaque over the page.
 *  - `FSR_RCAS_DENOISE` and the passthrough-alpha variant are not used.
 *
 * Textures hold the image top row first (row 0 = top), in every pass but the
 * final canvas write.
 */

/** Full-screen triangle; no vertex buffers (WebGL2 `gl_VertexID`). */
export const fullscreenVertexGlsl = /* glsl */ `#version 300 es
void main() {
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
`;

/**
 * EASU: edge-adaptive spatial upsampling, `src` (input size) -> the bound
 * framebuffer (output size). `con0` = `FsrEasuCon`'s first constant:
 * (inW / outW, inH / outH, 0.5 * inW / outW - 0.5, 0.5 * inH / outH - 0.5).
 */
export const easuGlsl = /* glsl */ `#version 300 es
precision highp float;
precision highp int;
precision highp sampler2D;

uniform sampler2D src;
uniform vec4 con0;
out vec4 fragColor;

// ffx_a.h approximations (exact bit tricks of the reference).
float APrxLoRcpF1(float a) { return uintBitsToFloat(0x7ef07ebbu - floatBitsToUint(a)); }
float APrxLoRsqF1(float a) { return uintBitsToFloat(0x5f347d74u - (floatBitsToUint(a) >> 1u)); }

vec3 tapAt(ivec2 p) {
  return texelFetch(src, clamp(p, ivec2(0), textureSize(src, 0) - 1), 0).rgb;
}

// Filtering for a given tap for the scalar.
void FsrEasuTapF(inout vec3 aC, inout float aW, vec2 off, vec2 dir, vec2 len, float lob, float clp, vec3 c) {
  // Rotate offset by direction.
  vec2 v;
  v.x = (off.x * ( dir.x)) + (off.y * dir.y);
  v.y = (off.x * (-dir.y)) + (off.y * dir.x);
  // Anisotropy.
  v *= len;
  // Compute distance^2, limited to the window.
  float d2 = v.x * v.x + v.y * v.y;
  d2 = min(d2, clp);
  // Approximation of lancos2 without sin() or rcp(), or sqrt() to get x.
  float wB = (2.0 / 5.0) * d2 + -1.0;
  float wA = lob * d2 + -1.0;
  wB *= wB;
  wA *= wA;
  wB = (25.0 / 16.0) * wB + (-(25.0 / 16.0 - 1.0));
  float w = wB * wA;
  // Do weighted average.
  aC += c * w;
  aW += w;
}

// Accumulate direction and length ('w' is the bilinear weight of this quad).
void FsrEasuSetF(inout vec2 dir, inout float len, float w, float lA, float lB, float lC, float lD, float lE) {
  float dc = lD - lC;
  float cb = lC - lB;
  float lenX = max(abs(dc), abs(cb));
  lenX = APrxLoRcpF1(lenX);
  float dirX = lD - lB;
  dir.x += dirX * w;
  lenX = clamp(abs(dirX) * lenX, 0.0, 1.0);
  lenX *= lenX;
  len += lenX * w;
  // Repeat for the y axis.
  float ec = lE - lC;
  float ca = lC - lA;
  float lenY = max(abs(ec), abs(ca));
  lenY = APrxLoRcpF1(lenY);
  float dirY = lE - lA;
  dir.y += dirY * w;
  lenY = clamp(abs(dirY) * lenY, 0.0, 1.0);
  lenY *= lenY;
  len += lenY * w;
}

// Simplest multi-channel approximate luma possible (luma times 2).
float luma2(vec3 c) { return c.b * 0.5 + (c.r * 0.5 + c.g); }

void main() {
  // Get position of 'f'.
  vec2 pp = floor(gl_FragCoord.xy) * con0.xy + con0.zw;
  vec2 fp = floor(pp);
  pp -= fp;
  ivec2 F = ivec2(fp);
  // 12-tap kernel.
  //    b c
  //  e f g h
  //  i j k l
  //    n o
  vec3 b = tapAt(F + ivec2( 0, -1));
  vec3 c = tapAt(F + ivec2( 1, -1));
  vec3 e = tapAt(F + ivec2(-1,  0));
  vec3 f = tapAt(F);
  vec3 g = tapAt(F + ivec2( 1,  0));
  vec3 h = tapAt(F + ivec2( 2,  0));
  vec3 i = tapAt(F + ivec2(-1,  1));
  vec3 j = tapAt(F + ivec2( 0,  1));
  vec3 k = tapAt(F + ivec2( 1,  1));
  vec3 l = tapAt(F + ivec2( 2,  1));
  vec3 n = tapAt(F + ivec2( 0,  2));
  vec3 o = tapAt(F + ivec2( 1,  2));
  float bL = luma2(b); float cL = luma2(c); float eL = luma2(e); float fL = luma2(f);
  float gL = luma2(g); float hL = luma2(h); float iL = luma2(i); float jL = luma2(j);
  float kL = luma2(k); float lL = luma2(l); float nL = luma2(n); float oL = luma2(o);
  // Accumulate for bilinear interpolation.
  vec2 dir = vec2(0.0);
  float len = 0.0;
  FsrEasuSetF(dir, len, (1.0 - pp.x) * (1.0 - pp.y), bL, eL, fL, gL, jL);
  FsrEasuSetF(dir, len,        pp.x  * (1.0 - pp.y), cL, fL, gL, hL, kL);
  FsrEasuSetF(dir, len, (1.0 - pp.x) *        pp.y , fL, iL, jL, kL, nL);
  FsrEasuSetF(dir, len,        pp.x  *        pp.y , gL, jL, kL, lL, oL);
  // Normalize with approximation, and cleanup close to zero.
  vec2 dir2 = dir * dir;
  float dirR = dir2.x + dir2.y;
  bool zro = dirR < (1.0 / 32768.0);
  dirR = APrxLoRsqF1(dirR);
  dirR = zro ? 1.0 : dirR;
  dir.x = zro ? 1.0 : dir.x;
  dir *= vec2(dirR);
  // Transform from {0 to 2} to {0 to 1} range, and shape with square.
  len = len * 0.5;
  len *= len;
  // Stretch kernel {1.0 vert|horz, to sqrt(2.0) on diagonal}.
  float stretch = (dir.x * dir.x + dir.y * dir.y) * APrxLoRcpF1(max(abs(dir.x), abs(dir.y)));
  // Anisotropic length after rotation.
  vec2 len2 = vec2(1.0 + (stretch - 1.0) * len, 1.0 + -0.5 * len);
  // Based on the amount of 'edge', the window shifts from +/-{sqrt(2.0) to slightly beyond 2.0}.
  float lob = 0.5 + ((1.0 / 4.0 - 0.04) - 0.5) * len;
  // Set distance^2 clipping point to the end of the adjustable window.
  float clp = APrxLoRcpF1(lob);
  // Accumulation mixed with min/max of 4 nearest.
  vec3 min4 = min(min(f, g), min(j, k));
  vec3 max4 = max(max(f, g), max(j, k));
  vec3 aC = vec3(0.0);
  float aW = 0.0;
  FsrEasuTapF(aC, aW, vec2( 0.0, -1.0) - pp, dir, len2, lob, clp, b);
  FsrEasuTapF(aC, aW, vec2( 1.0, -1.0) - pp, dir, len2, lob, clp, c);
  FsrEasuTapF(aC, aW, vec2(-1.0,  1.0) - pp, dir, len2, lob, clp, i);
  FsrEasuTapF(aC, aW, vec2( 0.0,  1.0) - pp, dir, len2, lob, clp, j);
  FsrEasuTapF(aC, aW, vec2( 0.0,  0.0) - pp, dir, len2, lob, clp, f);
  FsrEasuTapF(aC, aW, vec2(-1.0,  0.0) - pp, dir, len2, lob, clp, e);
  FsrEasuTapF(aC, aW, vec2( 1.0,  1.0) - pp, dir, len2, lob, clp, k);
  FsrEasuTapF(aC, aW, vec2( 2.0,  1.0) - pp, dir, len2, lob, clp, l);
  FsrEasuTapF(aC, aW, vec2( 2.0,  0.0) - pp, dir, len2, lob, clp, h);
  FsrEasuTapF(aC, aW, vec2( 1.0,  0.0) - pp, dir, len2, lob, clp, g);
  FsrEasuTapF(aC, aW, vec2( 1.0,  2.0) - pp, dir, len2, lob, clp, o);
  FsrEasuTapF(aC, aW, vec2( 0.0,  2.0) - pp, dir, len2, lob, clp, n);
  // Normalize and dering.
  fragColor = vec4(min(max4, max(min4, aC * vec3(1.0 / aW))), 1.0);
}
`;

/** RCAS limit: "set at the limit of providing unnatural results for sharpening". */
const rcasLimit = '(0.25 - (1.0 / 16.0))';

/**
 * RCAS: robust contrast-adaptive sharpening of the EASU output (same size) into
 * the canvas. `sharpness` = `FsrRcasCon`'s con.x = exp2(-stops). `offset` shifts
 * the rows read (webtoon bands); `outHeight` flips into the bottom-up canvas.
 */
export const rcasGlsl = /* glsl */ `#version 300 es
precision highp float;
precision highp int;
precision highp sampler2D;

uniform sampler2D src;
uniform float sharpness;
uniform ivec2 offset;
uniform int outHeight;
out vec4 fragColor;

float APrxMedRcpF1(float a) { float b = uintBitsToFloat(0x7ef19fffu - floatBitsToUint(a)); return b * (-b * a + 2.0); }

vec3 FsrRcasLoadF(ivec2 p) {
  return texelFetch(src, clamp(p, ivec2(0), textureSize(src, 0) - 1), 0).rgb;
}

void main() {
  ivec2 ip = ivec2(gl_FragCoord.xy);
  ip.y = outHeight - 1 - ip.y;
  ivec2 sp = ip + offset;
  // Algorithm uses minimal 3x3 pixel neighborhood.
  //    b
  //  d e f
  //    h
  vec3 b = FsrRcasLoadF(sp + ivec2( 0, -1));
  vec3 d = FsrRcasLoadF(sp + ivec2(-1,  0));
  vec3 e = FsrRcasLoadF(sp);
  vec3 f = FsrRcasLoadF(sp + ivec2( 1,  0));
  vec3 h = FsrRcasLoadF(sp + ivec2( 0,  1));
  // Min and max of ring.
  vec3 mn4 = min(min(b, d), min(f, h));
  vec3 mx4 = max(max(b, d), max(f, h));
  // Immediate constants for peak range.
  vec2 peakC = vec2(1.0, -1.0 * 4.0);
  // Limiters (the reference's high precision RCPs), guarded against 0/0.
  vec3 hitMin = min(mn4, e) / max(4.0 * mx4, vec3(1.0e-5));
  vec3 hitMax = (peakC.x - max(mx4, e)) / min(4.0 * mn4 + peakC.y, vec3(-1.0e-5));
  vec3 lobeRGB = max(-hitMin, hitMax);
  float lobe = max(-${rcasLimit}, min(max(lobeRGB.r, max(lobeRGB.g, lobeRGB.b)), 0.0)) * sharpness;
  // Resolve, which needs the medium precision rcp approximation to avoid visible tonality changes.
  float rcpL = APrxMedRcpF1(4.0 * lobe + 1.0);
  vec3 pix = (lobe * b + lobe * d + lobe * h + lobe * f + e) * rcpL;
  fragColor = vec4(pix, 1.0);
}
`;

/** FSR's sharpness is in stops (0 = strongest); 0.2 is AMD's suggested default. */
export const rcasStops = 0.2;

/** `FsrEasuCon`'s con0 for a whole-image upscale `in -> out`. */
export function easuCon0(inW: number, inH: number, outW: number, outH: number): [number, number, number, number] {
  return [inW / outW, inH / outH, 0.5 * inW / outW - 0.5, 0.5 * inH / outH - 0.5];
}

/** `FsrRcasCon`: stops -> linear. */
export function rcasSharpness(stops = rcasStops): number {
  return Math.pow(2, -stops);
}
