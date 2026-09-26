import { test, expect, Page, APIRequestContext } from '@playwright/test';

/**
 * Series metadata network controls (1.24.0, lane B2) - NEVER the real network:
 * - the admin "Series metadata" card: the web switch stays disabled until the consent
 *   checkbox is ticked, and enabling it makes no lookup (budget stays 0, no error, and
 *   the browser contacts no host but MangaPixer);
 * - Identify is disabled with its reason when web lookups are off, and the identify
 *   dialog opened from the selection menu shows the unavailable state.
 * The Identify checks need a library with at least one folder (skipped otherwise).
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];

test.describe.configure({ mode: 'serial' });

interface Node { id: string; displayName: string; kind: string }

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get('/api/v1/auth/csrf');
  const { token } = await res.json();
  return { 'X-MangaPixer-Csrf': token };
}

async function settings(page: Page): Promise<{ fetchEnabled: boolean; budgetUsedToday: number; lastErrorCode: string | null; libraries: { libraryId: string; fetchEnabled: boolean }[] }> {
  return (await page.request.get('/api/v1/admin/metadata/settings')).json();
}

async function setFetch(page: Page, on: boolean): Promise<void> {
  const res = await page.request.put('/api/v1/admin/metadata/settings', {
    headers: await csrf(page.request),
    data: on ? { fetchEnabled: true, acceptedConsentVersion: 1 } : { fetchEnabled: false },
  });
  expect(res.ok(), await res.text()).toBeTruthy();
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

/** Records every browser request to a host other than MangaPixer's. */
function watchForeignRequests(page: Page, baseURL: string): string[] {
  const own = new URL(baseURL).host;
  const foreign: string[] = [];
  page.on('request', (r) => {
    const url = new URL(r.url());
    if ((url.protocol === 'http:' || url.protocol === 'https:') && url.host !== own) foreign.push(url.host);
  });
  return foreign;
}

test('settings card: consent gates the web switch; enabling makes no lookup', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  await setFetch(page, false);
  const usedBefore = (await settings(page)).budgetUsedToday;
  await page.goto('/admin');

  const card = page.getByTestId('metadata-settings-card');
  await card.scrollIntoViewIfNeeded();
  await expect(card.getByTestId('md-consent-text')).toContainText('What is never sent:');
  const fetchSwitch = card.getByTestId('md-fetch').getByRole('switch');
  const consented = await card.getByTestId('md-consented').count();
  if (!consented) {
    await expect(fetchSwitch).toBeDisabled();
    await shot(page, 'b2-01-settings-consent-required');
    await card.getByTestId('md-consent').locator('input[type="checkbox"]').check();
  }
  await expect(fetchSwitch).toBeEnabled();
  await fetchSwitch.click();
  await expect(fetchSwitch).toHaveAttribute('aria-checked', 'true');
  await expect(card.getByTestId('md-consented')).toBeVisible();
  await expect.poll(async () => (await settings(page)).fetchEnabled).toBe(true);
  await shot(page, 'b2-02-settings-enabled');

  const after = await settings(page);
  expect(after.fetchEnabled).toBe(true);
  expect(after.budgetUsedToday).toBe(usedBefore); // enabling made no provider request
  expect(foreign).toEqual([]);

  await fetchSwitch.click();
  await expect(fetchSwitch).toHaveAttribute('aria-checked', 'false');
  await expect.poll(async () => (await settings(page)).fetchEnabled).toBe(false);
});

test('Identify is disabled with the reason, and the dialog shows the unavailable state', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  await setFetch(page, false);
  const usedBefore = (await settings(page)).budgetUsedToday;

  const libs: { id: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let target: { libraryId: string; folder: Node } | null = null;
  for (const lib of libs) {
    const root = await (await page.request.get(`/api/v1/libraries/${lib.id}/browse?pageSize=50`)).json();
    const folder = (root.items as Node[]).find((n) => n.kind === 'Folder');
    if (folder) { target = { libraryId: lib.id, folder }; break; }
  }
  test.skip(!target, 'No library with a folder to identify');

  await page.goto(`/libraries/${target!.libraryId}/browse/${target!.folder.id}`);
  await page.getByTestId('series-info-button').click();
  const overlay = page.getByTestId('series-overlay');
  await expect(overlay).toBeVisible();
  await overlay.getByTestId('series-admin-menu').click();
  await expect(page.locator('[data-slot="identify"]')).toBeDisabled();
  await expect(page.getByTestId('identify-why')).toContainText('off');
  await shot(page, 'b2-03-identify-menu-disabled');
  await page.keyboard.press('Escape');
  await page.keyboard.press('Escape');

  await page.goto(`/libraries/${target!.libraryId}/browse`);
  await page.locator('.select-toggle').click();
  await page.locator('.node-wrap', { hasText: target!.folder.displayName }).first().click();
  await page.getByTestId('series-selection-menu').click();
  await page.getByTestId('bulk-identify').click();
  await expect(page.getByTestId('identify-unavailable')).toBeVisible();
  await expect(page.getByTestId('identify-query')).toHaveCount(0);
  await shot(page, 'b2-04-identify-dialog-unavailable');
  await page.getByTestId('identify-close').click();

  expect((await settings(page)).budgetUsedToday).toBe(usedBefore);
  expect(foreign).toEqual([]);
});
