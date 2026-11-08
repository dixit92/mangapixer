import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Ratings (1.52.0 lane R): the series is rated from the browse Info sheet (a tap on the right half of the 4th star = 4 stars), its card
 * then carries the rating badge, the browse "My rating" sort lists it first, the Search page's `rating:4` term finds it (and `rating:5`
 * does not), and "Clear rating" in the Info sheet takes the badge off again - at 1280 px and at 390 px (the Info sheet is a bottom sheet
 * there; the 390 px run checks the layout with `expectFitsScreen` - the 40 x 40 touch stars are a `pointer: coarse` rule, which this
 * mouse-driven browser does not emulate).
 *
 * Needs the synthetic ComicInfo library from `e2e/fixtures/make-series-fixtures.mjs` (the same as series-info.spec.ts). Leaves the shared
 * admin account as it was: no rating, the stored browse sort restored. Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';
const SERIES = 'Synthetic Series';

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic ComicInfo library available');
test.describe.configure({ mode: 'serial' });

/**
 * The shared admin's state before the first test: its stored browse preferences (the sort menu changes them) and the series id. Put back
 * in `afterAll`, which has its own timeout - a test that times out must not leave the admin on "My rating" (the Volumes view needs Name).
 */
let saved: { prefs: Record<string, unknown>; series: string } | null = null;

test.afterAll(async ({ browser }) => {
  if (!saved) return;
  const page = await browser.newPage();
  try {
    await login(page);
    await restore(page, saved.series, saved.prefs);
  } finally {
    await page.close();
  }
});

interface Node { id: string; displayName: string; kind: string; hasSeriesInfo?: boolean; myRating?: number | null }

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

/** The stored browse preferences, to put back after the sort menu changed them (they are the shared admin's). */
async function preferences(page: Page): Promise<Record<string, unknown>> {
  return (await page.request.get('/api/v1/reading/library-preferences')).json();
}

async function restore(page: Page, series: string, prefs: Record<string, unknown>): Promise<void> {
  const headers = await csrf(page.request);
  await page.request.delete(`/api/v1/nodes/${series}/rating`, { headers });
  await page.request.put('/api/v1/reading/library-preferences', { headers, data: prefs });
}

/** The series' card or list row on the library's root level. */
function seriesEntry(page: Page) {
  return page.locator('.node-wrap', { hasText: SERIES }).first();
}

async function rateFromInfoSheet(page: Page, libraryId: string, series: string, phone: boolean): Promise<void> {
  await page.goto(`/libraries/${libraryId}/browse/${series}`);
  await page.getByTestId('series-info-button').click();
  const overlay = page.getByTestId('series-overlay');
  await expect(overlay).toBeVisible();
  const slider = overlay.getByTestId('rating-slider');
  await expect(slider).toBeVisible();
  await expect(slider).toHaveAttribute('aria-valuetext', 'Not rated');
  await expect(slider).not.toHaveAttribute('aria-disabled', 'true');
  await shot(page, `ratings-01-info-sheet-${phone ? 'phone' : 'desktop'}`);

  // The right half of the 4th star = 4 stars = score 8.
  const put = page.waitForResponse((r) => r.url().endsWith(`/nodes/${series}/rating`) && r.request().method() === 'PUT');
  await overlay.getByTestId('rating-half-8').click();
  const response = await put;
  expect(response.status()).toBe(204);
  expect(response.request().postDataJSON()).toEqual({ score: 8 });
  await expect(slider).toHaveAttribute('aria-valuetext', '4 of 5 stars');
  await expect(overlay.getByTestId('rating-clear')).toBeVisible();
  if (phone) await expectFitsScreen(page, 'the Info sheet with the rating, 390 px');
  await shot(page, `ratings-02-rated-${phone ? 'phone' : 'desktop'}`);
  await page.keyboard.press('Escape');
  await expect(overlay).toHaveCount(0);
}

async function clearFromInfoSheet(page: Page, libraryId: string, series: string): Promise<void> {
  await page.goto(`/libraries/${libraryId}/browse/${series}`);
  await page.getByTestId('series-info-button').click();
  const overlay = page.getByTestId('series-overlay');
  await expect(overlay.getByTestId('rating-slider')).toHaveAttribute('aria-valuetext', '4 of 5 stars');
  const cleared = page.waitForResponse((r) => r.url().endsWith(`/nodes/${series}/rating`) && r.request().method() === 'DELETE');
  await overlay.getByTestId('rating-clear').click();
  expect((await cleared).status()).toBe(204);
  await expect(overlay.getByTestId('rating-slider')).toHaveAttribute('aria-valuetext', 'Not rated');
  await page.keyboard.press('Escape');
}

