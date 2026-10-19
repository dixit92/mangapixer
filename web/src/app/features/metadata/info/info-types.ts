import type { FacetWorkDto, SearchFilterDto } from '../../../core/api/api-types';

// 1.45.0 lane P: the web side of the Info panel's tags, the tag search and the creator admin routes (D1 "Information Panel and Faceted
// Tags" 2.4 / 2.6). Lane P built against copies of lane Q's (`GET nodes/{id}/info`, the search filters) and lane F's (the creator admin
// routes) types; since Q and F merged, the generated contract types in `core/api/api-types.ts` are the one source and this file only
// re-exports them, so a contract change shows up as a compile error here instead of a silent drift.

export type {
  CreatorDto,
  CreatorNameDto,
  CreatorSearchResultDto,
  FacetBucket,
  FacetBucketDto,
  FacetChipDto,
  FacetLocationDto,
  FacetRoles,
  FacetWorkDto,
  FacetWorkSummaryDto,
  InfoPanelDto,
  InfoPanelMode,
  MergeCreatorRequest,
  SearchFilterDto,
  SearchFilterSource,
  SetCreatorDisplayNameRequest,
  SplitCreatorRequest,
} from '../../../core/api/api-types';

/** The search answer's tag-view part (lane Q's two fields on `SearchResultsDto`). */
export interface FacetSearchFields {
  filters?: SearchFilterDto[];
  works?: FacetWorkDto[];
}
