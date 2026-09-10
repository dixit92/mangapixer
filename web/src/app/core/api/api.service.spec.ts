import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';

import { ApiService } from './api.service';
import { UpdateProgressRequest } from './api-types';

describe('ApiService.updateProgress (D32 idempotency headers)', () => {
  let api: ApiService;
  let httpMock: HttpTestingController;

  const request: UpdateProgressRequest = {
    pageIndex: 3,
    expectedContentVersion: 1,
    mutationId: 'm-1',
    entryKey: 'p3',
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('sends If-None-Match:* for the first write of an unread item (revision 0)', () => {
    api.updateProgress('item-1', request, 0).subscribe();

    const req = httpMock.expectOne('/api/v1/reading/progress/item-1');
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('If-None-Match')).toBe('*');
    expect(req.request.headers.has('If-Match')).toBe(false);
    req.flush({ revision: 1, alreadyApplied: false });
  });

  it('sends If-Match with the known revision for a subsequent write', () => {
    api.updateProgress('item-1', request, 7).subscribe();

    const req = httpMock.expectOne('/api/v1/reading/progress/item-1');
    expect(req.request.headers.get('If-Match')).toBe('"7"');
    expect(req.request.headers.has('If-None-Match')).toBe(false);
    req.flush({ revision: 8, alreadyApplied: false });
  });
});

describe('ApiService rotating backup status (1.2.0)', () => {
  let api: ApiService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('fetches the rotating backup status from the operations endpoint', () => {
    const status = {
      enabled: true,
      intervalHours: 24,
      retentionCount: 7,
      lastAttemptUtc: '2026-09-09T03:41:26Z',
      lastSuccessUtc: '2026-09-09T03:41:26Z',
      lastFailureUtc: null,
      lastBackupFileName: 'rotating-20260909-034126.db',
      retainedCount: 2,
    };

    api.getRotatingBackupStatus().subscribe((dto) => {
      expect(dto.retainedCount).toBe(2);
      expect(dto.lastBackupFileName).toBe('rotating-20260909-034126.db');
    });

    const req = httpMock.expectOne('/api/v1/operations/backups');
    expect(req.request.method).toBe('GET');
    req.flush({ ...status, retainedCount: 2 });
  });

  it('triggers a manual backup via POST and returns the refreshed status', () => {
    api.runRotatingBackupNow().subscribe((dto) => {
      expect(dto.lastBackupFileName).toBe('rotating-20260909-035000.db');
      expect(dto.retainedCount).toBe(3);
    });

    const req = httpMock.expectOne('/api/v1/operations/backups/rotating');
    expect(req.request.method).toBe('POST');
    req.flush({
      enabled: true,
      intervalHours: 24,
      retentionCount: 7,
      lastAttemptUtc: '2026-09-09T03:50:00Z',
      lastSuccessUtc: '2026-09-09T03:50:00Z',
      lastFailureUtc: null,
      lastBackupFileName: 'rotating-20260909-035000.db',
      retainedCount: 3,
    });
  });
});

describe('ApiService incognito / Private libraries (1.4.0)', () => {
  let api: ApiService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('fetches the current user Private library set', () => {
    api.getPrivateLibraries().subscribe((dto) => {
      expect(dto.libraryIds).toEqual(['L1', 'L3']);
    });

    const req = httpMock.expectOne('/api/v1/reading/private-libraries');
    expect(req.request.method).toBe('GET');
    req.flush({ libraryIds: ['L1', 'L3'] });
  });

  it('replaces the Private library set via PUT with replacement semantics', () => {
    api.setPrivateLibraries(['L2']).subscribe();

    const req = httpMock.expectOne('/api/v1/reading/private-libraries');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ libraryIds: ['L2'] });
    req.flush(null);
  });

  it('fetches the full library list for the Private-libraries management surface', () => {
    api.getAllLibrariesForPrivacyManagement().subscribe((libs) => {
      expect(libs.length).toBe(1);
    });

    const req = httpMock.expectOne('/api/v1/libraries');
    expect(req.request.method).toBe('GET');
    req.flush([{ id: 'L1', name: 'Alpha', isScanning: false, itemCount: 1, lastScanCompleted: null, defaultReaderMode: null }]);
  });

  it('fetches continue-reading scoped to a single library', () => {
    api.getContinueReadingByLibrary('L1').subscribe((entries) => {
      expect(entries.length).toBe(1);
      expect(entries[0].libraryId).toBe('L1');
    });

    const req = httpMock.expectOne((r) => r.url === '/api/v1/reading/continue/by-library/L1');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('limit')).toBe('20');
    req.flush([
      { itemId: 'i1', libraryId: 'L1', libraryName: 'Alpha', displayName: 'One', pageIndex: 0, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z' },
    ]);
  });
});
