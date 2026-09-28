import { MissingReportPageDto, MissingSeriesDto, MissingUnitGapDto } from '../../../core/api/api-types';

/** Synthetic report rows for the missing-report specs. */
export function gap(over: Partial<MissingUnitGapDto> = {}): MissingUnitGapDto {
  return {
    kind: 'Volume', archiveCount: 7, lowest: 1, have: 7, available: 10, source: 'English', confidence: 'High',
    behindBy: 3, missing: [], missingCount: 0, ...over,
  };
}

export function missingRow(over: Partial<MissingSeriesDto> = {}): MissingSeriesDto {
  return {
    nodeId: 'series-1', displayName: 'Synthetic Series', libraryId: 'lib1', libraryName: 'Library One', coverUrl: null,
    provider: 'mangaupdates', recordTitle: 'Synthetic Record', linkState: 'Confirmed', verdict: 'Behind', volumes: gap(),
    chapters: null, mixedFolders: 0, englishTotalUnknown: false, statusText: null, ...over,
  };
}

export function missingPage(items: MissingSeriesDto[], nextCursor: string | null = null): MissingReportPageDto {
  return {
    items,
    summary: { series: items.length, behind: items.filter((i) => i.verdict === 'Behind').length, holes: 0, upToDate: 0, noTotal: 0, noVerdict: 0 },
    total: items.length,
    nextCursor,
  };
}
