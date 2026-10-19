import { FacetBucketDto, FacetChipDto, InfoPanelDto } from './info-types';

/** Test builders for the Info panel's DTOs (1.45.0 specs, lane Q's shapes). Synthetic values only. */
export function chip(overrides: Partial<FacetChipDto> = {}): FacetChipDto {
  const id = overrides.id ?? 'fv_a';
  return { id, display: 'Synthetic Author', roles: 'None', conflict: false, filter: `creator:${id}`, ...overrides };
}

export function bucket(overrides: Partial<FacetBucketDto> = {}): FacetBucketDto {
  return { bucket: 'Creators', chips: [chip()], more: 0, ...overrides };
}

export function panel(overrides: Partial<InfoPanelDto> = {}): InfoPanelDto {
  return { nodeId: 'n1', mode: 'Work', buckets: [bucket()], ...overrides };
}

/** "Nothing indexed" as lane Q answers it. */
export function emptyPanel(nodeId = 'n1'): InfoPanelDto {
  return { nodeId, mode: 'None', buckets: [] };
}
