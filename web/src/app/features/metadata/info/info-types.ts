import { BreadcrumbEntry, CatalogNodeDto } from '../../../core/api/api-types';

// 1.45.0 lane P: the web side of the Info panel's tags, the tag search and the creator admin routes (D1 "Information Panel and Faceted
// Tags" 2.4 / 2.6). These are lane Q's (`GET nodes/{id}/info`, the search filters) and lane F's (the creator admin routes) TypeScript
// types AS THOSE LANES BUILT THEM (their notes, "API shapes for lane P"), same names and fields. They are copied here because both lanes
// add them to `core/api/api-types.ts` on their own branches; when they are merged this file becomes re-exports from there.

/** Info panel buckets, in the owner's display order. URL / typed token: creator, series, magazine, publisher, year, genre, type, about, custom. */
export type FacetBucket = 'Creators' | 'Series' | 'Magazine' | 'Publisher' | 'Year' | 'Genres' | 'Type' | 'About' | 'Custom';

/** A creator's roles on a chip: comma-separated flag names ('Writer', 'Artist', 'Writer, Artist' = Story & art, 'None'). */
export type FacetRoles = string;

export type InfoPanelMode = 'None' | 'Work' | 'Container';

export interface FacetChipDto {
  /** The value's public id (`fv_...`). */
  id: string;
  display: string;
  /** Works the viewer may see that carry the value - only when 2 or more (count-of-1 rule). */
  count?: number | null;
  /** Container only: works below this node that carry it ("12 here"). */
  here?: number | null;
  roles: FacetRoles;
  conflict: boolean;
  /** One `f` query parameter value for the search (`creator:fv_...`). */
  filter: string;
}

export interface FacetBucketDto {
  bucket: FacetBucket;
  chips: FacetChipDto[];
  /** Further values beyond `chips` (container: top 12 per bucket). */
  more: number;
}

export interface FacetLocationDto {
  nodeId: string;
  libraryId: string;
  libraryName: string;
  /** From the library's top-level folder down to the location node itself (display names only). */
  trail: BreadcrumbEntry[];
}

export interface FacetWorkSummaryDto {
  title: string;
  locationCount: number;
  locations: FacetLocationDto[];
}

/** `GET /api/v1/nodes/{nodeId}/info` and `GET /api/v1/libraries/{libraryId}/info`. */
export interface InfoPanelDto {
  nodeId: string;
  mode: InfoPanelMode;
  buckets: FacetBucketDto[];
  worksBelow?: number | null;
  work?: FacetWorkSummaryDto | null;
}

/** A work card of the tag view. */
export interface FacetWorkDto {
  title: string;
  /** The first visible location as a normal card (cover, read state) - what the card opens. */
  node: CatalogNodeDto;
  coverUrl?: string | null;
  locationCount: number;
  /** At most 5 visible locations; the first is `node`. */
  locations: FacetLocationDto[];
}

export type SearchFilterSource = 'Chip' | 'Typed';

/** A filter as the server understood it. Not matched = unknown or not visible to the viewer (answered alike). */
export interface SearchFilterDto {
  bucket: FacetBucket;
  source: SearchFilterSource;
  id?: string;
  text?: string;
  display?: string;
  matched: boolean;
  valueIds: string[];
  redirectedFrom?: string;
}

/** The search answer's tag-view part (lane Q adds these two fields to `SearchResultsDto`). */
export interface FacetSearchFields {
  filters?: SearchFilterDto[];
  works?: FacetWorkDto[];
}

// --- lane F: the admin creator API (`/api/v1/admin/metadata/creators`, admin only, cookie + CSRF) ---

/** One spelling of a canonical creator and where it came from. */
export interface CreatorNameDto {
  name: string;
  /** record, providerAuthor, comicInfo, declared or admin. */
  source: string;
  /** latin, cjk, hangul or other. */
  script: string;
}

/** A canonical creator. Asked by a merged creator's id, the answer is the creator it was merged into, with `redirectedFrom`. */
export interface CreatorDto {
  /** cr_... */
  id: string;
  displayName: string;
  /** provider, mostUsed or admin. */
  displayNameSource: string;
  names?: CreatorNameDto[];
  /** mangaupdates:<author id>, sorted; empty for a name-only creator. */
  providerIds?: string[];
  mergedFrom?: string[];
  /** The creator's tag value (fv_...) for `/search?f=creators:<id>`, null while no work is indexed. */
  valueId?: string | null;
  /** Indexed works crediting it (all libraries). */
  works?: number;
  redirectedFrom?: string | null;
}

/** `GET /api/v1/admin/metadata/creators?q=&limit=`: live creators whose spellings contain the query. */
export interface CreatorSearchResultDto {
  items?: CreatorDto[];
}

export interface MergeCreatorRequest {
  intoId?: string | null;
}

export interface SplitCreatorRequest {
  providerIds?: string[] | null;
  names?: string[] | null;
}

export interface SetCreatorDisplayNameRequest {
  displayName?: string | null;
}