async function sortByMyRating(page: Page, libraryId: string): Promise<void> {
  await page.goto(`/libraries/${libraryId}/browse`);
  await expect(seriesEntry(page)).toBeVisible();
  await page.getByRole('button', { name: 'View options' }).click();
  const sorted = page.waitForResponse((r) => r.url().includes(`/libraries/${libraryId}/browse`) && r.url().includes('sort=myRating'));
  await page.getByRole('menuitemradio', { name: /My rating/ }).click();
  expect((await sorted).ok()).toBeTruthy();
  await page.keyboard.press('Escape');
  // The only rated node of the level comes first.
  await expect(page.locator('.node-wrap').first()).toContainText(SERIES);
}

test('rate from the Info sheet: the badge, the My rating sort, the rating: search, and Clear rating', async ({ page }) => {
  test.setTimeout(150_000); // several page loads on a shared, busy box (the 30 s default ran out at the search step)
  await page.setViewportSize({ width: 1280, height: 900 });
  await login(page);
  const [libraryId, series] = await ensureSeries(page);
  const prefs = saved?.prefs ?? await preferences(page);
  saved ??= { prefs, series };
  await restore(page, series, prefs);
  try {
    await page.goto(`/libraries/${libraryId}/browse`);
    await expect(seriesEntry(page)).toBeVisible();
    await expect(seriesEntry(page).getByTestId('rating-badge')).toHaveCount(0);

    await rateFromInfoSheet(page, libraryId, series, false);

    // The card (or list row) carries the badge: one star and "4", named for screen readers.
    await page.goto(`/libraries/${libraryId}/browse`);
    const badge = seriesEntry(page).getByTestId('rating-badge');
    await expect(badge).toBeVisible();
    await expect(badge).toHaveAttribute('aria-label', 'Rated 4 of 5');
    await expect(badge).toContainText('4');
    await shot(page, 'ratings-03-badge-desktop');

    await sortByMyRating(page, libraryId);
    await shot(page, 'ratings-04-sorted-desktop');

    // The Search page: rating:4 finds the series (with its badge), rating:5 does not; the "?" panel lists the term.
    await page.goto('/search?q=' + encodeURIComponent('rating:4'));
    const hit = page.locator('.result-card', { hasText: SERIES }).first();
    await expect(hit).toBeVisible();
    await expect(hit.getByTestId('rating-badge')).toBeVisible();
    await shot(page, 'ratings-05-search-desktop');
    // (Match the parsed q: Angular leaves the ':' of a query value unencoded.)
    const none = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/v1/search' && new URL(r.url()).searchParams.get('q') === 'rating:5');
    await page.goto('/search?q=' + encodeURIComponent('rating:5'));
    await none;
    await expect(page.locator('.result-card', { hasText: SERIES })).toHaveCount(0);

    await clearFromInfoSheet(page, libraryId, series);
    await page.goto(`/libraries/${libraryId}/browse`);
    await expect(seriesEntry(page)).toBeVisible();
    await expect(seriesEntry(page).getByTestId('rating-badge')).toHaveCount(0);
  } finally {
    await restore(page, series, prefs);
  }
});

test('phone 390 px: rate from the Info sheet, the badge fits, sort by My rating, clear', async ({ page }) => {
  test.setTimeout(150_000);
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const [libraryId, series] = await ensureSeries(page);
  const prefs = saved?.prefs ?? await preferences(page);
  saved ??= { prefs, series };
  await restore(page, series, prefs);
  try {
    await rateFromInfoSheet(page, libraryId, series, true);

    await page.goto(`/libraries/${libraryId}/browse`);
    const badge = seriesEntry(page).getByTestId('rating-badge');
    await expect(badge).toBeVisible();
    // The badge sits inside its card / row, not over the screen edge.
    const card = await seriesEntry(page).boundingBox();
    const box = await badge.boundingBox();
    expect(box!.x).toBeGreaterThanOrEqual(card!.x - 1);
    expect(box!.x + box!.width).toBeLessThanOrEqual(card!.x + card!.width + 1);
    await expectFitsScreen(page, 'browse with a rated card, 390 px');
    await shot(page, 'ratings-03-badge-phone');

    await sortByMyRating(page, libraryId);
    await expectFitsScreen(page, 'browse sorted by My rating, 390 px');

    await page.goto('/search?q=' + encodeURIComponent('rating:4'));
    await expect(page.locator('.result-card', { hasText: SERIES }).first()).toBeVisible();
    await expectFitsScreen(page, 'search rating:4, 390 px');

    await clearFromInfoSheet(page, libraryId, series);
  } finally {
    await restore(page, series, prefs);
  }
});
