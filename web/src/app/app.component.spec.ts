import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { AppComponent } from './app.component';

/**
 * Footer version display. The app shell fetches
 * GET /api/v1/system/info on init and renders the version in a small footer.
 * These tests flush the HTTP request via HttpTestingController and assert the
 * rendered DOM.
 */
describe('AppComponent footer version', () => {
  function setup() {
    TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
      ],
    });
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges(); // ngOnInit → fires GET /api/v1/system/info
    return { fixture, httpMock: TestBed.inject(HttpTestingController) };
  }

  it('renders the product version in the footer', () => {
    const { fixture, httpMock } = setup();
    const req = httpMock.expectOne('/api/v1/system/info');
    expect(req.request.method).toBe('GET');
    req.flush({ version: '1.3.0+sha.abc123' });
    fixture.detectChanges();

    const footer = fixture.nativeElement.querySelector('.app-footer') as HTMLElement;
    expect(footer).toBeTruthy();
    expect(footer.textContent).toContain('MangaPlex');
    expect(footer.textContent).toContain('1.3.0');
  });

  it('hides the footer when the request fails', () => {
    const { fixture, httpMock } = setup();
    const req = httpMock.expectOne('/api/v1/system/info');
    req.flush('error', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    const footer = fixture.nativeElement.querySelector('.app-footer');
    expect(footer).toBeNull();
  });

  it('hides the footer before the version loads', () => {
    const { fixture, httpMock } = setup();
    // Request is pending but not flushed yet
    fixture.detectChanges();
    const footer = fixture.nativeElement.querySelector('.app-footer');
    expect(footer).toBeNull();
    // Consume the pending request so HttpTestingController doesn't complain.
    httpMock.expectOne('/api/v1/system/info').flush({ version: '1.3.0' });
  });
});
