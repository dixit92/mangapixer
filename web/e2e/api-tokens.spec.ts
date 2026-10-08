import { test, expect, Page, APIRequestContext, request as playwrightRequest } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * API tokens (1.33.0): an admin creates a personal access token in Administration, sees the secret once, the list shows it
 * without the secret, the token reads the export ping (and nothing that needs a login), and after revoking it stops working.
 * The phone and tablet layout of the card with long names, the secret and the confirm step is in phone-layout.spec.ts.
 * 1.36.0: a token created with "Request library scans" starts a full library scan through POST /api/v1/export/libraries/{id}/scan
 * (202, the run shows in the library's scan history), stays refused on admin routes, and a browser cookie never reaches the scan
 * route; the card with the scope checkboxes fits a phone. The library is the synthetic fixture library (E2E_SERIES_FIXTURE_ROOT) or
 * any library the instance already has; without one that test skips.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const BASE_URL = process.env['E2E_BASE_URL'] ?? 'http://127.0.0.1:8091';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const LIBRARY_NAME = 'Series Fixtures';

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get('/api/v1/auth/csrf');
  const { token } = await res.json();
  return { 'X-MangaPixer-Csrf': token };
}

/** The fixture library (registered here when missing, as covers.spec.ts does), else any library; null when there is none. */
async function someLibrary(page: Page): Promise<string | null> {
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  const fixture = libs.find((l) => l.name === LIBRARY_NAME);
  if (fixture) return fixture.id;
  if (FIXTURE_ROOT) {
    const created = await page.request.post('/api/v1/admin/libraries', {
      headers: await csrf(page.request), data: { displayName: LIBRARY_NAME, rootPath: FIXTURE_ROOT },
    });
    expect(created.ok(), await created.text()).toBeTruthy();
    return (await created.json()).id;
  }
  return libs[0]?.id ?? null;
}

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

test('an admin creates a token, sees it once, uses it for the export only, and revokes it', async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/admin');
  const card = page.getByTestId('api-tokens-card');
  await card.scrollIntoViewIfNeeded();

  const name = `E2E token ${Date.now()}`;
  await card.getByTestId('api-token-name').fill(name);
  await card.getByTestId('api-token-expiry').selectOption('30');
  const created = page.waitForResponse((r) => r.request().method() === 'POST' && r.url().endsWith('/api/v1/admin/tokens'));
  await card.getByTestId('api-token-create').click();
  expect((await created).status()).toBe(200);

  const secret = (await card.getByTestId('api-token-secret-value').textContent())!.trim();
  expect(secret).toMatch(/^mpx_[A-Za-z0-9_-]{43}$/);
  await expect(card.getByTestId('api-token-secret')).toContainText('You will not see it again');
  await expect(card.getByText(name, { exact: true })).toBeVisible();
  await expectFitsScreen(page, 'Administration with a new token at 1280 px');

  // The list answer never carries the secret.
  const list = await (await page.request.get('/api/v1/admin/tokens')).text();
  expect(list).toContain(name);
  expect(list).not.toContain(secret.slice(4));

  // A client with no cookie: the token reads the export ping and nothing else.
  const app = await playwrightRequest.newContext({ baseURL: BASE_URL, extraHTTPHeaders: { Authorization: `Bearer ${secret}` } });
  try {
    const ping = await app.get('/api/v1/export/ping');
    expect(ping.status()).toBe(200);
    expect((await ping.json()).auth).toBe('token');
    expect((await app.get('/api/v1/admin/tokens')).status()).toBe(401);
    expect((await app.get('/api/v1/libraries')).status()).toBe(401);

    // Done: the secret leaves the page for good, also after a reload.
    await card.getByTestId('api-token-done').click();
    await expect(card.getByTestId('api-token-secret')).toHaveCount(0);
    await page.reload();
    await expect(page.getByTestId('api-tokens-card').getByText(name, { exact: true })).toBeVisible();
    expect(await page.content()).not.toContain(secret.slice(4));

    // Revoke after the confirm.
    const row = page.getByTestId('api-tokens-card').locator('li', { hasText: name });
    await row.getByRole('button', { name: 'Revoke' }).click();
    const revoked = page.waitForResponse((r) => r.request().method() === 'POST' && /\/api\/v1\/admin\/tokens\/[^/]+\/revoke$/.test(r.url()));
    await row.getByTestId('api-token-revoke-yes').click();
    expect((await revoked).status()).toBe(204);
    await expect(row.locator('.status')).toHaveText('Revoked');
    await expect(row.getByRole('button', { name: 'Revoke' })).toHaveCount(0);

    expect((await app.get('/api/v1/export/ping')).status()).toBe(401);
  } finally {
    await app.dispose();
  }
});

