/**
 * The appearance vocabulary (1.40.0 theming). These values are stored per user on the server (validated against the same
 * lists there), written to `<html data-theme="..." data-accent="...">` and styled only through the semantic `--mp-*` tokens
 * in `styles.scss` and `src/themes/`.
 *
 * - A BASE theme sets the surfaces, text and status colours. `system` follows the device: `light` when it prefers a light
 *   scheme, otherwise `dark`.
 * - An ACCENT sets the brand / selection colour (`--mp-accent*`) and Angular Material's primary family on top of any base.
 */
export const THEME_BASES = ['dark', 'light', 'black', 'sepia', 'system'] as const;
export type ThemeBase = (typeof THEME_BASES)[number];

/** The base a new user gets (owner decision, 1.40.0): dark, as before theming existed. */
export const DEFAULT_THEME_BASE: ThemeBase = 'dark';

export const THEME_ACCENTS = ['violet', 'blue', 'teal', 'green', 'amber', 'rose'] as const;
export type ThemeAccent = (typeof THEME_ACCENTS)[number];

/** The accent the app has always used. */
export const DEFAULT_THEME_ACCENT: ThemeAccent = 'violet';

/** A base theme as painted: what `system` resolves to on this device. */
export type ResolvedThemeBase = Exclude<ThemeBase, 'system'>;

export function isThemeBase(value: unknown): value is ThemeBase {
  return typeof value === 'string' && (THEME_BASES as readonly string[]).includes(value);
}

export function isThemeAccent(value: unknown): value is ThemeAccent {
  return typeof value === 'string' && (THEME_ACCENTS as readonly string[]).includes(value);
}
