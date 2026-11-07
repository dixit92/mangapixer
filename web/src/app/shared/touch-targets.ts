/**
 * Touch-target styles of the 1.51.0 mobile fixes (admin pages, Metadata Manager, series page, Tracking, reader options).
 *
 * One rule for all of them: on a touch screen (`@media (pointer: coarse)` - a phone in portrait AND in landscape, a tablet) a control
 * is at least 40 x 40 px; a mouse-driven window of any width keeps its compact look. The hit area grows by padding / min-height, never
 * by a bigger font. Components add these strings to their own `styles` (Angular scopes the selectors to the component); a component
 * with other needs writes its own `@media (pointer: coarse)` block.
 */

/** Native selects and text / number inputs (not checkboxes, radios or file pickers): at least 40 px tall. */
export const TOUCH_FIELDS_CSS = `
  @media (pointer: coarse) {
    select, input:not([type='checkbox']):not([type='radio']):not([type='file']) { min-height: 40px; box-sizing: border-box; }
  }
`;

/** A link or link-like button in a row: add `class="touch-link"` (it keeps its look, gains a 40 px tall hit area). */
export const TOUCH_LINK_CSS = `
  @media (pointer: coarse) {
    .touch-link { display: inline-flex; align-items: center; min-height: 40px; min-width: 40px; box-sizing: border-box; }
  }
`;
