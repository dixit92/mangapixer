import { FacetBucketName, FacetChipDto, FacetCreatorRole, FacetSourceName } from './info-types';

/**
 * The Info panel's bucket order and labels (owner decision 5, D1 2.4): creators first, About last. The server sends buckets in this order
 * already; the web sorts again so a reordered answer never reorders the panel.
 */
export const BUCKET_ORDER: readonly FacetBucketName[] = [
  'Creators', 'Series', 'Magazine', 'Publisher', 'Year', 'Genres', 'Type', 'About', 'Custom',
];

const BUCKET_LABELS: Record<FacetBucketName, string> = {
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

/**
 * The bucket's word in a search filter (`f=<token>:<value id>`) and in typed field syntax (`creator:"..."`, D1 2.6): one spelling for
 * both, so a filter chip reads like what a user would type.
 */
const BUCKET_TOKENS: Record<FacetBucketName, string> = {
  Creators: 'creator',
  Series: 'series',
  Magazine: 'magazine',
  Publisher: 'publisher',
  Year: 'year',
  Genres: 'genre',
  Type: 'type',
  About: 'about',
  Custom: 'tag',
};

export function bucketLabel(bucket: FacetBucketName): string {
  return BUCKET_LABELS[bucket] ?? bucket;
}

export function bucketToken(bucket: FacetBucketName): string {
  return BUCKET_TOKENS[bucket] ?? bucket.toLowerCase();
}

export function bucketRank(bucket: FacetBucketName): number {
  const i = BUCKET_ORDER.indexOf(bucket);
  return i < 0 ? BUCKET_ORDER.length : i;
}

/** The `f` search parameter for a chip: `<bucket token>:<value id>`. */
export function filterParam(bucket: FacetBucketName, valueId: string): string {
  return `${bucketToken(bucket)}:${valueId}`;
}

/** A well-formed `f` value (`<token>:<id>`, both non-empty, no spaces); anything else is dropped from the URL model. */
export function isFilterParam(value: string): boolean {
  return /^[a-z]+:[A-Za-z0-9_-]+$/.test(value);
}

/**
 * The count shown on a chip, or null (owner decision 5: a count of 1 is not shown - the chip stays quiet but clickable). A container's
 * chips count the works below that carry the value ("12 here", D1 2.6); a single work's chips count every work the viewer can see.
 */
export function chipCount(chip: FacetChipDto, container: boolean): string | null {
  const n = chip.count ?? 0;
  if (n < 2) return null;
  return container ? `${n} here` : `(${n})`;
}

const ROLE_LABELS: Record<FacetCreatorRole, string> = {
  Writer: 'Story',
  Artist: 'Art',
  StoryAndArt: 'Story & art',
};

export function roleLabel(role: FacetCreatorRole | null | undefined): string | null {
  return role ? ROLE_LABELS[role] ?? null : null;
}

/** The chip's text as a whole: what a screen reader announces and the tooltip shows (the visible text may be ellipsized). */
export function chipTitle(bucket: FacetBucketName, chip: FacetChipDto, container: boolean): string {
  const parts = [chipText(bucket, chip)];
  const role = roleLabel(chip.role);
  if (role) parts.push(role);
  const count = chip.count ?? 0;
  if (count >= 2) parts.push(container ? `${count} works here` : `${count} works`);
  if (chip.conflict) parts.push(conflictWords(chip.sources));
  return parts.join(', ');
}

/** The chip's visible name; an About chip names its series as "Fan works of <series>" (owner Q8). */
export function chipText(bucket: FacetBucketName, chip: FacetChipDto): string {
  return bucket === 'About' ? `Fan works of ${chip.display}` : chip.display;
}

/**
 * The conflict wording of the declared-facts line (owner 2026-09-27, answer 3: show both, mark the conflict): which side this chip is.
 */
export function conflictWords(sources: FacetSourceName[] | null | undefined): string {
  const declared = (sources ?? []).includes('Declared');
  return declared ? 'Conflict: declared here, the web record says otherwise' : 'Conflict: from the web record, the folder declares otherwise';
}

/** At most this many chips per bucket before "More" (owner Q10 - containers; the server already cuts there). */
export const CHIPS_BEFORE_MORE = 12;
