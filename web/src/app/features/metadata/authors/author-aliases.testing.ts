import { of } from 'rxjs';

import { AuthorAliasStatusDto } from '../../../core/api/api-types';
import { AuthorAliasesApiService } from './author-aliases-api.service';

/** A status for specs (1.38.0): nothing known yet unless overridden. */
export function aliasStatus(overrides: Partial<AuthorAliasStatusDto> = {}): AuthorAliasStatusDto {
  return {
    eligible: 0,
    fetched: 0,
    toFetch: 0,
    withOtherNames: 0,
    refreshAfterDays: 180,
    secondsPerRequest: 1,
    blockedReason: null,
    running: null,
    lastRun: null,
    ...overrides,
  };
}

/** A provider for specs of pages that host the "Artists' other names" card: answers the status, never the network. */
export function authorAliasesApiStub(status: AuthorAliasStatusDto = aliasStatus()) {
  return {
    provide: AuthorAliasesApiService,
    useValue: { status: () => of(status), start: () => of(status), cancel: () => of(status) },
  };
}
