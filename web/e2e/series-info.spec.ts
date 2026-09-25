import { test, expect, Page, APIRequestContext } from '@playwright/test';

/**
 * Series information over local ComicInfo data (1.24.0, stage 1): card (i) -> overlay,
 * the series page, folder precedence set/clear, "Show series information" off hides
 * everything, and the phone bottom sheet.
 *
 * Needs the synthetic fixture library from `e2e/fixtures/make-series-fixtures.mjs`
 * visible to the SERVER at E2E_SERIES_FIXTURE_ROOT (for example `/media/fixtures`).
 * Without it the suite skips - the default E2E flow mounts an empty media directory.
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic ComicInfo library available');
test.describe.configure({ mode: 'serial' });

interface Node { id: string; displayName: string; kind: string; hasSeriesInfo?: boolean }

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

/** Registers + scans the fixture library once, then waits until analysis has read its ComicInfo. */
async function ensureLibrary(page: Page): Promise<string> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', {
      headers, data: { displayName: LIBRARY_NAME, rootPath: FIXTURE_ROOT },
    });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    return (root.items as Node[]).find((n) => n.displayName === 'Synthetic Series')?.hasSeriesInfo === true;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(true);
  return lib!.id;
}

async function folderId(page: Page, libraryId: string, name: string): Promise<string> {
  const root = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=50`)).json();
  return (root.items as Node[]).find((n) => n.displayName === name)!.id;
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  // Let the sheet / dialog open animations settle so the capture is the final state.
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

test('card (i) opens the overlay with the ComicInfo series, then the series page', async ({ page }) => {
  await login(page);
  const libraryId = await ensureLibrary(page);
  await page.goto(`/libraries/${libraryId}/browse`);

  const seriesCard = page.locator('.node-wrap', { hasText: 'Synthetic Series' });
  const plainCard = page.locator('.node-wrap', { hasText: 'Plain Folder' });
  await expect(seriesCard.getByTestId('info-toggle')).toBeVisible();
  await expect(plainCard.getByTestId('info-toggle')).toHaveCount(0);
  await shot(page, '01-browse-card-info');

  await seriesCard.getByTestId('info-toggle').click();
  const overlay = page.getByTestId('series-overlay');
  await expect(overlay).toBeVisible();
  await expect(overlay.getByTestId('series-title')).toHaveText('Synthetic Saga');
  await expect(overlay).toContainText('ComicInfo in 3 of 3 items');
  await expect(overlay).toContainText('Test Writer');
  await expect(page.locator('.series-info-side-sheet')).toBeVisible();
  await shot(page, '02-overlay-desktop');

  await overlay.getByTestId('open-series-page').click();
  await expect(page).toHaveURL(/\/series\//);
  const seriesPage = page.getByTestId('series-page');
  await expect(seriesPage.getByTestId('series-title')).toHaveText('Synthetic Saga');
  await expect(seriesPage.getByTestId('series-items').locator('tbody tr')).toHaveCount(3);
  await shot(page, '03-series-page');
});

test('a chapter id redirects to the series anchor; a mixed folder lists its series', async ({ page }) => {
  await login(page);
  const libraryId = await ensureLibrary(page);
  const series = await folderId(page, libraryId, 'Synthetic Series');
  const chapters = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?parentId=${series}&pageSize=10`)).json();

  await page.goto(`/series/${(chapters.items as Node[])[0].id}`);
  await expect(page).toHaveURL(new RegExp(`/series/${series}$`));

  await page.goto(`/libraries/${libraryId}/browse`);
  await page.locator('.node-wrap', { hasText: 'Synthetic Anthology' }).getByTestId('info-toggle').click();
  await expect(page.getByTestId('series-mixed')).toBeVisible();
  await expect(page.getByTestId('series-overlay')).toContainText('Alpha Tale');
  await shot(page, '04-overlay-mixed');
});

test('admin sets and clears folder precedence from the selection bar', async ({ page }) => {
  await login(page);
  const libraryId = await ensureLibrary(page);
  const series = await folderId(page, libraryId, 'Synthetic Series');
  await page.goto(`/libraries/${libraryId}/browse`);

  await page.locator('button.select-toggle').click();
  await page.locator('.node-wrap', { hasText: 'Synthetic Series' }).locator('.node-card').click();
  await page.getByTestId('series-selection-menu').click();
  await page.getByTestId('bulk-precedence-comicinfo').click();
  await expect(page.getByText(/Source precedence \(ComicInfo first\) on 1 folder/)).toBeVisible();
  await shot(page, '05-selection-precedence');

  await page.goto(`/series/${series}`);
  await expect(page.getByTestId('series-page')).toContainText('ComicInfo first (set on a folder)');

  await page.getByTestId('series-admin-menu').click();
  await page.getByTestId('precedence-inherit').click();
  await expect(page.getByTestId('series-page')).toContainText('Web first (default)');
});

test('"Show series information" off hides the (i), the top-bar button and the series page', async ({ page }) => {
  await login(page);
  const libraryId = await ensureLibrary(page);
  const series = await folderId(page, libraryId, 'Synthetic Series');
  const headers = await csrf(page.request);
  const put = (show: boolean) =>
    page.request.put('/api/v1/admin/metadata/settings', { headers, data: { showSeriesInfo: show } });

  expect((await put(false)).ok()).toBeTruthy();
  try {
    await page.goto(`/libraries/${libraryId}/browse`);
    await expect(page.locator('.node-wrap', { hasText: 'Synthetic Series' })).toBeVisible();
    await expect(page.getByTestId('info-toggle')).toHaveCount(0);

    await page.goto(`/series/${series}`);
    await expect(page.getByTestId('series-none')).toBeVisible();
    await shot(page, '06-hidden-series-page');
  } finally {
    expect((await put(true)).ok()).toBeTruthy();
  }
  await page.goto(`/libraries/${libraryId}/browse/${series}`);
  await expect(page.getByTestId('series-info-button')).toBeVisible();
});

test('phone: the overlay is a bottom sheet', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const libraryId = await ensureLibrary(page);
  await page.goto(`/libraries/${libraryId}/browse`);

  await page.locator('.node-wrap', { hasText: 'Synthetic Series' }).getByTestId('info-toggle').click();
  await expect(page.locator('.series-info-bottom-sheet')).toBeVisible();
  await expect(page.getByTestId('series-overlay').getByTestId('series-title')).toHaveText('Synthetic Saga');
  await shot(page, '07-overlay-phone');
});
