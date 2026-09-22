// Server source of truth: src/MangaPixer.Core/Media/PageVariantFilters.cs
// Update this constant only if the server vocabulary changes.

/** Downscale filter vocabulary: ordered by increasing softness. */
export const DOWNSCALE_FILTERS = ['sharp', 'balanced', 'soft'] as const;

/** Which resampling filter a sized-down page request uses. */
export type DownscaleFilter = (typeof DOWNSCALE_FILTERS)[number];

/** Per-option tooltip text. */
const FILTER_HINTS: Record<DownscaleFilter, string> = {
  sharp: 'Crisp lines, may moire on screentones',
  balanced: 'Balanced (default)',
  soft: 'Smoothest screentones',
};

/** All filter options with their UI metadata (label, icon). */
export const DOWNSCALE_FILTER_OPTIONS: readonly { value: DownscaleFilter; label: string; icon: string }[] = [
  { value: 'sharp', label: 'Sharp', icon: 'deblur' },
  { value: 'balanced', label: 'Balanced', icon: 'texture' },
  { value: 'soft', label: 'Soft', icon: 'blur_on' },
];

/** Get the tooltip hint for a filter option. */
export function filterOptionHint(value: DownscaleFilter): string {
  return FILTER_HINTS[value] ?? '';
}
