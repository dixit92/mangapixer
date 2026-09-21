import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { AdminComponent } from './admin.component';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import {
  AuditTrailPageDto,
  DirectoryListingDto,
  LibraryDto,
  LogLevelDto,
  RotatingBackupListDto,
  RotatingBackupStatusDto,
  SystemInfoDto,
  UpdateCheckStatusDto,
} from '../../core/api/api-types';

/**
 * Directory browser row (Register New Library > Browse…). Was previously a plain
 * <div matListItemTitle (click)="..."> - not focusable and had no keyboard
 * equivalent (a11y lint: click-events-have-key-events, interactive-supports-focus).
 * Fixed by making it a real <button>, which is natively focusable and
 * keyboard-activatable (Enter/Space) without any extra JS - the browser itself
 * turns a keyboard activation into the same `click` the mouse path already used,
 * so the one behavioral thing worth pinning down here is that the row is a real
 * <button> wired to the same handler, not that a DOM click event fires (that part
 * is native <button> behavior, not application logic).
 */
describe('AdminComponent directory browser row', () => {
  function setup(entry: { name: string; path: string; hasChildren: boolean }) {
    const emptyBackupStatus: RotatingBackupStatusDto = {
      enabled: true, intervalHours: 24, retentionCount: 5,
      lastAttemptUtc: null, lastSuccessUtc: null, lastFailureUtc: null,
      lastBackupFileName: null, retainedCount: 0,
    };
    const emptyBackupList: RotatingBackupListDto = { files: [] };
    const emptyAudit: AuditTrailPageDto = { items: [], totalCount: 0, page: 1, pageSize: 20 };
    const systemInfo: SystemInfoDto = { version: '1.19.0', platform: null };
    const logLevel: LogLevelDto = { level: 'Information', categories: [] };
    const updateStatus: UpdateCheckStatusDto = {
      enabled: false, currentVersion: '1.19.0', latestVersion: null,
      updateAvailable: false, lastChecked: null,
    };
    const libs: LibraryDto[] = [];
    const listing: DirectoryListingDto = {
      available: true, root: null, current: '/library-root', parent: null, entries: [entry],
    };

    const apiSpy = {
      getAllLibraries: vi.fn().mockReturnValue(of(libs)),
      listUsers: vi.fn().mockReturnValue(of([])),
      getRotatingBackupStatus: vi.fn().mockReturnValue(of(emptyBackupStatus)),
      listRotatingBackups: vi.fn().mockReturnValue(of(emptyBackupList)),
      getAuditTrail: vi.fn().mockReturnValue(of(emptyAudit)),
      getSystemInfo: vi.fn().mockReturnValue(of(systemInfo)),
      browseLibraryPaths: vi.fn().mockReturnValue(of(listing)),
      getLoggingLevel: vi.fn().mockReturnValue(of(logLevel)),
      getUpdateCheck: vi.fn().mockReturnValue(of(updateStatus)),
    };
    const authSpy = { currentUser: () => null, isAdmin: () => true };

    TestBed.configureTestingModule({
      imports: [AdminComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: authSpy },
      ],
    });

    const fixture = TestBed.createComponent(AdminComponent);
    fixture.detectChanges(); // ngOnInit → the loads above

    // Open the directory browser (mirrors "Browse…" in the Register New Library form).
    fixture.componentInstance.toggleBrowser();
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    const row = el.querySelector<HTMLButtonElement>('.browser-entry');
    return { fixture, apiSpy, row };
  }

  it('renders the row as a real, natively-focusable <button> (not a bare <div>)', () => {
    const { row } = setup({ name: 'Folder A', path: '/library-root/Folder A', hasChildren: false });
    expect(row).not.toBeNull();
    expect(row!.tagName).toBe('BUTTON');
    expect(row!.getAttribute('type')).toBe('button');
    // Native buttons are focusable and keyboard-activatable (Enter/Space) by the
    // UA without a tabindex - never disabled here, so it stays in the tab order.
    expect(row!.disabled).toBe(false);
  });

  it('clicking the row selects a leaf entry (no subfolders) exactly like the "Select" button', () => {
    const { fixture, row } = setup({ name: 'Leaf', path: '/library-root/Leaf', hasChildren: false });
    row!.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.newLibPath()).toBe('/library-root/Leaf');
    expect(fixture.componentInstance.browserOpen()).toBe(false);
  });

  it('clicking the row browses into an entry that has subfolders', () => {
    const { fixture, apiSpy, row } = setup({ name: 'Parent', path: '/library-root/Parent', hasChildren: true });
    row!.click();
    fixture.detectChanges();
    expect(apiSpy.browseLibraryPaths).toHaveBeenCalledWith('/library-root/Parent');
    // Still open - browsing into a folder keeps the picker open (only selecting closes it).
    expect(fixture.componentInstance.browserOpen()).toBe(true);
  });
});
