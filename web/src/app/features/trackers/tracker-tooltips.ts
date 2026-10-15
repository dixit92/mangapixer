import { Provider } from '@angular/core';
import { MAT_TOOLTIP_DEFAULT_OPTIONS, MatTooltipDefaultOptions } from '@angular/material/tooltip';

/**
 * The tracker pages' tooltips (UX review 1.43.0, UX1): a tooltip is for a mouse only. On a touch screen it never opens - not on a
 * tap, not on a long press - so it can never stay stuck on the screen; whatever a tooltip explains is also written on the page
 * (a disabled action's reason is a line under it). "Touch screen" is a screen without any hover (`any-hover: none`), which also
 * catches an iPad that reports itself as a Mac; a tablet with a trackpad keeps its hover tooltips.
 */
export const TRACKER_TOOLTIP_OPTIONS: MatTooltipDefaultOptions = {
  showDelay: 0,
  hideDelay: 0,
  touchendHideDelay: 0,
  touchGestures: 'off',
  detectHoverCapability: true,
};

/** Component `providers` entry: every `matTooltip` in that component's template uses the options above. */
export function provideTrackerTooltips(): Provider {
  return { provide: MAT_TOOLTIP_DEFAULT_OPTIONS, useValue: TRACKER_TOOLTIP_OPTIONS };
}
