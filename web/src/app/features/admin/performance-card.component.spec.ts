import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { PerformanceCardComponent, formatBytes } from './performance-card.component';
import { PerformanceSettingsDto } from '../../core/api/api-types';

/**
 * Performance card (1.42.0). The API is mocked at the HTTP layer, so the real request shapes are asserted: a change
 * is a partial PUT, choosing the default is a reset, and a value pinned by the server configuration cannot be changed.
 */
describe('PerformanceCardComponent', () => {
  const URL = '/api/v1/admin/performance';
  const GiB = 1024 * 1024 * 1024;
  let httpMock: HttpTestingController;

  const dto = (overrides: Partial<PerformanceSettingsDto> = {}): PerformanceSettingsDto => ({
    mediaWorkers: 4, mediaWorkersSource: 'default', mediaWorkersDefault: 4, mediaWorkersMin: 1, mediaWorkersMax: 8,
    processorCount: 16,
    pageCacheBytes: GiB, pageCacheSource: 'default', pageCacheDefaultBytes: GiB,
    pageCacheMinBytes: 256 * 1024 * 1024, pageCacheMaxBytes: 64 * GiB, pageCacheUsageBytes: 300 * 1024 * 1024,
    backgroundWhileReading: 'slow', backgroundWhileReadingSource: 'default', backgroundWhileReadingDefault: 'slow',
    ...overrides,
  });

  function createLoaded(initial = dto()) {
    TestBed.configureTestingModule({
      imports: [PerformanceCardComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(PerformanceCardComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    httpMock.expectOne(URL).flush(initial);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => httpMock?.verify());

  it('shows the three settings with their defaults and the cache usage', () => {
    const fixture = createLoaded();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Media workers');
    expect(text).toContain('Page cache size');
    expect(text).toContain('Background work while someone reads');
    expect(text).toContain('16 processors');
    expect(text).toContain('300 MB used');
    expect(fixture.componentInstance.workerOptions()).toEqual([1, 2, 3, 4, 5, 6, 7, 8]);
    expect(fixture.componentInstance.cacheOptions()[0]).toBe(256 * 1024 * 1024);
  });

  it('saves a change as a partial update and shows the result', () => {
    const fixture = createLoaded();
    fixture.componentInstance.setWorkers(6);
    const req = httpMock.expectOne(URL);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ mediaWorkers: 6 });
    req.flush(dto({ mediaWorkers: 6, mediaWorkersSource: 'settings' }));
    fixture.detectChanges();
    expect(fixture.componentInstance.settings()?.mediaWorkers).toBe(6);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Saved');
  });

  it('choosing the default resets the stored value', () => {
    const fixture = createLoaded(dto({ pageCacheBytes: 4 * GiB, pageCacheSource: 'settings', backgroundWhileReading: 'pause', backgroundWhileReadingSource: 'settings' }));
    fixture.componentInstance.setCache(GiB);
    httpMock.expectOne(URL).flush(dto());
    fixture.componentInstance.setBackground('slow');
    const req = httpMock.expectOne(URL);
    expect(req.request.body).toEqual({ reset: ['backgroundWhileReading'] });
    req.flush(dto());
  });

  it('shows a configured value as pinned and disables its control', () => {
    const fixture = createLoaded(dto({ mediaWorkers: 12, mediaWorkersSource: 'configuration' }));
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Set by the server configuration.');
    const select = el.querySelector('[data-testid="perf-workers"]');
    expect(select?.getAttribute('aria-disabled')).toBe('true');
    // A configured value outside 1-8 is still shown.
    expect(fixture.componentInstance.workerOptions()).toContain(12);
  });

  it('shows the server error when a save is refused', () => {
    const fixture = createLoaded();
    fixture.componentInstance.setBackground('normal');
    httpMock.expectOne(URL).flush({ error: 'managed_by_config', message: 'This setting is set by server configuration.' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('set by server configuration');
  });

  it('formats sizes in MB and GB', () => {
    expect(formatBytes(256 * 1024 * 1024)).toBe('256 MB');
    expect(formatBytes(GiB)).toBe('1 GB');
    expect(formatBytes(1.5 * GiB)).toBe('1.5 GB');
  });
});
