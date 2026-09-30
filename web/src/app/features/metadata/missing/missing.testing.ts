import { MissingReportPageDto, MissingSeriesDto, MissingUnitGapDto, SeriesProgressDto } from '../../../core/api/api-types';

/** Synthetic report rows for the missing-report specs. */
export function gap(over: Partial<MissingUnitGapDto> = {}): MissingUnitGapDto {
  return {
    kind: 'Volume', archiveCount: 7, unitCount: 7, lowest: 1, have: 7, available: 10, source: 'English', confidence: 'High',
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

/** A 1.30.0 progress of the owner example: volumes 1-14 + chapters 43-57, English 15 volumes (one upgrade). */
export function ownerProgress(over: Partial<SeriesProgressDto> = {}): SeriesProgressDto {
  return {
    trackers: { language: 'en', origin: 'Japan', originStatus: 'Ongoing', originVolumes: 22, officialPublisher: 'Synthetic Press', officialVolumes: 15,
      officialStatus: 'Ongoing', latestChapter: 57 },
    reach: { volumeFiles: [{ from: 1, to: 14 }], chapters: [{ from: 43, to: 57 }], overlapChapters: 0, resolution: 'VolumeList' },
    missingVolumes: 0, missingChapters: 0, releaseKnown: true, upgradeVolumes: [15], upgradeCount: 1, completion: 'None', ...over,
  };
}
