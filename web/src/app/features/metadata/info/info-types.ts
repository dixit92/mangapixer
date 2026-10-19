import { BreadcrumbEntry } from '../../../core/api/api-types';

// 1.45.0 lane P: the web side of the Info panel's tags, the tag search and the creator admin routes (D1 "Information Panel and Faceted
// Tags" 2.4 / 2.6). These shapes mirror the server DTOs of lanes Q (`GET nodes/{id}/info`, the search filters) and F (the creator admin
// routes). They live here until those lanes' DTOs land in `core/api/api-types.ts`; at that merge this file re-exports them from there.

/** The Info panel's buckets, in the owner's display order (decision 5, D1 2.4; `FacetBucket` on the server). */
export type FacetBucketName = 'Creators' | 'Series' | 'Magazine' | 'Publisher' | 'Year' | 'Genres' | 'Type' | 'About' | 'Custom';

/** A creator's role on the work(s) a chip stands for: both = "Story & art" (D1 2.2). */
export type FacetCreatorRole = 'Writer' | 'Artist' | 'StoryAndArt';

/** Where a chip's value came from (`FacetSources`). */
export type FacetSourceName = 'Web' | 'ComicInfo' | 'Declared' | 'Admin';

/** One tag (a value of a bucket) as the viewer may see it. */
export interface FacetChipDto {
  /** The value's public id (`fv_...`): the search filter `f=<bucket>:<valueId>`. */
  valueId: string;
  display: string;
  /**
   * Works the viewer can see with this value (a single work), or works below carrying it (a container: "N here"). Present only when
   * 2 or more: a count of 1 is not shown (owner decision 5) - the chip stays clickable.
   */
  count?: number | null;
  /** Creators only: the role on this work / these works. */
  role?: FacetCreatorRole | null;
  /** A declared value that disagrees with the linked record (both chips are shown; this one is amber). */
  conflict?: boolean;
  sources?: FacetSourceName[] | null;
  /** Creators only: the canonical creator's public id (`cr_...`), for the admin menu. */
  creatorId?: string | null;
}

export interface FacetBucketDto {
  bucket: FacetBucketName;
  /** At most 12 on a container (the most carried first, Q10); every value on a single work. */
  chips: FacetChipDto[];
  /** Values left out of `chips` ("More"); 0 when all are shown. */
  more: number;
}

/** `GET /api/v1/nodes/{id}/info`: the node's tags, per viewer (D1 2.5 / 2.6). */
export interface InfoPanelDto {
  nodeId: string;
  /** True for a container (a library root, an artist's folder, "One Shots"): the chips are the values of the works below. */
  container: boolean;
  /** Works the node is or holds that the index knows (0 = nothing indexed: no Tags block). */
  works: number;
  /** Buckets with at least one chip, in display order. */
  buckets: FacetBucketDto[];
}

/** One location of a work (a folder or an archive), as display names only - never a path. */
export interface FacetWorkLocationDto {
  nodeId: string;
  libraryId: string;
  kind: 'Folder' | 'Archive';
  displayName: string;
  /** Library > folder > ... above the location (display names of visible nodes). The first entry is the library. */
  trail: BreadcrumbEntry[];
}

/** A WORK card of a filtered search (the tag view). */
export interface FacetWorkDto {
  workId: string;
  title: string;
  coverUrl?: string | null;
  /** The visible locations (a split series has several). */
  locations: FacetWorkLocationDto[];
  /** All visible locations; `locations` may hold fewer. */
  locationCount: number;
}

/** A search filter as the server resolved it (from `f=` or typed field syntax in `q`), so the field can show its name. */
export interface FacetFilterDto {
  /** `<bucket token>:<valueId>` - the `f` parameter that stands for this filter. */
  param: string;
  bucket: FacetBucketName;
  valueId: string;
  display: string;
  /** Set when the filter named a merged creator's value: the `f` parameter that was asked for (the web replaces the URL). */
  redirectedFrom?: string | null;
}

/** The search answer's filtered part (lane Q adds these fields to `SearchResultsDto`). */
export interface FacetSearchFields {
  /** With a filter: the matching works (instead of `items`). */
  works?: FacetWorkDto[] | null;
  /** The filters the server applied, in the order asked; a filter it did not know is left out. */
  filters?: FacetFilterDto[] | null;
}

// --- Creator admin (lane F: `/api/v1/admin/metadata/creators/...`) ---

export type CreatorNameSourceName = 'Record' | 'ProviderAuthor' | 'ComicInfo' | 'Declared' | 'Admin';

export interface CreatorNameDto {
  name: string;
  source: CreatorNameSourceName;
}

export interface CreatorProviderIdDto {
  provider: string;
  externalId: string;
}

export interface CreatorDto {
  id: string;
  displayName: string;
  displayNameSource: 'Provider' | 'MostUsed' | 'Admin';
  names: CreatorNameDto[];
  providerIds: CreatorProviderIdDto[];
  /** Works credited to this creator (all libraries - admins see every library). */
  works: number;
  /** Set when the id asked for was merged into this creator: the old id. */
  redirectedFrom?: string | null;
}

export interface CreatorListDto {
  items: CreatorDto[];
}

export interface CreatorMergeRequest {
  targetId: string;
}

export interface CreatorSplitRequest {
  providerIds: CreatorProviderIdDto[];
  names: string[];
}

export interface CreatorRenameRequest {
  /** null = back to the automatic display name. */
  displayName: string | null;
}
