import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { AdminRatingSharingCardComponent, SHARED_RATINGS_CONFIRMATION } from './admin-rating-sharing-card.component';
import { RatingSharingService } from './rating-sharing.service';

/** 1.53.0 lane S: Administration's "Shared ratings" switch - off by default, ticking asks first (the owner's text), off at once. */
describe('AdminRatingSharingCardComponent', () => {
  const URL = '/api/v1/admin/ratings/sharing';
  let httpMock: HttpTestingController | undefined;

  function create(enabled: boolean) {
    TestBed.configureTestingModule({
      imports: [AdminRatingSharingCardComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(AdminRatingSharingCardComponent);
    const http = TestBed.inject(HttpTestingController);
    httpMock = http;
    fixture.detectChanges();
    http.expectOne(URL).flush({ enabled });
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => httpMock?.verify());

  const box = (el: HTMLElement) => el.querySelector<HTMLInputElement>('[data-testid="admin-rating-sharing"] input[type="checkbox"]')!;

  it('carries the owner\'s confirmation text, word for word', () => {
    expect(SHARED_RATINGS_CONFIRMATION).toBe("Every user's ratings will count in an average and a count on each series and volume - never who rated " +
      "what. Ratings in a user's Private libraries never count. Each user can turn sharing off for themselves in Settings.");
  });

  it('ticking asks first; Cancel sends nothing and leaves it off', () => {
    const { fixture, el } = create(false);
    expect(box(el).checked).toBe(false);
    box(el).click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="admin-rating-sharing-confirm"]')!.textContent).toContain(SHARED_RATINGS_CONFIRMATION);
    httpMock!.expectNone(URL);
    (el.querySelector('[data-testid="admin-rating-sharing-cancel"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="admin-rating-sharing-confirm"]')).toBeNull();
    expect(box(el).checked).toBe(false);
  });

  it('"Turn on shared ratings" PUTs enabled and tells the client state', () => {
    const { fixture, el } = create(false);
    box(el).click();
    fixture.detectChanges();
    (el.querySelector('[data-testid="admin-rating-sharing-confirm-yes"]') as HTMLButtonElement).click();
    const put = httpMock!.expectOne(URL);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ enabled: true });
    put.flush({ enabled: true });
    fixture.detectChanges();
    expect(box(el).checked).toBe(true);
    expect(TestBed.inject(RatingSharingService).serverEnabled()).toBe(true);
  });

  it('unticking turns it off at once, without a question', () => {
    const { fixture, el } = create(true);
    expect(box(el).checked).toBe(true);
    box(el).click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="admin-rating-sharing-confirm"]')).toBeNull();
    const put = httpMock!.expectOne(URL);
    expect(put.request.body).toEqual({ enabled: false });
    put.flush({ enabled: false });
  });
});
