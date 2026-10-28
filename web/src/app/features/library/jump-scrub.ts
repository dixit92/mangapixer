import { JumpIndexBucketDto } from '../../core/api/api-types';

/** Phone scrub strip (1.49.0): the pure parts - when a level is long, where a finger lands, which marks carry a label. */

/** A level with at least this many rows (the buckets' counts added up) gets the scrub strip on phones; a shorter one keeps the picker only. */
export const LONG_LEVEL_ROWS = 50;

/** The smallest slice of the strip (px) a labelled mark may have; below it only every Nth mark is labelled. */
export const MIN_MARK_PITCH = 18;

/** Marks longer than this many characters (`Folders`, `Other`) are drawn as a dot - the bubble names them. */
export const MAX_MARK_CHARS = 3;

/** Whether the level is long enough for the strip: two or more buckets holding `LONG_LEVEL_ROWS` rows or more together. */
export function isLongLevel(buckets: readonly JumpIndexBucketDto[], threshold = LONG_LEVEL_ROWS): boolean {
  if (buckets.length < 2) return false;
  let rows = 0;
  for (const b of buckets) rows += b.count;
  return rows >= threshold;
}

/**
 * The bucket a touch at `y` (viewport px) is on: the strip's height is cut into `count` equal slices from `top`. Clamped, so a drag
 * above or below the strip stays on the first / last bucket. -1 when there is nothing to pick.
 */
export function bucketIndexAt(y: number, top: number, height: number, count: number): number {
  if (count <= 0) return -1;
  if (!(height > 0)) return 0;
  const index = Math.floor(((y - top) / height) * count);
  return Math.min(count - 1, Math.max(0, index));
}

/** Every how many marks one carries a label so the labels do not touch (1 = all of them). */
export function labelStride(count: number, height: number, pitch = MIN_MARK_PITCH): number {
  if (count <= 0 || !(height > 0)) return 1;
  const slice = height / count;
  return Math.max(1, Math.ceil(pitch / slice));
}

/** The text of a mark: the chip's label, or null (a dot) when it is a word, or when thinning leaves this mark unlabelled. */
export function markText(bucket: JumpIndexBucketDto, index: number, stride: number): string | null {
  if (index % stride !== 0) return null;
  return bucket.label.length > MAX_MARK_CHARS ? null : bucket.label;
}

/** The long form shown in the bubble while scrubbing: a number chip's range (`50-99`), else its label. */
export function bubbleTitle(bucket: JumpIndexBucketDto): string {
  return bucket.range ?? bucket.label;
}