test('a token with "Request library scans" starts a full library scan and nothing else; the card fits a phone', async ({ page }) => {
  test.setTimeout(180_000);
  await login(page);
  const libraryId = await someLibrary(page);
  test.skip(!libraryId, 'no library on this instance');

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/admin');
  const card = page.getByTestId('api-tokens-card');
  await card.scrollIntoViewIfNeeded();
  await expect(card.getByTestId('api-token-scope-read')).toBeChecked();
  await expect(card.getByTestId('api-token-scope-scan')).not.toBeChecked();

  const name = `E2E scan token ${Date.now()}`;
  await card.getByTestId('api-token-name').fill(name);
  await card.getByTestId('api-token-scope-read').uncheck();
  await expect(card.getByTestId('api-token-scope-required')).toBeVisible();
  await expect(card.getByTestId('api-token-create')).toBeDisabled();
  await card.getByTestId('api-token-scope-scan').check();
  await expect(card.getByTestId('api-token-scope-required')).toHaveCount(0);
  await expectFitsScreen(page, 'The API tokens card with the scope checkboxes at 390 px');

  const createdResponse = page.waitForResponse((r) => r.request().method() === 'POST' && r.url().endsWith('/api/v1/admin/tokens'));
  await card.getByTestId('api-token-create').click();
  const created = await createdResponse;
  expect(created.status()).toBe(200);
  expect(created.request().postDataJSON().scopes).toEqual(['library:scan']);
  const secret = (await card.getByTestId('api-token-secret-value').textContent())!.trim();
  const tokenId: string = (await created.json()).token.id;
  await expect(card.getByTestId(`api-token-scopes-${tokenId}`)).toContainText('request library scans');
  await expect(card.getByTestId(`api-token-scopes-${tokenId}`)).not.toContainText('read the metadata export');
  await expectFitsScreen(page, 'The API tokens card with a new scan token at 390 px');
  await card.getByTestId('api-token-done').click();

  const app = await playwrightRequest.newContext({ baseURL: BASE_URL, extraHTTPHeaders: { Authorization: `Bearer ${secret}` } });
  try {
    // 202 (a scan may already be running after the library was registered: 409 + Retry-After, then ask again).
    let response = await app.post(`/api/v1/export/libraries/${libraryId}/scan`);
    for (let i = 0; i < 60 && response.status() === 409; i++) {
      expect(response.headers()['retry-after']).toBe('60');
      await page.waitForTimeout(2000);
      response = await app.post(`/api/v1/export/libraries/${libraryId}/scan`);
    }
    expect(response.status(), await response.text()).toBe(202);
    const { scanRunId } = await response.json();
    const history: { id: string }[] = await (await page.request.get(`/api/v1/admin/libraries/${libraryId}/scans`)).json();
    expect(history.map((h) => h.id)).toContain(scanRunId);

    // Asked again at once: the per-library cooldown (or the running scan) answers, with a Retry-After.
    const again = await app.post(`/api/v1/export/libraries/${libraryId}/scan`);
    expect([409, 429]).toContain(again.status());
    expect(Number(again.headers()['retry-after'])).toBeGreaterThan(0);

    // Nothing else: the scan-only token cannot read the export, scan through Administration or list tokens.
    expect((await app.get('/api/v1/export/libraries')).status()).toBe(403);
    expect((await app.post(`/api/v1/admin/libraries/${libraryId}/scan`)).status()).toBe(401);
    expect((await app.get('/api/v1/admin/tokens')).status()).toBe(401);
  } finally {
    await app.dispose();
  }

  // A browser cookie never reaches the token scan route, with or without the CSRF header.
  expect((await page.request.post(`/api/v1/export/libraries/${libraryId}/scan`)).status()).toBe(401);
  expect((await page.request.post(`/api/v1/export/libraries/${libraryId}/scan`, { headers: await csrf(page.request) })).status()).toBe(401);
});
