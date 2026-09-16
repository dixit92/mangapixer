import { readerModeGlyph } from './reader-mode-glyph';
import { ReaderMode } from '../core/api/api-types';

/**
 * The ReaderMode -> glyph mapping shared by the app-shell sidebar and the home
 * library grid. Every non-null mode gets a distinct glyph and a human-readable
 * label (used for both tooltip and aria-label); null (inherit) intentionally gets
 * no badge.
 */
describe('readerModeGlyph', () => {
  it('maps each concrete reader mode to a distinct glyph and label', () => {
    expect(readerModeGlyph('PagedLtr')).toEqual({ icon: 'format_textdirection_l_to_r', label: 'Left to right' });
    expect(readerModeGlyph('PagedRtl')).toEqual({ icon: 'format_textdirection_r_to_l', label: 'Right to left' });
    expect(readerModeGlyph('VerticalWebtoon')).toEqual({ icon: 'swap_vert', label: 'Vertical (webtoon)' });
    expect(readerModeGlyph('DoubleSpread')).toEqual({ icon: 'import_contacts', label: 'Double-page spread' });
  });

  it('gives every mode a unique icon (no glyph collisions)', () => {
    const modes: ReaderMode[] = ['PagedLtr', 'PagedRtl', 'VerticalWebtoon', 'DoubleSpread'];
    const icons = modes.map((m) => readerModeGlyph(m)!.icon);
    expect(new Set(icons).size).toBe(modes.length);
  });

  it('returns null for the inherit-default (null) mode so no badge renders', () => {
    expect(readerModeGlyph(null)).toBeNull();
  });
});
