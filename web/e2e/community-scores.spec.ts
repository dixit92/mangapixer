import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Community scores (1.54.0, owner q4: shown on the Info sheet and the series page as "MangaUpdates 9.0", no switch). The real app and the
 * synthetic ComicInfo library (`e2e/fixtures/make-series-fixtures.mjs`, as ratings.spec.ts); the scores themselves are added to the
 * server's own series-info answer with `route.fetch()` (the fixture library has no web data to read them from). Desktop and a phone with
 * touch: the lines show with their source, read out with the exact score, and fit the screen.
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';
const SERIES = 'Synthetic Series';

const SCORES = [
  { source: 'mangaupdates', sourceName: 'MangaUpdates', score: 8.98, votes: 1200 },
  { source: 'mangadex', sourceName: 'MangaDex', score: 8.71, votes: 300 },
  { source: 'myanimelist', sourceName: 'MyAnimeList', score: 8.62, votes: null },
];

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic ComicInfo library available');

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

/** Registers + scans the fixture library once (whichever spec runs first); returns [libraryId, seriesFolderId]. */
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
    const node = (root.items as { id: string; displayName: string; hasSeriesInfo?: boolean }[]).find((n) => n.displayName === SERIES);
    series = node?.id ?? '';
    return node?.hasSeriesInfo === true;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(true);
  return [lib!.id, series];
}

/** The server's own series-info answer, with community scores added. */
async function withScores(page: Page): Promise<void> {
  await page.route('**/api/v1/nodes/*/series-info*', async (route) => {
    const response = await route.fetch();
    const body = await response.json();
    await route.fulfill({ response, json: { ...body, communityScores: SCORES } });
  });
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${SHOTS}/community-scores-${name}.png`, fullPage: false });
}

async function expectScores(scope: ReturnType<Page['locator']>): Promise<void> {
  const lines = scope.getByTestId('community-scores').getByRole('img');
  await expect(lines).toHaveCount(3);
  await expect(lines.nth(0)).toHaveAccessibleName('MangaUpdates community score 8.98 of 10, 1,200 ratings');
  await expect(lines.nth(0)).toContainText('MangaUpdates9.0');
  await expect(lines.nth(1)).toContainText('MangaDex8.7');
  await expect(lines.nth(2)).toHaveAccessibleName('MyAnimeList community score 8.62 of 10');
}

for (const vp of [{ name: 'desktop', width: 1280, height: 800, touch: false }, { name: 'phone', width: 390, height: 844, touch: true }]) {
  test.describe(`community scores - ${vp.name}`, () => {
    test.use({ viewport: { width: vp.width, height: vp.height }, hasTouch: vp.touch, isMobile: vp.touch });

    test('the Info sheet and the series page list the community scores with their source', async ({ page }) => {
      await login(page);
      const [libraryId, series] = await ensureSeries(page);
      await withScores(page);

      await page.goto(`/libraries/${libraryId}/browse/${series}`);
      await page.getByTestId('series-info-button').click();
      const overlay = page.getByTestId('series-overlay');
      await expect(overlay).toBeVisible();
      await expectScores(overlay);
      await expectFitsScreen(page, `the Info sheet with community scores, ${vp.name}`);
      await shot(page, `info-sheet-${vp.name}`);
      await page.keyboard.press('Escape');

      await page.goto(`/series/${series}`);
      const hero = page.getByTestId('series-page');
      await expectScores(hero);
      await expectFitsScreen(page, `the series page with community scores, ${vp.name}`);
      await shot(page, `series-page-${vp.name}`);
    });
  });
}
