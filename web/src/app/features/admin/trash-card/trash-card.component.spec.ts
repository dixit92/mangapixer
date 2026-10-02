import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { TrashCardComponent } from './trash-card.component';
import { TrashCountsDto, TrashLibraryDto, TrashOverviewDto } from '../../../core/api/api-types';

/**
 * Trash card (1.31.0). The API is mocked at the HTTP layer so the real ApiService request shapes are asserted: the
 * retention select PUTs the settings, the automatic cleaning status line points at Scheduled jobs (the switch and the hour live
 * there since 1.32.0), "Empty trash now" / one library's empty (releasing its hold) / "Clean bundles
 * now" POST after a confirm, and every action reloads the preview.
 */
describe('TrashCardComponent', () => {
  const URL = '/api/v1/admin/trash';
  let httpMock: HttpTestingController;

  const counts = (nodes: number, overrides: Partial<TrashCountsDto> = {}): TrashCountsDto => ({
    nodes,
    archives: nodes,
    folders: 0,
    userStateRows: 0,
    files: nodes,
    bytes: nodes * 2048,
    ...overrides,
  });

  const library = (id: string, nodes: number, hold: TrashLibraryDto['hold'] = null): TrashLibraryDto => ({
    libraryId: id,
    name: `Library ${id}`,
    eligible: counts(nodes),
    waiting: 1,
    libraryNodes: 10,
    hold,
    holdReleasable: hold === 'burst' || hold === 'root_unavailable',
  });

  const overview = (overrides: Partial<TrashOverviewDto> = {}): TrashOverviewDto => ({
    settings: { automaticCleaning: false, retentionDays: 30, allowedRetentionDays: [1, 7, 30, 90, 365], automaticHour: 4 },
    windowStart: '2026-09-01T12:00:00Z',
    libraries: [library('a', 3), library('b', 6, 'burst')],
    total: counts(3, { userStateRows: 4 }),
    bundles: { files: 2, bytes: 4096 },
    lastEmpty: null,
    lastBundleClean: null,
    ...overrides,
  });

  function createLoaded(initial = overview()) {
    TestBed.configureTestingModule({
      imports: [TrashCardComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(TrashCardComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    httpMock.expectOne((r) => r.method === 'GET' && r.url === URL).flush(initial);
    fixture.detectChanges();
    return fixture;
  }

  const text = (fixture: { nativeElement: HTMLElement }) => (fixture.nativeElement.textContent ?? '').replace(/\s+/g, ' ');

  afterEach(() => httpMock?.verify());

  it('shows the retention, the libraries with their holds, and what is ready', () => {
    const fixture = createLoaded();
    const el: HTMLElement = fixture.nativeElement;
    const select = el.querySelector<HTMLSelectElement>('[data-testid="trash-retention"]')!;
    expect(select.value).toBe('30');
    expect(Array.from(select.options).map((o) => o.textContent?.trim())).toEqual([
      'Daily (1 day)', 'Weekly (7 days)', 'Monthly (30 days)', 'Quarterly (90 days)', 'Yearly (365 days)',
    ]);
    expect(text(fixture)).toContain('recognised and keeps its reading state');
    expect(el.querySelector('[data-testid="trash-lib-b"] [data-hold="burst"]')).not.toBeNull();
    expect(text(fixture)).toContain('More than half of this library would go');
    expect(text(fixture)).toContain('Ready to empty: 3 items (3 files) · 4 reading-state entries · 6.0 KB');
    expect(text(fixture)).toContain('Last emptied: never');
  });

  it('says so when nothing was removed', () => {
    const fixture = createLoaded(overview({ libraries: [], total: counts(0), bundles: { files: 0, bytes: 0 } }));
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="trash-empty-state"]')).not.toBeNull();
    expect(el.querySelector<HTMLButtonElement>('[data-testid="trash-empty-all"]')!.disabled).toBe(true);
    expect(el.querySelector<HTMLButtonElement>('[data-testid="trash-clean"]')!.disabled).toBe(true);
  });

  it('the automatic cleaning status line says Off, or the daily hour, and holds no switch or hour select (1.32.0)', () => {
    const off = createLoaded();
    const el: HTMLElement = off.nativeElement;
    const status = (e: HTMLElement) => (e.querySelector('[data-testid="trash-auto-status"]')!.textContent ?? '').replace(/\s+/g, ' ');
    expect(status(el)).toContain('Automatic cleaning: Off');
    expect(el.querySelector('[data-testid="trash-auto"]')).toBeNull();
    expect(el.querySelector('[data-testid="trash-hour"]')).toBeNull();
    httpMock.verify();
    TestBed.resetTestingModule();

    const on = createLoaded(overview({ settings: { ...overview().settings, automaticCleaning: true, automaticHour: 22 } }));
    expect(status(on.nativeElement)).toContain('Automatic cleaning: daily at 22:00');
  });

  it('the status line link scrolls to and focuses the Scheduled jobs row', () => {
    const fixture = createLoaded();
    const row = document.createElement('li');
    row.id = 'job-trash';
    row.innerHTML = '<span data-testid="job-trash-auto"><button type="button">toggle</button></span>';
    document.body.appendChild(row);
    const scroll = vi.fn();
    row.scrollIntoView = scroll;
    try {
      (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="trash-auto-link"]')!.click();
      expect(scroll).toHaveBeenCalled();
      expect(document.activeElement).toBe(row.querySelector('button'));
    } finally {
      row.remove();
    }
  });

  it('a new retention is saved and the preview reloads', () => {
    const fixture = createLoaded();
    fixture.componentInstance.setRetention(7);
    const put = httpMock.expectOne(`${URL}/settings`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ retentionDays: 7 });
    put.flush({ ...overview().settings, retentionDays: 7 });
    httpMock.expectOne((r) => r.method === 'GET' && r.url === URL).flush(overview({ settings: { ...overview().settings, retentionDays: 7 } }));
    fixture.detectChanges();
    expect(fixture.componentInstance.overview()!.settings.retentionDays).toBe(7);
    expect(fixture.componentInstance.message()).toContain('weekly (7 days)');
  });

  it('"Empty trash now" confirms with the preview, then empties every library without a hold', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;
    (fixture.nativeElement.querySelector('[data-testid="trash-empty-all"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const confirm: HTMLElement = fixture.nativeElement.querySelector('[data-testid="trash-confirm"]');
    expect(confirm.textContent).toContain('This removes 3 items');
    expect(confirm.textContent).toContain('1 held library keeps its trash.');

    (fixture.nativeElement.querySelector('[data-testid="trash-confirm-yes"]') as HTMLButtonElement).click();
    const post = httpMock.expectOne(`${URL}/empty`);
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual({ libraryId: null, releaseHold: false });
    post.flush({ removed: counts(3), held: [{ libraryId: 'b', hold: 'burst' }] });
    httpMock.expectOne((r) => r.method === 'GET' && r.url === URL).flush(overview({ libraries: [library('b', 6, 'burst')] }));
    fixture.detectChanges();
    expect(c.message()).toBe('Removed 3 items (3 files) · 6.0 KB. 1 held library was skipped.');
  });

  it("one held library's empty names the hold and releases it after the confirm", () => {
    const fixture = createLoaded();
    (fixture.nativeElement.querySelector('[data-testid="trash-empty-lib-b"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const confirm: HTMLElement = fixture.nativeElement.querySelector('[data-testid="trash-confirm"]');
    expect(confirm.textContent).toContain('Empty the trash of Library b?');
    expect(confirm.textContent).toContain('Empty it anyway?');
    fixture.componentInstance.confirm();
    const post = httpMock.expectOne(`${URL}/empty`);
    expect(post.request.body).toEqual({ libraryId: 'b', releaseHold: true });
    post.flush({ removed: counts(6), held: [] });
    httpMock.expectOne((r) => r.method === 'GET' && r.url === URL).flush(overview({ libraries: [library('a', 3)] }));
  });

  it('"Clean bundles now" posts after the confirm and shows what went', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;
    c.ask({ kind: 'clean' });
    fixture.detectChanges();
    expect(text(fixture)).toContain('This removes 2 unused files (4.0 KB).');
    c.confirm();
    const post = httpMock.expectOne(`${URL}/clean-bundles`);
    expect(post.request.method).toBe('POST');
    post.flush({ files: 2, bytes: 4096 });
    httpMock.expectOne((r) => r.method === 'GET' && r.url === URL).flush(overview({ bundles: { files: 0, bytes: 0 } }));
    expect(c.message()).toBe('Removed 2 unused files (4.0 KB).');
  });

  it('a refused empty shows the server message and reloads', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;
    c.ask({ kind: 'empty-library', library: library('a', 3) });
    c.confirm();
    httpMock.expectOne(`${URL}/empty`).flush(
      { error: 'scan_in_progress', message: 'A scan of this library is running. Empty its trash after the scan.' },
      { status: 409, statusText: 'Conflict' });
    httpMock.expectOne((r) => r.method === 'GET' && r.url === URL).flush(overview());
    expect(c.error()).toContain('A scan of this library is running');
  });
});
