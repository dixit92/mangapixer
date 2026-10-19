import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { By } from '@angular/platform-browser';
import { NEVER, of } from 'rxjs';

import { AdminComponent } from './admin.component';
import { ScheduledJobsComponent } from './scheduled-jobs/scheduled-jobs.component';
import { MatTooltip } from '@angular/material/tooltip';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import {
  AnalyticsOverviewDto,
  AuditTrailPageDto,
  BackupSettingsDto,
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
    const analyticsOverview: AnalyticsOverviewDto = {
      generatedAt: '2026-09-23T00:00:00Z',
      libraryCount: 0, totalNodeCount: 0, archiveNodeCount: 0, folderNodeCount: 0,
      tombstonedNodeCount: 0, analyzedItemCount: 0, pendingItemCount: 0, failedItemCount: 0,
      userCount: 0, activeUserCount: 0, adminCount: 0, pendingActivationCount: 0,
      readingProgressCount: 0, completedItemCount: 0, inProgressItemCount: 0,
      bookmarkCount: 0, favoriteCount: 0, activeSessionCount: 0,
    };
    const backupSettings: BackupSettingsDto = {
      enabled: true, enabledSource: 'default', intervalHours: 24, intervalHoursSource: 'default',
      retentionCount: 5, retentionCountSource: 'default', locationKind: 'default', locationSource: 'default',
      customLocation: null, locationChangeAllowed: true, locationStatus: 'ok', platform: null,
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
      // The Trash card (1.31.0) loads its own overview; these tests are about other cards, so it just stays loading.
      getTrash: vi.fn().mockReturnValue(NEVER),
      // The Scheduled jobs section (1.32.0) loads its own list; it stays loading here too.
      getScheduledJobs: vi.fn().mockReturnValue(NEVER),
      // The API tokens card (1.33.0) loads its own list; it stays loading here too.
      listApiTokens: vi.fn().mockReturnValue(NEVER),
      // The Performance card (1.42.0) loads its own settings; it stays loading here too.
      getPerformanceSettings: vi.fn().mockReturnValue(NEVER),
      // The Volume contents card (1.44.0) loads its own status; it stays loading here too.
      getVolumeScanStatus: vi.fn().mockReturnValue(NEVER),
      getAnalyticsOverview: vi.fn().mockReturnValue(of(analyticsOverview)),
      getAnalyticsUsers: vi.fn().mockReturnValue(of([])),
      getBackupSettings: vi.fn().mockReturnValue(of(backupSettings)),
      getBackupSnapshotMove: vi.fn().mockReturnValue(of({ state: 'idle', fromKind: null, toKind: null, totalFiles: 0, filesDone: 0, totalBytes: 0, bytesDone: 0, movedCount: 0, prunedCount: 0, startedUtc: null, finishedUtc: null, issues: [] })),
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

  it('shows "Inherit" in a library\'s Direction box when it has no default of its own', async () => {
    const { fixture, apiSpy } = setup({ name: 'x', path: '/library-root/x', hasChildren: false });
    // Each row's auto-scan control loads its library.
    Object.assign(apiSpy, { getLibrary: vi.fn((id: string) => of({ id, name: id, scanSchedule: 'Daily' } as unknown as LibraryDto)) });
    fixture.componentInstance.libraries.set([
      { id: 'l1', name: 'Shelf', itemCount: 3, defaultReaderMode: null } as unknown as LibraryDto,
      { id: 'l2', name: 'Strips', itemCount: 2, defaultReaderMode: 'VerticalWebtoon' } as unknown as LibraryDto,
    ]);
    fixture.detectChanges();
    await fixture.whenStable(); // mat-select settles its selected label after its options
    fixture.detectChanges();
    const shown = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.dir-select .mat-mdc-select-value')]
      .map((e) => e.textContent!.trim());
    expect(shown[0]).toBe('Inherit');
    expect(shown[1]).toBe('Vertical');
  });

  it('1.44.0: a scanning library keeps its icon row - the scan button greyed, the row spins, Cancel scan in its line', () => {
    const { fixture, apiSpy } = setup({ name: 'x', path: '/library-root/x', hasChildren: false });
    Object.assign(apiSpy, { getLibrary: vi.fn((id: string) => of({ id, name: id, scanSchedule: 'Daily' } as unknown as LibraryDto)) });
    fixture.componentInstance.libraries.set([
      { id: 'l1', name: 'Shelf', itemCount: 3, defaultReaderMode: null, isScanning: true } as unknown as LibraryDto,
      { id: 'l2', name: 'Strips', itemCount: 2, defaultReaderMode: null, isScanning: false } as unknown as LibraryDto,
    ]);
    fixture.detectChanges();
    const [scanning, idle] = [...(fixture.nativeElement as HTMLElement).querySelectorAll('mat-list-item')] as HTMLElement[];
    const labels = (row: HTMLElement) => [...row.querySelectorAll('.lib-meta button')].map((b) => b.getAttribute('aria-label'));

    // The same icons in the same places: only the scan button's state differs.
    expect(labels(scanning)).toEqual(['Regenerate thumbnails', 'Scan in progress', 'Change icon', 'Rename library', 'Remove library']);
    expect(labels(idle)).toEqual(['Regenerate thumbnails', 'Scan now', 'Change icon', 'Rename library', 'Remove library']);
    // 1.44.0 (UX review): disabledInteractive so the greyed button still shows its "Scan in progress" tooltip - aria-disabled, and a
    // click on it starts nothing.
    const busy = scanning.querySelector('[data-testid="lib-scan"]') as HTMLButtonElement;
    expect(busy.getAttribute('aria-disabled')).toBe('true');
    const scan = vi.spyOn(fixture.componentInstance, 'triggerScan');
    busy.click();
    expect(scan).not.toHaveBeenCalled();
    expect(idle.querySelector('[data-testid="lib-scan"]')!.getAttribute('aria-disabled')).not.toBe('true');
    expect(scanning.querySelector('.chip')).toBeNull();

    // The library shows the scan: a spinning ring on its icon, "Scanning…" and Cancel scan in its line, and (1.45.0) the tint and ONE bar
    // on the whole block - the title row AND its Auto-scan / Declared lines, the bar after them (it was drawn between them).
    const [scanBlock, idleBlock] = [...(fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="lib-block"]')] as HTMLElement[];
    expect(scanBlock.contains(scanning)).toBe(true);
    expect(scanBlock.classList).toContain('scanning');
    expect(scanBlock.querySelector('app-library-scan-schedule')).not.toBeNull();
    expect(scanBlock.querySelector('app-library-declared-facts')).not.toBeNull();
    expect(scanBlock.querySelectorAll('[data-testid="lib-scan-bar"]')).toHaveLength(1);
    expect(scanBlock.lastElementChild!.getAttribute('data-testid')).toBe('lib-scan-bar');
    expect(scanning.querySelector('[data-testid="lib-scan-bar"]')).toBeNull();
    // The ring sits on the glyph itself (a box of the icon's size), not on the list's icon slot.
    expect(scanning.querySelector('.lib-glyph [data-testid="lib-scan-ring"]')).not.toBeNull();
    expect(scanning.querySelector('.lib-line')!.textContent).toContain('Scanning…');
    expect(scanning.querySelector('.lib-line [data-testid="lib-cancel-scan"]')!.textContent!.trim()).toBe('Cancel scan');
    // A real gap: the dot is its own spaced element, never glued to "Scanning…" ("Scanning…·").
    const label = scanning.querySelector('.lib-line .scan-label')!;
    expect(label.nextElementSibling!.firstElementChild!.classList).toContain('scan-sep');
    expect(idleBlock.classList).not.toContain('scanning');
    expect(idleBlock.querySelector('[data-testid="lib-scan-bar"]')).toBeNull();
    expect(idleBlock.querySelector('[data-testid="lib-scan-ring"]')).toBeNull();
    expect(idle.querySelector('[data-testid="lib-cancel-scan"]')).toBeNull();
    // One library scanning: no summary line.
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="lib-scan-summary"]')).toBeNull();
  });

  it('1.45.0: with several libraries scanning, one line over the list says how many and Cancel all stops each known run once', () => {
    const { fixture, apiSpy } = setup({ name: 'x', path: '/library-root/x', hasChildren: false });
    const cancelScan = vi.fn().mockReturnValue(of(undefined));
    Object.assign(apiSpy, {
      getLibrary: vi.fn((id: string) => of({ id, name: id, scanSchedule: 'Daily' } as unknown as LibraryDto)),
      getScanHistory: vi.fn((id: string) => of(id === 'l3' ? [] : [{ id: `run-${id}`, status: 'running' }])),
      cancelScan,
    });
    const c = fixture.componentInstance;
    c.libraries.set([
      { id: 'l1', name: 'Shelf', itemCount: 3, isScanning: true } as unknown as LibraryDto,
      { id: 'l2', name: 'Strips', itemCount: 2, isScanning: true } as unknown as LibraryDto,
      { id: 'l3', name: 'Comics', itemCount: 1, isScanning: true } as unknown as LibraryDto,
      { id: 'l4', name: 'Idle', itemCount: 1, isScanning: false } as unknown as LibraryDto,
    ]);
    // The runs are learned as on page load (l3's is not known yet).
    for (const id of ['l1', 'l2', 'l3']) (c as unknown as { backfillScanRunId(id: string): void }).backfillScanRunId(id);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const summary = el.querySelector('[data-testid="lib-scan-summary"]')!;
    expect(summary.textContent!.replace(/\s+/g, ' ').trim()).toMatch(/^3 libraries scanning ?· ?Cancel all$/);
    expect(summary.querySelector('.scan-sep')).not.toBeNull();
    // Over the list, not inside it.
    expect(summary.nextElementSibling!.tagName.toLowerCase()).toBe('mat-list');
    // Every scanning block has its own bar and ring; the idle one none.
    expect(el.querySelectorAll('[data-testid="lib-scan-bar"]')).toHaveLength(3);
    expect(el.querySelectorAll('[data-testid="lib-scan-ring"]')).toHaveLength(3);

    (summary.querySelector('[data-testid="lib-cancel-all"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(cancelScan.mock.calls.map((a) => a[0])).toEqual(['run-l1', 'run-l2']);
    // Pressed again: nothing more to cancel (the two are cancelling, l3's run is unknown).
    expect(c.canCancelAll()).toBe(false);
    expect((summary.querySelector('[data-testid="lib-cancel-all"]') as HTMLButtonElement).disabled).toBe(true);
    c.cancelAll();
    expect(cancelScan).toHaveBeenCalledTimes(2);
  });

  it('1.45.0: a library that starts scanning between two polls gets its run, so Cancel scan and Cancel all work for it', () => {
    const { fixture, apiSpy } = setup({ name: 'x', path: '/library-root/x', hasChildren: false });
    const getScanHistory = vi.fn((id: string) => of([{ id: `run-${id}`, status: 'running' }]));
    Object.assign(apiSpy, { getLibrary: vi.fn((id: string) => of({ id, name: id, scanSchedule: 'Daily' } as unknown as LibraryDto)), getScanHistory });
    const c = fixture.componentInstance;
    c.libraries.set([{ id: 'l1', name: 'Shelf', itemCount: 3, isScanning: false } as unknown as LibraryDto]);
    apiSpy.getAllLibraries.mockReturnValue(of([{ id: 'l1', name: 'Shelf', itemCount: 3, isScanning: true } as unknown as LibraryDto]));
    (c as unknown as { refreshLibraries(): void }).refreshLibraries();
    fixture.detectChanges();
    expect(getScanHistory).toHaveBeenCalledWith('l1');
    expect(c.scanRunId('l1')).toBe('run-l1');
    (c as unknown as { stopPolling(): void }).stopPolling();
  });

  it('1.45.0: the "Scan all libraries" tooltip opens above the button (below, it covered the first library\'s name)', () => {
    const { fixture, apiSpy } = setup({ name: 'x', path: '/library-root/x', hasChildren: false });
    Object.assign(apiSpy, { getLibrary: vi.fn((id: string) => of({ id, name: id, scanSchedule: 'Daily' } as unknown as LibraryDto)) });
    fixture.componentInstance.libraries.set([{ id: 'l1', name: 'Shelf', itemCount: 3, isScanning: false } as unknown as LibraryDto]);
    fixture.detectChanges();
    const button = fixture.debugElement.query(By.css('.lib-actions-bar button'));
    const tip = button.injector.get(MatTooltip);
    expect(tip.position).toBe('above');
  });

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

  it('lays the cards out in the grid, and a change to the trash schedule reloads the Trash card (1.32.0)', () => {
    const { fixture, apiSpy } = setup({ name: 'x', path: '/library-root/x', hasChildren: false });
    const el: HTMLElement = fixture.nativeElement;
    const grid = el.querySelector('[data-testid="admin-grid"]')!;
    expect(grid).not.toBeNull();
    // Libraries and Scheduled jobs span the row; the rest sit in two columns.
    expect(grid.querySelectorAll(':scope > .col').length).toBe(4);
    expect(grid.querySelector(':scope > app-scheduled-jobs.wide')).not.toBeNull();
    expect(grid.querySelector(':scope > .wide')).not.toBeNull();
    expect(grid.querySelector('.col app-trash-card')).not.toBeNull();

    const loads = apiSpy.getTrash.mock.calls.length;
    fixture.debugElement.query(By.directive(ScheduledJobsComponent)).componentInstance.trashChanged.emit();
    expect(apiSpy.getTrash.mock.calls.length).toBe(loads + 1);
  });
});
