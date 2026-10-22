import { test, expect, Page, APIRequestContext, Route } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Info panel follow-ups (1.47.0, lane P), on the owner's paths and the REAL tag index of the series library of
 * `e2e/fixtures/make-series-fixtures.mjs` (`Synthetic Series`: ComicInfo writer "Test Writer", artist "Test Artist", genres Action /
 * Fantasy; shared with info-panel.spec.ts and series-info.spec.ts):
 * - the LIBRARY ROOT's Info: browse a library's root -> "Info" -> the library's tags -> a chip -> the tag search;
 * - "+N more": the panel's answer is cut to its first chip per bucket on the way to the page (the real cap is 12; the fixture has
 *   fewer), so "+N more" asks the REAL bucket route for the rest and the chips appear in place;
 * - the read chips in the tag view: they are shown and the chosen read state reaches the server with the tags.
 * Optional: E2E_SCREENSHOT_DIR saves the screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic ComicInfo library available');
test.describe.configure({ mode: 'serial' });

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

/** The series library (registered + scanned once, shared with the other series specs). */
async function ensureLibrary(page: Page): Promise<{ libraryId: string; libraryName: string }> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: FIXTURE_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  // The tag index is built in the background after the scan: wait until the library root has the fixture's creators.
  await expect.poll(async () => {
    const res = await page.request.get(`/api/v1/libraries/${lib!.id}/info`);
    if (!res.ok()) return false;
    const panel = await res.json();
    return JSON.stringify(panel.buckets ?? []).includes('Test Writer');
  }, { timeout: 180_000, intervals: [1000, 2000, 5000] }).toBe(true);
  return { libraryId: lib!.id, libraryName: lib!.name };
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/info-library-${name}.png`, fullPage: false });
}

/** Whether a tap at the element's centre reaches it (not an overlay or a neighbour on top). */
async function tapHits(page: Page, selector: string): Promise<boolean> {
  return page.locator(selector).first().evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!hit && (hit === el || el.contains(hit));
  });
}

/** Cuts every bucket of the library panel's answer to its first chip, counting the rest as `more` (the real route serves them). */
async function cutPanelToOneChip(page: Page, libraryId: string): Promise<void> {
  await page.route(`**/api/v1/libraries/${libraryId}/info`, async (route: Route) => {
    const res = await route.fetch();
    const body = await res.json();
    for (const b of body.buckets ?? []) {
      b.more = (b.more ?? 0) + Math.max(0, b.chips.length - 1);
      b.chips = b.chips.slice(0, 1);
    }
    return route.fulfill({ response: res, json: body });
  });
}

test('library root: browse -> Info -> the library\'s tags -> a chip opens the tag search, with the read chips', async ({ page }) => {
  test.setTimeout(300_000);
  await login(page);
  const ids = await ensureLibrary(page);

  await page.goto(`/libraries/${ids.libraryId}/browse`);
  const info = page.getByRole('button', { name: 'Info', exact: true });
  await expect(info).toBeVisible({ timeout: 30_000 });
  await info.click();
  const overlay = page.getByTestId('series-overlay');
  await expect(overlay.locator('.kicker')).toHaveText('Info');
  await expect(overlay.getByTestId('library-info').locator('.library-name')).toHaveText(ids.libraryName);
  await expect(overlay.getByTestId('library-works')).toContainText('with tags');
  // A library root has no series information: no summary, no series footer.
  await expect(overlay.locator('app-series-info-summary')).toHaveCount(0);
  await expect(overlay.getByTestId('open-series-page')).toHaveCount(0);
  const creators = overlay.locator('[data-testid="info-bucket"][data-bucket="Creators"]');
  const writer = creators.getByTestId('info-chip').filter({ hasText: 'Test Writer' });
  await expect(writer).toBeVisible();
  // A container counts "N here".
  await expect(writer.getByTestId('info-chip-count')).toContainText('here');
  await shot(page, '01-overlay-desktop');

  await writer.click();
  await expect(page).toHaveURL(/\/search\?f=creator(:|%3A)[A-Za-z0-9_]+$/);
  await expect(overlay).toHaveCount(0);
  await expect(page.getByTestId('search-filter-chip')).toContainText('Test Writer');
  await expect(page.getByTestId('work-card').filter({ hasText: 'Synthetic Saga' })).toBeVisible();

  // 1.47.0: the read chips are offered in the tag view, and the chosen state goes to the server with the tag.
  const readRow = page.locator('.read-filter');
  await expect(readRow).toBeVisible();
  const answered = page.waitForResponse((r) => r.url().includes('/api/v1/search?') && r.url().includes('readState=read') && r.url().includes('f='));
  await readRow.getByRole('option', { name: 'Read', exact: true }).click();
  const res = await answered;
  expect(res.status()).toBe(200);
  const body = await res.json();
  expect(Array.isArray(body.works)).toBe(true);
  await expect(page).toHaveURL(/read=read/);
  await expect(page.getByTestId('search-filter-chip')).toContainText('Test Writer');
  await expectFitsScreen(page, 'tag search with a read filter');
});

test('"+N more" adds the rest of a bucket from the real bucket route', async ({ page }) => {
  test.setTimeout(300_000);
  await login(page);
  const ids = await ensureLibrary(page);
  await cutPanelToOneChip(page, ids.libraryId);

  await page.goto(`/libraries/${ids.libraryId}/browse`);
  await page.getByRole('button', { name: 'Info', exact: true }).click();
  const overlay = page.getByTestId('series-overlay');
  const genres = overlay.locator('[data-testid="info-bucket"][data-bucket="Genres"]');
  await expect(genres.getByTestId('info-chip')).toHaveCount(1);
  const more = genres.getByTestId('info-more');
  await expect(more).toHaveText('+1 more');
  await shot(page, '02-more-before');

  const page2 = page.waitForResponse((r) => /\/api\/v1\/libraries\/[^/]+\/info\/buckets\/Genres\?offset=1/.test(r.url()));
  await more.click();
  const res = await page2;
  expect(res.status()).toBe(200);
  const answer = await res.json();
  expect(answer.total).toBe(2);
  await expect(genres.getByTestId('info-chip')).toHaveCount(2);
  await expect(genres.getByTestId('info-chip').filter({ hasText: 'Action' })).toBeVisible();
  await expect(genres.getByTestId('info-chip').filter({ hasText: 'Fantasy' })).toBeVisible();
  await expect(genres.getByTestId('info-more')).toHaveCount(0);
  await shot(page, '03-more-after');

  // A chip added by "+N more" opens the tag search like any other.
  await genres.getByTestId('info-chip').nth(1).click();
  await expect(page).toHaveURL(/\/search\?f=genre(:|%3A)[A-Za-z0-9_]+$/);
  await expect(page.getByTestId('work-card').filter({ hasText: 'Synthetic Saga' })).toBeVisible();
});

test('phone: the library\'s Info sheet fits, "+N more" and the chips are reachable', async ({ page }) => {
  test.setTimeout(300_000);
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const ids = await ensureLibrary(page);
  await cutPanelToOneChip(page, ids.libraryId);

  await page.goto(`/libraries/${ids.libraryId}/browse`);
  await page.getByRole('button', { name: 'Info', exact: true }).click();
  const sheet = page.locator('.series-info-bottom-sheet');
  await expect(sheet).toBeVisible();
  await expect(sheet.getByTestId('library-info')).toBeVisible();
  const more = '.series-info-bottom-sheet [data-testid="info-more"]';
  await expect(page.locator(more).first()).toBeVisible();
  const box = await page.locator(more).first().boundingBox();
  expect(Math.round(box!.height)).toBe(32);
  await expect.poll(() => tapHits(page, more), { timeout: 3_000 }).toBe(true);
  await page.locator(more).first().click();
  await expect.poll(() => tapHits(page, '.series-info-bottom-sheet [data-testid="info-chip"]'), { timeout: 3_000 }).toBe(true);
  await expectFitsScreen(page, 'library Info sheet');
  await shot(page, '04-sheet-phone');

  await page.setViewportSize({ width: 820, height: 1180 });
  await expectFitsScreen(page, 'library Info at 820');
});
