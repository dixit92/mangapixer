import { ReaderMode } from '../core/api/api-types';

/**
 * A compact, at-a-glance indicator for a library's configured reading direction
 * / reader mode (1.5.0). Rendered on library icons in the app-shell sidebar and
 * the home library grid so the user can see the direction without opening the
 * library.
 *
 * The mapping lives here (not inline in each template) so every surface that
 * shows a library agrees on the same glyph and accessible label. `icon` is a
 * Material Icons ligature; `label` is a human-readable direction used for the
 * tooltip AND the aria-label (the badge is otherwise icon-only).
 */
export interface ReaderModeGlyph {
  icon: string;
  label: string;
}

/**
 * Map a `LibraryDto.defaultReaderMode` to its glyph. `null` means "inherit the
 * app default" - we intentionally return `null` (no badge) for it: the badge is
 * a signal that the library has an *explicit* direction, and a neutral glyph on
 * every inheriting library would only add noise. Callers render nothing when
 * this returns `null`.
 */
export function readerModeGlyph(mode: ReaderMode | null): ReaderModeGlyph | null {
  switch (mode) {
    case 'PagedLtr':
      return { icon: 'format_textdirection_l_to_r', label: 'Left to right' };
    case 'PagedRtl':
      return { icon: 'format_textdirection_r_to_l', label: 'Right to left' };
    case 'VerticalWebtoon':
      return { icon: 'swap_vert', label: 'Vertical (webtoon)' };
    case 'DoubleSpread':
      return { icon: 'import_contacts', label: 'Double-page spread' };
    default:
      // null (inherit) or any unknown/future value: no badge.
      return null;
  }
}
