/**
 * The rating scale (1.52.0, owner decision 5a): a rating is stored as an integer 1-10 and shown as 5 stars with halves - score / 2
 * stars (1 = half a star, 7 = three and a half, 10 = five). Pure helpers shared by the control, the badge and the sheets.
 */

export const MIN_SCORE = 1;
export const MAX_SCORE = 10;
export const STAR_COUNT = 5;

/** Whether `score` is a storable rating (an integer 1-10). */
export function isScore(score: unknown): score is number {
  return typeof score === 'number' && Number.isInteger(score) && score >= MIN_SCORE && score <= MAX_SCORE;
}

/** A stored score as stars - "3.5", "4", "0.5". */
export function starsText(score: number): string {
  return Number.isInteger(score / 2) ? String(score / 2) : (score / 2).toFixed(1);
}

/** The slider's `aria-valuetext` - "3.5 of 5 stars"; "Not rated" without a score. */
export function valueText(score: number | null | undefined): string {
  return isScore(score) ? `${starsText(score)} of ${STAR_COUNT} stars` : 'Not rated';
}

/** The read-only label - "Rated 3.5 of 5". */
export function ratedLabel(score: number): string {
  return `Rated ${starsText(score)} of ${STAR_COUNT}`;
}

/** The icon of star `star` (1-5) under `score`: full, half or empty. */
export function starIcon(star: number, score: number | null | undefined): 'star' | 'star_half' | 'star_border' {
  const s = isScore(score) ? score : 0;
  if (s >= star * 2) return 'star';
  if (s === star * 2 - 1) return 'star_half';
  return 'star_border';
}

/** The score a tap on the left (`half` = 'left') or right half of star `star` (1-5) sets. */
export function scoreOf(star: number, half: 'left' | 'right'): number {
  return half === 'left' ? star * 2 - 1 : star * 2;
}

/**
 * The slider's keyboard (WAI-ARIA slider pattern): Right / Up a half star more, Left / Down a half star less (never below half a
 * star - Delete clears), Page Up / Page Down a whole star, Home half a star, End five stars, Delete / Backspace clears (null).
 * `undefined` = not a key of the slider. From no rating, a step up starts at half a star.
 */
export function keyScore(key: string, score: number | null | undefined): number | null | undefined {
  const s = isScore(score) ? score : null;
  switch (key) {
    case 'ArrowRight':
    case 'ArrowUp':
      return s === null ? MIN_SCORE : Math.min(MAX_SCORE, s + 1);
    case 'ArrowLeft':
    case 'ArrowDown':
      return s === null ? MIN_SCORE : Math.max(MIN_SCORE, s - 1);
    case 'PageUp':
      return s === null ? 2 : Math.min(MAX_SCORE, s + 2);
    case 'PageDown':
      return s === null ? MIN_SCORE : Math.max(MIN_SCORE, s - 2);
    case 'Home':
      return MIN_SCORE;
    case 'End':
      return MAX_SCORE;
    case 'Delete':
    case 'Backspace':
      return null;
    default:
      return undefined;
  }
}
