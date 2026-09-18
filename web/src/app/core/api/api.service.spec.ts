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

describe('ApiService backup restore + audit trail (1.18.0)', () => {
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

  it('lists the on-disk rotating snapshots', () => {
    api.listRotatingBackups().subscribe((dto) => {
      expect(dto.files.length).toBe(1);
      expect(dto.files[0].fileName).toBe('rotating-20260909-035000.db');
      expect(dto.files[0].byteSize).toBe(4096);
    });

    const req = httpMock.expectOne('/api/v1/operations/backups/files');
    expect(req.request.method).toBe('GET');
    req.flush({
      files: [{ fileName: 'rotating-20260909-035000.db', byteSize: 4096, timestampUtc: '2026-09-09T03:50:00Z' }],
    });
  });

  it('stages a restore from a chosen snapshot file name', () => {
    api.restoreFromBackup('rotating-20260909-035000.db').subscribe((res) => {
      expect(res.preRestoreBackupFileName).toBe('pre-restore-20260909-040000.db');
    });

    const req = httpMock.expectOne('/api/v1/operations/backups/restore');
    expect(req.request.method).toBe('POST');
    expect(req.request.body.fileName).toBe('rotating-20260909-035000.db');
    req.flush({ preRestoreBackupFileName: 'pre-restore-20260909-040000.db', message: 'Restore staged.' });
  });

  it('stages a restore from an uploaded file via multipart form', () => {
    const file = new File([new Uint8Array([1, 2, 3])], 'backup.db', { type: 'application/octet-stream' });
    api.restoreFromUpload(file).subscribe((res) => {
      expect(res.message).toBe('Restore staged.');
    });

    const req = httpMock.expectOne('/api/v1/operations/restore');
    expect(req.request.method).toBe('POST');
    expect(req.request.body instanceof FormData).toBe(true);
    req.flush({ preRestoreBackupFileName: 'pre-restore.db', message: 'Restore staged.' });
  });

  it('fetches a page of the audit trail with page params', () => {
    api.getAuditTrail(2, 50).subscribe((dto) => {
      expect(dto.totalCount).toBe(120);
      expect(dto.items[0].action).toBe('user.delete');
    });

    const req = httpMock.expectOne((r) => r.url === '/api/v1/admin/audit');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('50');
    req.flush({
      items: [{
        id: 1, action: 'user.delete', result: 'success', actorUserId: 1,
        actorUserName: 'admin', targetUserId: 5, timestamp: '2026-09-09T03:50:00Z', correlationId: null,
      }],
      totalCount: 120,
      page: 2,
      pageSize: 50,
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
    api.getAllLibraries().subscribe((libs) => {
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

describe('ApiService scan-all libraries (1.8.0)', () => {
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

  it('triggers a scan for every library via POST and reports started/skipped counts', () => {
    api.scanAllLibraries().subscribe((dto) => {
      expect(dto.startedCount).toBe(2);
      expect(dto.skippedCount).toBe(1);
      expect(dto.scanRunIds).toEqual(['run-a', 'run-b']);
    });

    const req = httpMock.expectOne('/api/v1/admin/libraries/scan-all');
    expect(req.request.method).toBe('POST');
    req.flush({ startedCount: 2, skippedCount: 1, scanRunIds: ['run-a', 'run-b'] });
  });
});
