import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Favorites read state, the folder star and the phone browse-bar icons (1.48.0): a folder is starred from INSIDE it (a star
 * right after the Info button in the browse bar), it appears on the Favorites page with its Reading badge (the folder's read
 * rollup), unstarring from the bar takes it off again; and at 390 px the bar's icon-only buttons are 40 px squares with the
 * icon centred (Material pulled a projected icon 8 px left, which left a narrow oval with the icon off-centre).
 *
 * Needs the synthetic ComicInfo library from `e2e/fixtures/make-series-fixtures.mjs` (the same as series-info.spec.ts).
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';
const SERIES = 'Synthetic Series';

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

/** Registers + scans the fixture library once (whichever spec runs first), waits for its ComicInfo; returns [libraryId, seriesFolderId]. */
async function ensureSeries(page: Page): Promise<[string, string]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: FIXTURE_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let series = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    const node = (root.items as Node[]).find((n) => n.displayName === SERIES);
    series = node?.id ?? '';
    return node?.hasSeriesInfo === true;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(true);
  return [lib!.id, series];
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

/** Leaves the shared admin account as it was: nothing starred, the series' first archive unread. */
async function reset(page: Page, series: string): Promise<void> {
  const headers = await csrf(page.request);
  await page.request.delete(`/api/v1/nodes/${series}/favorite`, { headers });
  await page.request.delete(`/api/v1/reading/folders/${series}/read`, { headers });
}

test('a folder starred from inside it appears on Favorites with its read badge, and the bar star removes it again', async ({ page }) => {
  await login(page);
  const [libraryId, series] = await ensureSeries(page);
  await reset(page, series);
  try {
    // One chapter read: the folder is "Reading" (partially read).
    const headers = await csrf(page.request);
    const inside = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?parentId=${series}&pageSize=50&group=flat`)).json();
    const first = (inside.items as Node[]).find((n) => n.kind === 'Archive')!;
    expect((await page.request.put(`/api/v1/reading/${first.id}/read`, { headers, data: {} })).ok()).toBeTruthy();

    await page.goto(`/libraries/${libraryId}/browse/${series}`);
    const star = page.getByTestId('folder-star');
    await expect(star).toBeVisible();
    await expect(star).toHaveAttribute('aria-pressed', 'false');
    // Right after the Info button, in the same bar.
    const info = await page.getByTestId('series-info-button').boundingBox();
    const starBox = await star.boundingBox();
    expect(starBox!.x).toBeGreaterThan(info!.x + info!.width - 1);
    expect(Math.abs((starBox!.y + starBox!.height / 2) - (info!.y + info!.height / 2))).toBeLessThan(3);
    await shot(page, '01-bar-star-desktop');

    const starred = page.waitForResponse((r) => r.url().endsWith(`/nodes/${series}/favorite`) && r.request().method() === 'POST');
    await star.click();
    expect((await starred).ok()).toBeTruthy();
    await expect(star).toHaveAttribute('aria-pressed', 'true');

    // Opening the folder again keeps the star on (seeded from the node lookup).
    await page.reload();
    await expect(page.getByTestId('folder-star')).toHaveAttribute('aria-pressed', 'true');

    await page.goto('/favorites');
    const card = page.locator('.fav-card', { hasText: SERIES });
    await expect(card).toBeVisible();
    await expect(card.locator('app-folder-rollup-badge .badge')).toHaveText('Reading');
    await expect(card.locator('.kind-badge')).toBeVisible();
    // The kind badge no longer sits under the read badge.
    const badge = await card.locator('app-folder-rollup-badge .badge').boundingBox();
    const kind = await card.locator('.kind-badge').boundingBox();
    expect(kind!.x + kind!.width).toBeLessThanOrEqual(badge!.x + 1);
    await shot(page, '02-favorites-desktop');

    // The bar star takes it off again.
    await page.goto(`/libraries/${libraryId}/browse/${series}`);
    const unstarred = page.waitForResponse((r) => r.url().endsWith(`/nodes/${series}/favorite`) && r.request().method() === 'DELETE');
    await page.getByTestId('folder-star').click();
    expect((await unstarred).ok()).toBeTruthy();
    await page.goto('/favorites');
    await expect(page.locator('.fav-card', { hasText: SERIES })).toHaveCount(0);
  } finally {
    await reset(page, series);
  }
});

test('phone: the browse bar\'s icon buttons are 40 px squares with the icon centred', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const [libraryId, series] = await ensureSeries(page);
  await reset(page, series);
  await page.goto(`/libraries/${libraryId}/browse/${series}`);
  await expect(page.getByTestId('folder-star')).toBeVisible();

  for (const testId of ['series-info-button', 'folder-star']) {
    const geometry = await page.getByTestId(testId).evaluate((button) => {
      const b = button.getBoundingClientRect();
      const i = button.querySelector('mat-icon')!.getBoundingClientRect();
      return { width: b.width, height: b.height, offset: (i.left + i.width / 2) - (b.left + b.width / 2) };
    });
    expect(geometry.width, `${testId} width`).toBeCloseTo(40, 0);
    expect(geometry.height, `${testId} height`).toBeCloseTo(40, 0);
    expect(Math.abs(geometry.offset), `${testId} icon offset from the centre`).toBeLessThan(1);
  }
  await expectFitsScreen(page, 'folder browse bar with the star, 390 px');
  await shot(page, '03-bar-phone');

  // Starring from the phone bar works as on desktop.
  const starred = page.waitForResponse((r) => r.url().endsWith(`/nodes/${series}/favorite`) && r.request().method() === 'POST');
  await page.getByTestId('folder-star').click();
  expect((await starred).ok()).toBeTruthy();
  await reset(page, series);
});
