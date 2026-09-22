// Server source of truth: src/MangaPixer.Core/Media/PageVariantFilters.cs
// These names are shared by both contracts: HTTP ?filter= query parameter,
// variant/cache-key string (webp@1440:balanced), X-MangaPixer-Variant response
// header and the worker's ExtractRequest.ResizeFilter. Update this constant
// only if the server vocabulary changes.

/** Downscale filter vocabulary: ordered by increasing softness. */
export const DOWNSCALE_FILTERS = ['sharp', 'balanced', 'soft'] as const;

/** Which resampling filter a sized-down page request uses. */
export type DownscaleFilter = (typeof DOWNSCALE_FILTERS)[number];

/**
 * Metadata for each downscale filter: what it is (resampling algorithm), where
 * it applies (sized-down requests under Page quality: Auto), and UI labels.
 *
 * sharp: Lanczos resampling (1.19.x behaviour). Sharpest lines, crisp edges
 * but can exhibit moire fringes on screentoned manga when a sharp server
 * downscale followed by browser near-unity resample beats the halftone grid.
 *
 * balanced: Mitchell resampling. The default since 1.20.0. Trade-off between
 * sharpness and smoothness.
 *
 * soft: Area-average (Box) resampling. Softest filter, dampens high-frequency
 * energy before browser resampling, eliminating screentone moire at the cost
 * of some line-art crispness. Which trade is right depends on the artwork and
 * screen, so it is exposed as a reader choice.
 *
 * The filter only applies to a sized (?maxDim=) request under Page quality
 * Auto. Full quality (original fit) never downscales, so filter has no effect.
 */
const DOWNSCALE_FILTER_METADATA: Record<DownscaleFilter, { label: string; icon: string; hint: string }> = {
  sharp: {
    label: 'Sharp',
    icon: 'deblur',
    hint: 'Crisp lines, may moire on screentones',
  },
  balanced: {
    label: 'Balanced',
    icon: 'texture',
    hint: 'Balanced (default)',
  },
  soft: {
    label: 'Soft',
    icon: 'blur_on',
    hint: 'Smoothest screentones',
  },
};

/** All filter options with their UI metadata (label, icon). Derived from metadata. */
export const DOWNSCALE_FILTER_OPTIONS = DOWNSCALE_FILTERS.map((value) => ({
  value,
  label: DOWNSCALE_FILTER_METADATA[value].label,
  icon: DOWNSCALE_FILTER_METADATA[value].icon,
})) as readonly { readonly value: DownscaleFilter; readonly label: string; readonly icon: string }[];

/** Get the tooltip hint for a filter option. */
export function filterOptionHint(value: DownscaleFilter): string {
  return DOWNSCALE_FILTER_METADATA[value].hint;
}
