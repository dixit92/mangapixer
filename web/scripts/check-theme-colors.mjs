// Theme colour guard (1.40.0 theming). Components style themselves ONLY through the semantic `--mp-*` tokens defined in
// src/styles.scss (and overridden per base theme / accent in src/themes/). This script lists every colour literal left in
// the component sources - hex, rgb()/rgba(), hsl()/hsla(), and literal fallbacks such as `var(--mp-accent, #b39dff)` - and
// suggests the token with the same (or the nearest) DARK value, so moving a component onto the tokens changes no pixel in
// the dark theme.
//
// A literal that must stay (e.g. a constant inside a shader) is exempted with a `theme-exempt: <reason>` comment on the same
// line or on the line above it. Scrims and text over cover images use the theme-independent tokens (`--mp-shade-rgb`,
// `--mp-on-scrim`), not an exemption.
//
// Usage: node scripts/check-theme-colors.mjs [--list] [--enforce] [path-prefix ...]
//   (no flag)   per-file counts, report only (exit 0)
//   --list      every literal with its file:line and the suggested token
//   --enforce   exit 1 when any non-exempt literal remains
//   path-prefix limit the scan to files under these paths (relative to web/, e.g. src/app/features/metadata)
// Scope: src/app/**/*.ts except *.spec.ts and *.testing.ts.
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const webDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const list = args.includes('--list');
const enforce = args.includes('--enforce');
const prefixes = args.filter((a) => !a.startsWith('--')).map((p) => p.replace(/\\/g, '/').replace(/\/$/, ''));

// --- the dark token values: the first :root block that defines --mp-ink-rgb
const styles = readFileSync(join(webDir, 'src', 'styles.scss'), 'utf8');
const rootBlock = [...styles.matchAll(/:root\s*\{([^}]*)\}/g)].map((m) => m[1]).find((b) => /--mp-ink-rgb\s*:/.test(b));
if (!rootBlock) {
  console.error('THEME CHECK ERROR: no :root block with --mp-ink-rgb in src/styles.scss');
  process.exit(2);
}
const hexTokens = []; // { name, rgb: [r,g,b] }
const rgbTokens = []; // { name, rgb: [r,g,b] }
for (const m of rootBlock.matchAll(/(--mp-[a-z0-9-]+)\s*:\s*([^;]+);/g)) {
  const [, name, raw] = m;
  const value = raw.trim();
  if (/^#[0-9a-f]{3,8}$/i.test(value)) hexTokens.push({ name, rgb: hexToRgb(value) });
  else if (/^\d+\s+\d+\s+\d+$/.test(value)) rgbTokens.push({ name, rgb: value.split(/\s+/).map(Number) });
}

function hexToRgb(hex) {
  let h = hex.slice(1);
  if (h.length === 3 || h.length === 4) h = [...h].map((c) => c + c).join('');
  return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16));
}
const same = (a, b) => a[0] === b[0] && a[1] === b[1] && a[2] === b[2];
const dist = (a, b) => Math.sqrt((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2 + (a[2] - b[2]) ** 2);
function nearest(tokens, rgb) {
  let best = null;
  for (const t of tokens) {
    const d = dist(t.rgb, rgb);
    if (!best || d < best.d) best = { t, d };
  }
  return best;
}

function suggest(literal) {
  const fn = /^(rgba?|hsla?)\(([^)]*)\)$/i.exec(literal);
  if (fn && fn[1].toLowerCase().startsWith('hsl')) return 'hsl(): pick the matching --mp-* token by hand';
  if (fn) {
    const parts = fn[2].split(/[\s,/]+/).filter(Boolean).map(Number);
    const rgb = parts.slice(0, 3);
    const alpha = parts.length > 3 ? parts[3] : 1;
    const exact = rgbTokens.filter((t) => same(t.rgb, rgb)).map((t) => t.name);
    if (exact.length) return exact.map((n) => (alpha === 1 ? `rgb(var(${n}))` : `rgb(var(${n}) / ${alpha})`)).join(' | ');
    const n = nearest(rgbTokens, rgb);
    return `~ rgb(var(${n.t.name}) / ${alpha})  (nearest, distance ${n.d.toFixed(0)})`;
  }
  const rgb = hexToRgb(literal);
  const exactHex = hexTokens.filter((t) => same(t.rgb, rgb)).map((t) => `var(${t.name})`);
  const exactRgb = rgbTokens.filter((t) => same(t.rgb, rgb)).map((t) => `rgb(var(${t.name}))`);
  if (exactHex.length || exactRgb.length) return [...exactHex, ...exactRgb].join(' | ');
  const n = nearest(hexTokens, rgb);
  return `~ var(${n.t.name})  (nearest, distance ${n.d.toFixed(0)})`;
}

// --- scan
const colour = /#[0-9a-fA-F]{3,8}\b|\b(?:rgba?|hsla?)\((?!\s*var\()[^)]*\)/g;
function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) yield* walk(p);
    else if (p.endsWith('.ts') && !p.endsWith('.spec.ts') && !p.endsWith('.testing.ts')) yield p;
  }
}
const perFile = new Map();
const hits = [];
for (const file of walk(join(webDir, 'src', 'app'))) {
  const rel = relative(webDir, file).replace(/\\/g, '/');
  if (prefixes.length && !prefixes.some((p) => rel === p || rel.startsWith(p + '/'))) continue;
  const lines = readFileSync(file, 'utf8').split('\n');
  lines.forEach((line, i) => {
    if (line.includes('theme-exempt') || (i > 0 && lines[i - 1].includes('theme-exempt:'))) return;
    for (const m of line.matchAll(colour)) {
      hits.push({ rel, line: i + 1, literal: m[0], suggestion: suggest(m[0]) });
      perFile.set(rel, (perFile.get(rel) ?? 0) + 1);
    }
  });
}

if (list) for (const h of hits) console.log(`${h.rel}:${h.line}  ${h.literal}  ->  ${h.suggestion}`);
else for (const [f, n] of [...perFile].sort((a, b) => b[1] - a[1])) console.log(`${String(n).padStart(4)}  ${f}`);
console.log(`theme check: ${hits.length} colour literal(s) in ${perFile.size} file(s)${prefixes.length ? ` under ${prefixes.join(', ')}` : ''}`);
if (enforce && hits.length > 0) {
  console.error('THEME CHECK FAILED: use the --mp-* tokens (src/styles.scss) or mark a necessary literal with `theme-exempt: <reason>`');
  process.exit(1);
}
