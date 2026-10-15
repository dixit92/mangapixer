import {
  TrackerAccountDto, TrackerListCountsDto, TrackerSeriesPageDto, TrackerSeriesRowDto, TrackerSummaryDto,
} from '../../core/api/api-types';

/** Test builders for the tracker sync specs (1.43.0). */
export function summary(overrides: Partial<TrackerSummaryDto> = {}): TrackerSummaryDto {
  return {
    slug: 'mal', name: 'MyAnimeList', allowed: true, networkDisabledByConfig: false, connectKind: 'OAuthPkce',
    currentConsentVersion: 1, supportsPrivateEntries: false, account: null, ...overrides,
  };
}

export function trackerAccount(overrides: Partial<TrackerAccountDto> = {}): TrackerAccountDto {
  return {
    tracker: 'mal', state: 'Ok', displayName: 'reader', connectedAt: '2026-10-13T00:00:00Z', consentRenewalNeeded: false,
    addStartedSeries: true, privateEntries: true, requestsToday: 3, dailyRequestCap: 2000, ...overrides,
  };
}

/** A Tracking list row (lane G's `TrackerSeriesRowDto`): MyAnimeList, in sync at 12 ch / 2 vol of 51 / 10, dates supported. */
export function seriesRow(overrides: Partial<TrackerSeriesRowDto> = {}): TrackerSeriesRowDto {
  return {
    seriesId: 's1', title: 'Synthetic Saga', nodeId: 'n1', tracker: 'mal', remoteId: '2', remoteIdSource: 'MangaDex', state: 'Ok',
    bucket: 'InSync', errorCode: null, dontSync: false, chapters: 12, volumes: 2, allChapters: 12, allVolumes: 2, heldChapters: null,
    heldVolumes: null, totalChapters: 51, totalVolumes: 10, remoteChapters: 12, remoteVolumes: 2, remoteStatus: 'Reading',
    remoteMissing: false,
    dates: {
      supported: true, startedOn: '2026-10-01', startedOnOverridden: false, finishedOn: null, finishedOnOverridden: false,
      remoteStartedOn: '2026-10-01', remoteFinishedOn: null, pending: false,
    },
    fromTracker: { possible: false, block: 'NotAhead' }, toTracker: { possible: false, block: 'NotAhead' },
    lastSyncAt: '2026-10-13T00:00:00Z', lastSentAt: '2026-10-13T00:00:00Z', updatedAt: '2026-10-13T00:00:00Z', ...overrides,
  };
}

/** Chip counts with everything 0 but the ones given (`all` = the sum unless given). */
export function listCounts(overrides: Partial<TrackerListCountsDto> = {}): TrackerListCountsDto {
  const base: TrackerListCountsDto = {
    all: 0, inSync: 0, mangaPixerAhead: 0, trackerAhead: 0, leftAsIs: 0, held: 0, noId: 0, notSynced: 0, problem: 0, unchecked: 0, attention: 0,
  };
  const merged = { ...base, ...overrides };
  if (overrides.all == null) {
    merged.all = merged.inSync + merged.mangaPixerAhead + merged.trackerAhead + merged.leftAsIs + merged.held + merged.noId + merged.notSynced
      + merged.problem + merged.unchecked;
  }
  return merged;
}

/** One page of the Tracking list. */
export function seriesPage(items: TrackerSeriesRowDto[], overrides: Partial<TrackerSeriesPageDto> = {}): TrackerSeriesPageDto {
  return { items, page: 1, pageSize: 50, totalCount: items.length, counts: listCounts({ inSync: items.length }), ...overrides };
}
