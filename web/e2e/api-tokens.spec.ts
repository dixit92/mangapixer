import { test, expect, Page, request as playwrightRequest } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * API tokens (1.33.0): an admin creates a personal access token in Administration, sees the secret once, the list shows it
 * without the secret, the token reads the export ping (and nothing that needs a login), and after revoking it stops working.
 * The phone and tablet layout of the card with long names, the secret and the confirm step is in phone-layout.spec.ts.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const BASE_URL = process.env['E2E_BASE_URL'] ?? 'http://127.0.0.1:8091';

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
  await expect(card.getByText(name)).toBeVisible();
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
    await expect(page.getByTestId('api-tokens-card').getByText(name)).toBeVisible();
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
