// The theme sources are read as text by the bundler (esbuild's text loader); tsc only knows ES2022 modules, hence the
// expect-error on each import attribute.
// @ts-expect-error TS2823: import attributes are read by esbuild, not by tsc (module ES2022)
import stylesScss from '../styles.scss' with { loader: 'text' };
// @ts-expect-error TS2823: import attributes are read by esbuild, not by tsc (module ES2022)
import blackScss from './_black.scss' with { loader: 'text' };
// @ts-expect-error TS2823: import attributes are read by esbuild, not by tsc (module ES2022)
import sepiaScss from './_sepia.scss' with { loader: 'text' };
// @ts-expect-error TS2823: import attributes are read by esbuild, not by tsc (module ES2022)
import accentsScss from './_accents.scss' with { loader: 'text' };

import { DEFAULT_THEME_ACCENT, DEFAULT_THEME_BASE, THEME_ACCENTS, THEME_BASES } from '../app/core/theme/theme-vocabulary';

/** The seven tokens an accent sets (the `--mp-accent-bg` tint is derived from `--mp-accent-strong-rgb` in styles.scss). */
const ACCENT_TOKENS = [
  '--mp-accent',
  '--mp-accent-rgb',
  '--mp-accent-strong',
  '--mp-accent-strong-rgb',
  '--mp-accent-soft',
  '--mp-on-accent',
  '--mp-surface-accent',
];

interface Block {
  file: string;
  selector: string;
  theme?: string;
  accent?: string;
  props: Map<string, string>;
  body: string;
}

const SOURCES: Record<string, string> = {
  'styles.scss': stylesScss as string,
  '_black.scss': blackScss as string,
  '_sepia.scss': sepiaScss as string,
  '_accents.scss': accentsScss as string,
};

/** Top-level `:root...` / `html` rule blocks of a stylesheet, with their custom properties (comments removed). */
function blocksOf(file: string, source: string): Block[] {
  const text = source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');
  const blocks: Block[] = [];
  const head = /^(:root(?:\[[^\]]+\])*)\s*\{/gm;
  let m: RegExpExecArray | null;
  while ((m = head.exec(text))) {
    let depth = 1;
    let i = head.lastIndex;
    for (; i < text.length && depth > 0; i++) {
      if (text[i] === '{') depth++;
      else if (text[i] === '}') depth--;
    }
    const body = text.slice(head.lastIndex, i - 1);
    const props = new Map<string, string>();
    for (const d of body.matchAll(/(--[a-z0-9-]+|color-scheme)\s*:\s*([^;]+);/g)) props.set(d[1], d[2].trim());
    const selector = m[1];
    blocks.push({
      file,
      selector,
      theme: /\[data-theme='([a-z]+)'\]/.exec(selector)?.[1],
      accent: /\[data-accent='([a-z]+)'\]/.exec(selector)?.[1],
      props,
      body,
    });
  }
  return blocks;
}

const ALL = Object.entries(SOURCES).flatMap(([file, src]) => blocksOf(file, src));
const find = (theme?: string, accent?: string) => ALL.filter((b) => b.theme === theme && b.accent === accent);
const PAINTED = THEME_BASES.filter((b) => b !== 'system');
/** The scheme a painted base declares (`dark` for the `:root` default). */
const schemeOf = (base: string) =>
  base === DEFAULT_THEME_BASE ? 'dark' : find(base).map((b) => b.props.get('color-scheme')).find((s) => !!s);
const LIGHT_FAMILY = PAINTED.filter((b) => schemeOf(b) === 'light');

describe('theme sources', () => {
  it('styles.scss loads every theme file', () => {
    for (const f of ['black', 'sepia', 'accents']) expect(stylesScss).toContain(`@use './themes/${f}';`);
  });

  it('every painted base other than the default has its own block with a color-scheme', () => {
    for (const base of PAINTED) {
      if (base === DEFAULT_THEME_BASE) continue;
      expect(find(base).length, `:root[data-theme='${base}']`).toBeGreaterThan(0);
      expect(['light', 'dark'], `color-scheme of ${base}`).toContain(schemeOf(base));
    }
    expect(schemeOf('black')).toBe('dark');
    expect(schemeOf('sepia')).toBe('light');
  });

  it('the default :root block holds the default (violet) accent values', () => {
    const defaults = find(undefined, undefined).filter((b) => b.file === 'styles.scss');
    const declared = new Set(defaults.flatMap((b) => [...b.props.keys()]));
    for (const t of ACCENT_TOKENS) expect(declared.has(t), t).toBe(true);
    expect(DEFAULT_THEME_ACCENT).toBe('violet');
  });

  it('a light-family base sets its own default accent text colour; a dark-family one leaves the accents alone', () => {
    for (const base of PAINTED.filter((b) => b !== DEFAULT_THEME_BASE)) {
      const props = new Set(find(base).flatMap((b) => [...b.props.keys()]));
      if (LIGHT_FAMILY.includes(base)) expect(props.has('--mp-accent'), `${base} sets --mp-accent`).toBe(true);
      else for (const t of ACCENT_TOKENS) expect(props.has(t), `${base} must not set ${t}`).toBe(false);
    }
  });

  for (const accent of THEME_ACCENTS.filter((a) => a !== DEFAULT_THEME_ACCENT)) {
    it(`accent ${accent} has dark-family values and the Material primary family`, () => {
      const [block, ...extra] = find(undefined, accent);
      expect(block, `:root[data-accent='${accent}']`).toBeDefined();
      expect(extra).toEqual([]);
      for (const t of ACCENT_TOKENS) expect(block.props.has(t), `${accent}: ${t}`).toBe(true);
      expect(block.body).toMatch(/@include _material-accent\(\$_[a-z]+-primary, \$_[a-z]+-secondary\);/);
    });

    it(`accent ${accent} has its own values on every light-family base`, () => {
      for (const base of LIGHT_FAMILY) {
        const [block] = find(base, accent);
        expect(block, `:root[data-accent='${accent}'][data-theme='${base}']`).toBeDefined();
        for (const t of ACCENT_TOKENS) expect(block.props.has(t), `${accent} on ${base}: ${t}`).toBe(true);
      }
    });
  }

  it('every *-rgb token holds the channels of its colour token where both are set in one block', () => {
    const hex = (v: string) => {
      const h = v.replace('#', '');
      const full = h.length === 3 ? [...h].map((c) => c + c).join('') : h;
      return [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16)).join(' ');
    };
    for (const b of ALL.filter((x) => x.file !== 'styles.scss')) {
      for (const [name, value] of b.props) {
        if (!name.endsWith('-rgb') || name === '--mp-ink-rgb') continue;
        const colour = b.props.get(name.slice(0, -4));
        if (colour) expect(value, `${b.selector} ${name}`).toBe(hex(colour));
      }
    }
  });

  it('the theme files only set tokens styles.scss defines, and only for vocabulary values', () => {
    const defined = new Set(find(undefined, undefined).flatMap((b) => [...b.props.keys()]));
    for (const b of ALL.filter((x) => x.file !== 'styles.scss')) {
      if (b.theme) expect(THEME_BASES as readonly string[], b.selector).toContain(b.theme);
      if (b.accent) expect(THEME_ACCENTS as readonly string[], b.selector).toContain(b.accent);
      for (const name of b.props.keys()) {
        if (name === 'color-scheme' || name.startsWith('--mat-sys-')) continue;
        expect(defined.has(name), `${b.file} ${b.selector} sets unknown token ${name}`).toBe(true);
      }
    }
  });
});
