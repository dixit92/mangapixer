import { vi } from 'vitest';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { HomeComponent } from './home.component';
import { AuthService } from '../../core/auth/auth.service';
import { FIRST_ANSWER_SPINNER_DELAY_MS } from '../../shared/first-answer';

beforeEach(() => localStorage.clear());

/**
 * 1.53.0 (visual audit V2): Home's first request - the libraries - is never read as "No libraries yet": not loaded shows the series page's
 * spinner after 300 ms, a failure shows the reader's error block with "Try again", and the empty line (only after a successful answer)
 * sends only an admin to Administration.
 */
describe('HomeComponent first-answer states', () => {
  let httpMock: HttpTestingController;

  function create(admin = true) {
    TestBed.configureTestingModule({
      imports: [HomeComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideNoopAnimations(),
        { provide: AuthService, useValue: { isAdmin: signal(admin) } },
      ],
    });
    const fixture = TestBed.createComponent(HomeComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    // Everything but the libraries answers at once (empty).
    httpMock.expectOne((r) => r.url === '/api/v1/reading/continue').flush([]);
    httpMock.expectOne((r) => r.url === '/api/v1/home/recent-chapters').flush({ libraries: [] });
    httpMock.expectOne('/api/v1/reading/library-preferences').flush({ viewMode: 'card', density: 'comfortable', sort: 'name', direction: 'asc' });
    httpMock.expectOne('/api/v1/reading/home-libraries').flush({ excludedLibraryIds: [] });
    httpMock.expectOne((r) => r.url === '/api/v1/recommendations').flush({ items: [] });
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`);

  beforeEach(() => vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] }));
  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  it('a slow answer shows the spinner after the delay, never "No libraries yet"', () => {
    const { fixture, el } = create();
    expect(q(el, 'home-libraries-loading')).toBeNull(); // a fast answer shows nothing in between
    expect(el.textContent).not.toContain('No libraries yet');
    vi.advanceTimersByTime(FIRST_ANSWER_SPINNER_DELAY_MS);
    fixture.detectChanges();
    expect(q(el, 'home-libraries-loading')?.querySelector('mat-spinner')).not.toBeNull();
    expect(el.textContent).not.toContain('No libraries yet');
    expect(el.textContent).not.toContain('Loading…');

    httpMock.expectOne('/api/v1/libraries').flush([{ id: 'L1', name: 'Alpha', isScanning: false, itemCount: 3, lastScanCompleted: null }]);
    fixture.detectChanges();
    expect(q(el, 'home-libraries-loading')).toBeNull();
    expect(el.querySelectorAll('.library-card').length).toBe(1);
  });

  it('a failed answer shows the error block; Try again asks again and shows the libraries', () => {
    const { fixture, el } = create();
    httpMock.expectOne('/api/v1/libraries').flush({ error: 'boom' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    const block = q(el, 'home-libraries-failed') as HTMLElement;
    expect(block).not.toBeNull();
    expect(block.querySelector('mat-icon')?.textContent).toContain('error_outline');
    expect(block.textContent).toContain('Could not load your libraries.');
    expect(el.textContent).not.toContain('No libraries yet');

    (q(el, 'home-libraries-retry') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(q(el, 'home-libraries-failed')).toBeNull();
    httpMock.expectOne('/api/v1/libraries').flush([{ id: 'L1', name: 'Alpha', isScanning: false, itemCount: 3, lastScanCompleted: null }]);
    fixture.detectChanges();
    expect(el.querySelectorAll('.library-card').length).toBe(1);
  });

  it('empty after a successful answer: an admin is pointed to Administration', () => {
    const { fixture, el } = create(true);
    httpMock.expectOne('/api/v1/libraries').flush([]);
    fixture.detectChanges();
    const line = q(el, 'home-libraries-empty') as HTMLElement;
    expect(line.textContent).toContain('No libraries yet');
    expect(line.querySelector('a[href="/admin"]')).not.toBeNull();
  });

  it('empty for a reader: no Administration link it cannot open', () => {
    const { fixture, el } = create(false);
    httpMock.expectOne('/api/v1/libraries').flush([]);
    fixture.detectChanges();
    const line = q(el, 'home-libraries-empty') as HTMLElement;
    expect(line.textContent?.trim()).toBe('Ask an administrator for access to a library.');
    expect(el.querySelector('a[href="/admin"]')).toBeNull();
  });
});
