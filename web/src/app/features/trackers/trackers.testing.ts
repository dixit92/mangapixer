import { TrackerAccountDto, TrackerSummaryDto } from '../../core/api/api-types';

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
    addStartedSeries: true, privateEntries: true, requestsToday: 3, dailyRequestCap: 300, ...overrides,
  };
}
