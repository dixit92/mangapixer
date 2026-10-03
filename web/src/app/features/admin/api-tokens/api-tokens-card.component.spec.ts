import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { ApiTokensCardComponent } from './api-tokens-card.component';
import { ApiTokenDto, CreateApiTokenResponse } from '../../../core/api/api-types';

/**
 * API tokens card (1.33.0). The API is mocked at the HTTP layer so the real ApiService request shapes are asserted: the list,
 * create (name + expiry, default 1 year, "Never" sends null) with the secret shown once and copied, and revoke after a confirm.
 */
describe('ApiTokensCardComponent', () => {
  const URL = '/api/v1/admin/tokens';
  const SECRET = 'mpx_' + 'A'.repeat(43);
  let httpMock: HttpTestingController;

  const token = (id: string, overrides: Partial<ApiTokenDto> = {}): ApiTokenDto => ({
    id,
    name: `Token ${id}`,
    prefix: 'mpx_AbCd',
    scopes: ['metadata:read'],
    ownerUserName: 'admin',
    createdAt: '2026-10-01T10:00:00Z',
    expiresAt: '2027-10-01T10:00:00Z',
    lastUsedAt: null,
    revokedAt: null,
    status: 'active',
    ...overrides,
  });

  function createLoaded(initial: ApiTokenDto[] = [token('a'), token('b', { status: 'revoked', revokedAt: '2026-10-02T10:00:00Z' })]) {
    TestBed.configureTestingModule({
      imports: [ApiTokensCardComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(ApiTokensCardComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    httpMock.expectOne((r) => r.method === 'GET' && r.url === URL).flush(initial);
    fixture.detectChanges();
    return fixture;
  }

  const text = (fixture: { nativeElement: HTMLElement }) => (fixture.nativeElement.textContent ?? '').replace(/\s+/g, ' ');
  const q = <T extends Element>(fixture: { nativeElement: HTMLElement }, testId: string) =>
    fixture.nativeElement.querySelector<T>(`[data-testid="${testId}"]`);

  afterEach(() => httpMock?.verify());

  it('lists tokens with prefix, owner, status and last use, and offers revoke only for a token not yet revoked', () => {
    const fixture = createLoaded();
    expect(text(fixture)).toContain('Token a');
    expect(text(fixture)).toContain('mpx_AbCd…');
    expect(text(fixture)).toContain('by admin');
    expect(text(fixture)).toContain('Last used: never');
    expect(q(fixture, 'api-token-revoke-a')).not.toBeNull();
    expect(q(fixture, 'api-token-revoke-b')).toBeNull();
    expect(q(fixture, 'api-token-b')!.textContent).toContain('Revoked');
  });

  it('shows the empty state', () => {
    const fixture = createLoaded([]);
    expect(q(fixture, 'api-tokens-empty')).not.toBeNull();
  });

  it('creates a token with the default expiry of one year, shows the secret once with a copy button, and reloads', async () => {
    const fixture = createLoaded([]);
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    expect(q<HTMLSelectElement>(fixture, 'api-token-expiry')!.value).toBe('365');
    const create = q<HTMLButtonElement>(fixture, 'api-token-create')!;
    expect(create.disabled).toBe(true);

    fixture.componentInstance.name.set('  MangaList ');
    fixture.detectChanges();
    expect(create.disabled).toBe(false);
    create.click();

    const req = httpMock.expectOne((r) => r.method === 'POST' && r.url === URL);
    expect(req.request.body).toEqual({ name: 'MangaList', expiresInDays: 365 });
    const response: CreateApiTokenResponse = { token: token('c', { name: 'MangaList' }), secret: SECRET };
    req.flush(response);
    httpMock.expectOne((r) => r.method === 'GET' && r.url === URL).flush([response.token]);
    fixture.detectChanges();

    expect(q(fixture, 'api-token-secret-value')!.textContent).toBe(SECRET);
    expect(text(fixture)).toContain('You will not see it again');
    q<HTMLButtonElement>(fixture, 'api-token-copy')!.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledWith(SECRET);

    // Done removes the secret from the page for good; the list never shows it.
    q<HTMLButtonElement>(fixture, 'api-token-done')!.click();
    fixture.detectChanges();
    expect(q(fixture, 'api-token-secret')).toBeNull();
    expect(text(fixture)).not.toContain(SECRET);
  });

  it('sends null for a token that never expires', () => {
    const fixture = createLoaded([]);
    const select = q<HTMLSelectElement>(fixture, 'api-token-expiry')!;
    select.value = 'never';
    select.dispatchEvent(new Event('change'));
    fixture.componentInstance.name.set('Forever');
    fixture.detectChanges();
    q<HTMLButtonElement>(fixture, 'api-token-create')!.click();

    const req = httpMock.expectOne((r) => r.method === 'POST' && r.url === URL);
    expect(req.request.body).toEqual({ name: 'Forever', expiresInDays: null });
    req.flush({ token: token('d'), secret: SECRET } satisfies CreateApiTokenResponse);
    httpMock.expectOne((r) => r.method === 'GET' && r.url === URL).flush([]);
  });

  it('shows the server error when creating fails', () => {
    const fixture = createLoaded([]);
    fixture.componentInstance.name.set('x');
    fixture.detectChanges();
    q<HTMLButtonElement>(fixture, 'api-token-create')!.click();
    httpMock.expectOne((r) => r.method === 'POST' && r.url === URL)
      .flush({ error: 'invalid_name', message: 'Give the token a name of 1 to 64 characters.' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();
    expect(text(fixture)).toContain('Give the token a name of 1 to 64 characters.');
    expect(q(fixture, 'api-token-secret')).toBeNull();
  });

  it('revokes a token only after the confirm, then reloads', () => {
    const fixture = createLoaded();
    q<HTMLButtonElement>(fixture, 'api-token-revoke-a')!.click();
    fixture.detectChanges();
    expect(q(fixture, 'api-token-confirm-a')).not.toBeNull();
    httpMock.expectNone((r) => r.method === 'POST');

    q<HTMLButtonElement>(fixture, 'api-token-revoke-yes')!.click();
    httpMock.expectOne((r) => r.method === 'POST' && r.url === `${URL}/a/revoke`).flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne((r) => r.method === 'GET' && r.url === URL)
      .flush([token('a', { status: 'revoked', revokedAt: '2026-10-03T10:00:00Z' })]);
    fixture.detectChanges();
    expect(q(fixture, 'api-token-confirm-a')).toBeNull();
    expect(q(fixture, 'api-token-revoke-a')).toBeNull();
  });
});
