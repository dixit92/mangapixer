import { SeriesInfoDto } from '../../core/api/api-types';

/** Test fixture builder for SeriesInfoDto (1.24.0 specs). Synthetic values only. */
export function seriesInfo(overrides: Partial<SeriesInfoDto> = {}): SeriesInfoDto {
  return {
    nodeId: 'n1',
    nodeKind: 'Folder',
    anchorNodeId: 'n1',
    anchorKind: 'Folder',
    anchorDisplayName: 'Synthetic Folder',
    libraryId: 'lib1',
    state: 'ComicInfo',
    title: 'Synthetic Saga',
    precedence: 'WebFirst',
    precedenceSource: 'Default',
    ...overrides,
  };
}
