import { FacetBucketDto, FacetChipDto, InfoPanelDto } from './info-types';

/** Test builders for the Info panel's DTOs (1.45.0 specs). Synthetic values only. */
export function chip(overrides: Partial<FacetChipDto> = {}): FacetChipDto {
  return { valueId: 'fv_a', display: 'Synthetic Author', ...overrides };
}

export function bucket(overrides: Partial<FacetBucketDto> = {}): FacetBucketDto {
  return { bucket: 'Creators', chips: [chip()], more: 0, ...overrides };
}

export function panel(overrides: Partial<InfoPanelDto> = {}): InfoPanelDto {
  return { nodeId: 'n1', container: false, works: 1, buckets: [bucket()], ...overrides };
}
