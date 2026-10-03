import {
  MetadataFlagDto,
  MetadataMatchEstimateDto,
  MetadataMatchRunDto,
  MetadataReviewCandidateDto,
  MetadataReviewItemDto,
  MetadataReviewSummaryDto,
  MetadataSettingsDto,
} from '../../../core/api/api-types';
import { AUTO_CONSENT_TEXT_VERSION, CONSENT_TEXT_VERSION } from './settings/metadata-settings.component';

/** Test fixture builders for the stage-2 admin metadata specs. Synthetic values only. */

export function summary(overrides: Partial<MetadataReviewSummaryDto> = {}): MetadataReviewSummaryDto {
  return {
    needsReview: 3, later: 0, autoLinked: 12, unmatched: 2, openFlags: 1, dontMatch: 4, confirmed: 20, missingFolders: 1, pending: 0, recheckPending: 0,
    ...overrides,
  };
}

export function candidate(overrides: Partial<MetadataReviewCandidateDto> = {}): MetadataReviewCandidateDto {
  return {
    rank: 1, provider: 'mangaupdates', externalId: '100', title: 'Synthetic Saga', providerType: 'Manga', year: 2014, volumes: 14,
    titleScore: 0.94, adjustedScore: 0.9, reasons: ['close_second'], imageToken: 'tok1',
    ...overrides,
  };
}

export function reviewItem(overrides: Partial<MetadataReviewItemDto> = {}): MetadataReviewItemDto {
  return {
    nodeId: 'n1', nodeKind: 'Folder', displayName: 'Synthetic Saga', libraryId: 'lib1', libraryName: 'Library One',
    trail: ['Manga'], workClass: 'Series', matchLevel: 'Folder', itemCount: 24, openFlagCount: 0,
    candidates: [candidate(), candidate({ rank: 2, externalId: '200', title: 'Synthetic Saga Returns', year: 2019, volumes: 3,
      titleScore: 0.91, adjustedScore: 0.84, reasons: [], imageToken: 'tok2' })],
    reasons: ['close_second'],
    ...overrides,
  };
}

export function settings(overrides: Partial<MetadataSettingsDto> = {}): MetadataSettingsDto {
  return {
    showSeriesInfo: true,
    fetchEnabled: false,
    providers: [
      { id: 'mangaupdates', name: 'MangaUpdates', hosts: ['api.mangaupdates.com', 'cdn.mangaupdates.com'], usedFor: 'Series details.', sends: 'Search text.', allowed: true },
      { id: 'mangadex', name: 'MangaDex', hosts: ['api.mangadex.org', 'uploads.mangadex.org'], usedFor: 'Volume covers.', sends: 'A linked title.', allowed: true },
      { id: 'anilist', name: 'AniList', hosts: ['graphql.anilist.co'], usedFor: 'Chapters per volume.', sends: 'A linked title.', allowed: true },
    ],
    networkDisabledByConfig: false,
    acceptedConsentVersion: null,
    currentConsentVersion: CONSENT_TEXT_VERSION,
    consentAt: null,
    dailyBudget: 5000,
    defaultDailyBudget: 5000,
    budgetUsedToday: 12,
    backoffUntil: null,
    lastErrorAt: null,
    lastErrorCode: null,
    comicInfo: { archivesRead: 90, archivesTotal: 100, archivesWithComicInfo: 7 },
    webRecordCount: 2,
    libraries: [
      { libraryId: 'lib1', name: 'Library One', fetchEnabled: false, showSeriesInfo: true, precedence: null, linkCount: 2 },
    ],
    autoMatchEnabled: false,
    acceptedAutoConsentVersion: null,
    currentAutoConsentVersion: AUTO_CONSENT_TEXT_VERSION,
    autoConsentAt: null,
    thresholds: { autoTitle: 0.92, margin: 0.1, reviewFloor: 0.6 },
    defaultThresholds: { autoTitle: 0.92, margin: 0.1, reviewFloor: 0.6 },
    thresholdBounds: { autoTitleMin: 0.85, autoTitleMax: 0.99, marginMin: 0.05, marginMax: 0.3, reviewFloorMin: 0.4, reviewFloorMax: 0.9 },
    thresholdsAreDefault: true,
    ...overrides,
  };
}

export function run(overrides: Partial<MetadataMatchRunDto> = {}): MetadataMatchRunDto {
  return {
    runId: 'r1', libraryId: 'lib1', libraryName: 'Library One', trigger: 'Bulk', status: 'Completed', reviewFirst: false,
    startedAt: '2026-09-20T10:00:00Z', completedAt: '2026-09-20T11:00:00Z', candidates: 40, queued: 40, processed: 40,
    autoLinked: 30, needsReview: 8, unmatched: 2, skipped: 0, failed: 0, requestsUsed: 110, autoChangedByAdmin: 2,
    reviewAcceptedTop: 5, reviewAcceptedOther: 1, reviewDontMatch: 1,
    ...overrides,
  };
}

export function flag(overrides: Partial<MetadataFlagDto> = {}): MetadataFlagDto {
  return {
    flagId: 'f1', nodeId: 'n1', nodeKind: 'Folder', nodeDisplayName: 'Synthetic Saga', libraryId: 'lib1', reason: 'WrongSeries',
    note: 'The cover is from another series.', state: 'Open', reporterDisplayName: 'Reader One', createdAt: '2026-09-25T08:00:00Z',
    currentLink: { state: 'Auto', provider: 'mangaupdates', externalId: '100', title: 'Synthetic Saga', matchMethod: 'Auto',
      matchScore: 0.95, updatedAt: '2026-09-24T00:00:00Z' },
    ...overrides,
  };
}

export function estimate(overrides: Partial<MetadataMatchEstimateDto> = {}): MetadataMatchEstimateDto {
  return {
    libraryId: 'lib1', candidates: 120, estimatedRequests: 360, estimatedDays: 0.07, alreadyLinked: 15, unmatched: 4,
    dailyBudget: 5000, budgetUsedToday: 12, firstRun: true, automaticAvailable: true, unavailableCode: null,
    ...overrides,
  };
}
