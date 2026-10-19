import { FacetBucket, FacetChipDto, FacetLocationDto, FacetRoles } from './info-types';

/**
 * The Info panel's bucket order and labels (owner decision 5, D1 2.4): creators first, About last. The server sends buckets in this order
 * already; the web sorts again so a reordered answer never reorders the panel.
 */
export const BUCKET_ORDER: readonly FacetBucket[] = [
  'Creators', 'Series', 'Magazine', 'Publisher', 'Year', 'Genres', 'Type', 'About', 'Custom',
];

const BUCKET_LABELS: Record<FacetBucket, string> = {
  Creators: 'Creators',
  Series: 'Series',
  Magazine: 'Magazine',
  Publisher: 'Publisher',
  Year: 'Year started',
  Genres: 'Genres',
  Type: 'Type',
  About: 'About',
  Custom: 'Tags',
};

/** The bucket's word in a search filter (`f=<token>:<value id>`) and in typed field syntax - lane Q's tokens. */
const BUCKET_TOKENS: Record<FacetBucket, string> = {
  Creators: 'creator',
  Series: 'series',
  Magazine: 'magazine',
  Publisher: 'publisher',
  Year: 'year',
  Genres: 'genre',
  Type: 'type',
  About: 'about',
  Custom: 'custom',
};

export function bucketLabel(bucket: FacetBucket): string {
  return BUCKET_LABELS[bucket] ?? bucket;
}

export function bucketToken(bucket: FacetBucket): string {
  return BUCKET_TOKENS[bucket] ?? bucket.toLowerCase();
}

export function bucketRank(bucket: FacetBucket): number {
  const i = BUCKET_ORDER.indexOf(bucket);
  return i < 0 ? BUCKET_ORDER.length : i;
}

/** The `f` search parameter for a value: `<bucket token>:<value id>` (a chip carries its own as `filter`). */
export function filterParam(bucket: FacetBucket, valueId: string): string {
  return `${bucketToken(bucket)}:${valueId}`;
}

/** A well-formed `f` value (`<token>:<id>`, both non-empty, no spaces); anything else is dropped from the URL model (the server answers 400). */
export function isFilterParam(value: string): boolean {
  return /^[a-z]+:[A-Za-z0-9_-]+$/.test(value);
}

/** The value id part of an `f` parameter. */
export function filterValueId(param: string): string {
  return param.slice(param.indexOf(':') + 1);
}

/**
 * The count shown on a chip, or null (owner decision 5: a count of 1 is not shown - the chip stays quiet but clickable). A container's
 * chips count the works below that carry the value ("12 here", D1 2.6); a single work's chips count every work the viewer can see.
 */
export function chipCount(chip: FacetChipDto, container: boolean): string | null {
  const n = (container ? chip.here : chip.count) ?? 0;
  if (n < 2) return null;
  return container ? `${n} here` : `(${n})`;
}

/** "Story", "Art" or "Story & art" from the chip's role flags (D1 2.2); nothing for other buckets. */
export function roleLabel(roles: FacetRoles | null | undefined): string | null {
  const set = new Set((roles ?? '').split(',').map((r) => r.trim()));
  const writer = set.has('Writer');
  const artist = set.has('Artist');
  if (writer && artist) return 'Story & art';
  if (writer) return 'Story';
  if (artist) return 'Art';
  return null;
}

/** The conflict wording of the declared-facts line (owner 2026-09-27, answer 3: both values are shown, the conflicting one marked). */
export const CONFLICT_WORDS = 'Conflict: the folder\'s declared value and the web record disagree';

/** The chip's text as a whole: what a screen reader announces and the tooltip shows (the visible text may be ellipsized). */
export function chipTitle(bucket: FacetBucket, chip: FacetChipDto, container: boolean): string {
  const parts = [chipText(bucket, chip)];
  const role = roleLabel(chip.roles);
  if (role) parts.push(role);
  const n = (container ? chip.here : chip.count) ?? 0;
  if (n >= 2) parts.push(container ? `${n} works here` : `${n} works`);
  if (chip.conflict) parts.push(CONFLICT_WORDS);
  return parts.join(', ');
}

/** The chip's visible name; an About chip names its series as "Fan works of <series>" (owner Q8). */
export function chipText(bucket: FacetBucket, chip: FacetChipDto): string {
  return bucket === 'About' ? `Fan works of ${chip.display}` : chip.display;
}

/** "Manga › Series A › Part 2": a location as display names (the library, then its folders down to the node) - never a path. */
export function locationText(loc: FacetLocationDto): string {
  return [loc.libraryName, ...loc.trail.map((t) => t.displayName)].filter((s) => !!s).join(' › ');
}
