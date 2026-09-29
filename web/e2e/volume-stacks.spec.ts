import { test, expect, Page, APIRequestContext } from '@playwright/test';

/**
 * The Volumes view, OFFLINE (1.29.0): a synthetic library whose archives state their volume in the name
 * (`Stacked Saga v01 c001` ...) groups into volume stacks with no series record and no network; the Volumes | Folders
 * switch shows the real folder; the choice is remembered.
 *
 * Needs the fixture library from `e2e/fixtures/make-series-fixtures.mjs`, written next to the series fixtures as
 * `<E2E_SERIES_FIXTURE_ROOT>-volumes` and visible to the SERVER at that path (`scripts/Verify-E2E.ps1` does both).
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const VOLUMES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-volumes` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Volume Stacks';

test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic volume library available');
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

/** Registers + scans the volume library once and waits until the series folder is listed. Returns [libraryId, folderId]. */
async function ensureLibrary(page: Page): Promise<[string, string]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: VOLUMES_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folder = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = ((root.items as Node[]).find((n) => n.displayName === 'Stacked Saga')?.id) ?? '';
    if (!folder) return 0;
    const inside = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?parentId=${folder}&pageSize=50&group=flat`)).json();
    return (inside.items as Node[]).length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(6);
  return [lib!.id, folder];
}

async function setSwitch(page: Page, mode: 'Volumes' | 'Folders' | null): Promise<void> {
  const headers = await csrf(page.request);
  const current = await (await page.request.get('/api/v1/reading/library-preferences')).json();
  const res = await page.request.put('/api/v1/reading/library-preferences', { headers, data: { ...current, seriesViewMode: mode } });
  expect(res.ok(), await res.text()).toBeTruthy();
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

test('a folder of chapters that state their volume groups into stacks, offline', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  await page.goto(`/libraries/${libraryId}/browse/${folderId}`);

  const cards = page.locator('.node-wrap');
  await expect(cards).toHaveCount(3);
  await expect(cards.nth(0).locator('.node-title')).toHaveText('Vol. 1');
  await expect(cards.nth(0).locator('.node-sub')).toContainText('3 chapters');
  await expect(cards.nth(1).locator('.node-title')).toHaveText('Vol. 2');
  await expect(cards.nth(1).locator('.node-sub')).toContainText('2 chapters');
  // Volume 3 is a real volume file with no chapters of its own: a plain card, not a stack.
  await expect(cards.nth(2).locator('.node-title')).toHaveText('Stacked Saga v03.cbz');
  await expect(page.locator('app-stack-card')).toHaveCount(2);
  // Nothing is missing without a volume list: no incomplete mark.
  await expect(page.getByTestId('stack-incomplete')).toHaveCount(0);
  await expect(page.getByTestId('volume-view-switch')).toBeVisible();
  await shot(page, 'volumes-01-stacks');
});

test('a stack opens its chapters, with previous / next volume', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  await page.goto(`/libraries/${libraryId}/browse/${folderId}`);

  await page.locator('.node-wrap', { hasText: 'Vol. 1' }).locator('a.node-card').click();
  await expect(page).toHaveURL(new RegExp(`/libraries/${libraryId}/browse/${folderId}/volume/1$`));
  await expect(page.getByTestId('stack-title')).toHaveText('Vol. 1');
  await expect(page.getByTestId('stack-counts')).toHaveText('3 chapters');
  await expect(page.getByTestId('stack-item')).toHaveCount(3);
  await expect(page.getByTestId('stack-prev')).toHaveCount(0);
  await shot(page, 'volumes-02-stack-view');

  await page.getByTestId('stack-next').click();
  await expect(page).toHaveURL(/\/volume\/2$/);
  await expect(page.getByTestId('stack-title')).toHaveText('Vol. 2');
  await expect(page.getByTestId('stack-item')).toHaveCount(2);
  await expect(page.getByTestId('stack-next')).toHaveCount(0);

  // The breadcrumb names the REAL folder.
  await page.getByTestId('stack-folder-link').click();
  await expect(page.locator('.node-wrap')).toHaveCount(3);
});

test('the Folders switch shows the real chapters and the choice is remembered', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
  await expect(page.locator('.node-wrap')).toHaveCount(3);

  await page.getByTestId('view-folders').click();
  await expect(page.locator('.node-wrap')).toHaveCount(6);
  await expect(page.locator('app-stack-card')).toHaveCount(0);
  await expect(page.locator('.node-wrap', { hasText: 'Stacked Saga v01 c001' })).toBeVisible();
  await shot(page, 'volumes-03-folders');

  // Remembered per user: a reload stays on Folders.
  await page.reload();
  await expect(page.locator('.node-wrap')).toHaveCount(6);
  await expect(page.getByTestId('view-folders')).toHaveAttribute('aria-pressed', 'true');

  await page.getByTestId('view-volumes').click();
  await expect(page.locator('.node-wrap')).toHaveCount(3);
  await page.reload();
  await expect(page.locator('.node-wrap')).toHaveCount(3);
  await expect(page.getByTestId('view-volumes')).toHaveAttribute('aria-pressed', 'true');
  await setSwitch(page, null);
});

test('the phone layout groups too', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  await page.goto(`/libraries/${libraryId}/browse/${folderId}`);

  await expect(page.locator('.node-wrap')).toHaveCount(3);
  await expect(page.getByTestId('volume-view-switch')).toBeVisible();
  await shot(page, 'volumes-04-phone');
});
