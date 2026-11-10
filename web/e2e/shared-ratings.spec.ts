import { test, expect, Page, APIRequestContext, Locator } from '@playwright/test';

import { expectFitsScreen, openSelectionMore } from './layout';

/**
 * Shared ratings and the ratings follow-ups (1.53.0 lane S), in a real browser on the synthetic ComicInfo library:
 * - Administration's "Shared ratings" card (off by default; ticking asks first with the owner's text; Cancel leaves it off);
 * - Settings' "Ratings and recommendations" card (the share box disabled with the administrator hint while the switch is off);
 * - the Info sheet / series page: the one-star "SERVER ★ 4.0 · 1 rating" line under the user's own stars, and a rating row that keeps
 *   its layout when you rate (audit V4: the stars do not move);
 * - the browse "Server rating" sort, offered only while the switch is on;
 * - follow-up 5b: "Rate" in the browse bar where a folder has no Info button, and "Rate..." in the selection bar (one item);
 * - audit V7 (list rows: the star and (i) in fixed columns whatever the badges) and V8 (one `stars` Rate icon, the reader bar too).
 * At 1280 px and 390 px. Leaves the shared admin as it was: the switch off, no rating, sharing on, the stored browse preferences.
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots (dark and light).
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';
const SERIES = 'Synthetic Series';
const PLAIN = 'Plain Folder';
const CONFIRMATION = "Every user's ratings will count in an average and a count on each series and volume - never who rated what. " +
  "Ratings in a user's Private libraries never count. Each user can turn sharing off for themselves in Settings.";

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic ComicInfo library available');
test.describe.configure({ mode: 'serial' });

interface Node { id: string; displayName: string; kind: string; hasSeriesInfo?: boolean }

let saved: { prefs: Record<string, unknown>; nodes: string[] } | null = null;

test.afterAll(async ({ browser }) => {
  if (!saved) return;
  const page = await browser.newPage();
  try {
    await login(page);
    await restore(page);
  } finally {
    await page.close();
  }
});

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

/** Registers + scans the fixture library once; returns the library id and the series / plain folder ids. */
async function ensureFixture(page: Page): Promise<{ libraryId: string; series: string; plain: string }> {
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
  let plain = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50&sort=name`)).json();
    const items = root.items as Node[];
    series = items.find((n) => n.displayName === SERIES && n.hasSeriesInfo)?.id ?? '';
    plain = items.find((n) => n.displayName === PLAIN)?.id ?? '';
    return !!series && !!plain;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(true);
  saved ??= { prefs: await (await page.request.get('/api/v1/reading/library-preferences')).json(), nodes: [series, plain] };
  return { libraryId: lib!.id, series, plain };
}

async function setSwitch(page: Page, enabled: boolean): Promise<void> {
  const res = await page.request.put('/api/v1/admin/ratings/sharing', { headers: await csrf(page.request), data: { enabled } });
  expect(res.ok(), await res.text()).toBeTruthy();
}

/** Back to the shared admin's start: switch off, sharing on, no rating on the fixture nodes, the stored browse preferences. */
async function restore(page: Page): Promise<void> {
  if (!saved) return;
  const headers = await csrf(page.request);
  await page.request.put('/api/v1/admin/ratings/sharing', { headers, data: { enabled: false } });
  await page.request.put('/api/v1/ratings/sharing', { headers, data: { share: true } });
  for (const id of saved.nodes) await page.request.delete(`/api/v1/nodes/${id}/rating`, { headers });
  await page.request.put('/api/v1/reading/library-preferences', { headers, data: saved.prefs });
}

/** Shows the app in the light theme for this page only (the stored preference is never changed - the audit's method). */
async function useLightTheme(page: Page): Promise<void> {
  await page.route('**/api/v1/reading/preferences', async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    const response = await route.fetch();
    await route.fulfill({ response, json: { ...(await response.json()), theme: 'light' } });
  });
}

/** One screenshot per theme: the page as it is (dark, the default), then reloaded in light (then back to dark). */
async function shots(page: Page, name: string, reopen?: () => Promise<void>): Promise<void> {
  if (!SHOTS) return;
  const width = page.viewportSize()?.width ?? 0;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/s-${name}-${width}-dark.png` });
  if (!reopen) return;
  await useLightTheme(page);
  await page.reload();
  await reopen();
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/s-${name}-${width}-light.png` });
  await page.unroute('**/api/v1/reading/preferences');
  await page.reload();
}

async function openInfoSheet(page: Page, libraryId: string, series: string): Promise<Locator> {
  await page.goto(`/libraries/${libraryId}/browse/${series}`);
  await page.getByTestId('series-info-button').click();
  const overlay = page.getByTestId('series-overlay');
  await expect(overlay).toBeVisible();
  await expect(overlay.getByTestId('rating-slider')).not.toHaveAttribute('aria-disabled', 'true');
  return overlay;
}

/**
 * The rating block's geometry: the slider's place (from the caption) and the stars row's height - read once it has settled (the Info sheet slides in;
 * a reading taken while it moves would compare an animation, not the layout).
 */
async function ratingGeometry(block: Locator): Promise<{ x: number; y: number; height: number }> {
  // Relative to the block's caption: a phone's bottom sheet is anchored at the bottom, so the whole block moves up when the server line
  // arrives under it - the stars must not move inside the block.
  const read = async () => {
    const caption = (await block.locator('.caption').first().boundingBox())!;
    const slider = (await block.getByTestId('rating-slider').boundingBox())!;
    const row = (await block.locator('app-rating-stars').boundingBox())!;
    return { x: Math.round(slider.x - caption.x), y: Math.round(slider.y - caption.y), height: Math.round(row.height) };
  };
  let last = await read();
  for (let i = 0; i < 30; i++) {
    await block.page().waitForTimeout(150);
    const now = await read();
    if (now.x === last.x && now.y === last.y && now.height === last.height) return now;
    last = now;
  }
  return last;
}

for (const viewport of [{ width: 1280, height: 900 }, { width: 390, height: 844 }]) {
  const phone = viewport.width < 600;

  test(`${viewport.width} px: the admin switch, the Settings card, the server line, a stable rating row and the Server rating sort`, async ({ page }) => {
    test.setTimeout(240_000);
    await page.setViewportSize(viewport);
    await login(page);
    const { libraryId, series } = await ensureFixture(page);
    await restore(page);
    try {
      // Settings while the switch is off: the share box is disabled with the administrator hint; the recommendations row is on.
      await page.goto('/settings');
      const card = page.getByTestId('ratings-card');
      await expect(card).toBeVisible();
      await card.scrollIntoViewIfNeeded();
      await expect(card.getByRole('checkbox', { name: 'Share my ratings with other users of this server' })).toBeDisabled();
      await expect(card.getByTestId('share-ratings-off')).toHaveText('Your administrator has not turned on shared ratings.');
      await expect(card.getByRole('checkbox', { name: 'Show a "Recommended for you" row on the home page' })).toBeChecked();
      if (phone) await expectFitsScreen(page, 'Settings with the Ratings and recommendations card');
      await shots(page, 'settings-ratings-off', async () => { await card.scrollIntoViewIfNeeded(); });

      // The browse View menu offers no "Server rating" while the switch is off.
      await page.goto(`/libraries/${libraryId}/browse`);
      await page.getByRole('button', { name: 'View options' }).click();
      await expect(page.getByRole('menuitemradio', { name: /My rating/ })).toBeVisible();
      await expect(page.getByRole('menuitemradio', { name: /Server rating/ })).toHaveCount(0);
      await expect(page.getByRole('menuitemradio', { name: /My rating/ }).locator('mat-icon')).toHaveText('stars'); // V8
      await page.keyboard.press('Escape');

      // Administration: ticking asks first; Cancel leaves it off; "Turn on shared ratings" turns it on.
      await page.goto('/admin');
      const admin = page.getByTestId('admin-rating-sharing-card');
      await admin.scrollIntoViewIfNeeded();
      const box = admin.getByRole('checkbox', { name: 'Share ratings between users' });
      await expect(box).not.toBeChecked();
      await box.click();
      const confirm = admin.getByTestId('admin-rating-sharing-confirm');
      await expect(confirm).toContainText(CONFIRMATION);
      if (phone) await expectFitsScreen(page, 'Administration with the shared ratings confirmation');
      await shots(page, 'admin-shared-ratings-confirm');
      await admin.getByTestId('admin-rating-sharing-cancel').click();
      await expect(confirm).toHaveCount(0);
      await expect(box).not.toBeChecked();
      expect((await (await page.request.get('/api/v1/admin/ratings/sharing')).json()).enabled).toBe(false);
      await box.click();
      const turnedOn = page.waitForResponse((r) => r.url().endsWith('/api/v1/admin/ratings/sharing') && r.request().method() === 'PUT');
      await admin.getByTestId('admin-rating-sharing-confirm-yes').click();
      expect((await (await turnedOn).json()).enabled).toBe(true);
      await expect(box).toBeChecked();
      await shots(page, 'admin-shared-ratings-on', async () => { await page.getByTestId('admin-rating-sharing-card').scrollIntoViewIfNeeded(); });

      // The Info sheet: rating the series keeps the stars where they were (V4), then the server line shows (the admin shares).
      const overlay = await openInfoSheet(page, libraryId, series);
      const block = overlay.getByTestId('node-rating');
      const before = await ratingGeometry(block);
      await expect(block.getByTestId('server-rating')).toHaveCount(0);
      const put = page.waitForResponse((r) => r.url().endsWith(`/nodes/${series}/rating`) && r.request().method() === 'PUT');
      await block.getByTestId('rating-half-8').click();
      expect((await put).status()).toBe(204);
      await expect(block.getByTestId('rating-slider')).toHaveAttribute('aria-valuetext', '4 of 5 stars');
      await expect(block.getByTestId('rating-clear')).toBeVisible();
      expect(await ratingGeometry(block), 'the stars and the row stay put when you rate (audit V4)').toEqual(before);
      const server = block.getByTestId('server-rating');
      await expect(server).toBeVisible();
      await expect(server).toHaveAttribute('aria-label', 'Server average 4.0 of 5, 1 rating');
      await expect(server).toContainText('4.0 · 1 rating');
      // N1: one star and a number, under the stars and left-aligned with them.
      await expect(server.locator('mat-icon')).toHaveCount(1);
      const stars = (await block.getByTestId('rating-slider').boundingBox())!;
      const line = (await server.boundingBox())!;
      expect(line.y).toBeGreaterThanOrEqual(stars.y + stars.height - 1);
      expect(Math.abs(line.x - stars.x)).toBeLessThanOrEqual(2);
      if (phone) await expectFitsScreen(page, 'the Info sheet with the server line');
      await shots(page, 'info-sheet-server-line', async () => { await page.getByTestId('series-info-button').click(); });
      await page.keyboard.press('Escape');

      // The series page hero shows the same block.
      await page.goto(`/series/${series}`);
      const hero = page.getByTestId('node-rating').first();
      await expect(hero.getByTestId('server-rating')).toContainText('4.0 · 1 rating');
      if (phone) await expectFitsScreen(page, 'the series page with the server line');
      await shots(page, 'series-page-server-line', async () => { await expect(page.getByTestId('server-rating').first()).toBeVisible(); });

      // Browse: "Server rating" is offered now and lists the rated series first.
      await page.goto(`/libraries/${libraryId}/browse`);
      await page.getByRole('button', { name: 'View options' }).click();
      const sorted = page.waitForResponse((r) => r.url().includes(`/libraries/${libraryId}/browse`) && r.url().includes('sort=serverRating'));
      await page.getByRole('menuitemradio', { name: /Server rating/ }).click();
      expect((await sorted).ok()).toBeTruthy();
      await page.keyboard.press('Escape');
      await expect(page.locator('.node-wrap').first()).toContainText(SERIES);

      // Settings: the share box is live now; turning it off says so and takes the admin's rating out of the average at once.
      await page.goto('/settings');
      const share = page.getByTestId('ratings-card').getByRole('checkbox', { name: 'Share my ratings with other users of this server' });
      await expect(share).toBeEnabled();
      await expect(share).toBeChecked();
      await expect(page.getByTestId('share-ratings-off')).toHaveCount(0);
      await shots(page, 'settings-ratings-on', async () => { await page.getByTestId('ratings-card').scrollIntoViewIfNeeded(); });
      await share.click();
      await expect(page.getByText('Your ratings are no longer shared.')).toBeVisible();
      await expect(share).not.toBeChecked();
      const after = await openInfoSheet(page, libraryId, series);
      await expect(after.getByTestId('rating-slider')).toHaveAttribute('aria-valuetext', '4 of 5 stars');
      await expect(after.getByTestId('server-rating')).toHaveCount(0);
      await page.keyboard.press('Escape');
    } finally {
      await restore(page);
    }
  });

  test(`${viewport.width} px: "Rate" where a folder has no Info, and "Rate..." for one selected item`, async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize(viewport);
    await login(page);
    const { libraryId, plain } = await ensureFixture(page);
    await restore(page);
    try {
      // A folder with no series information: no Info button - "Rate" sits in its slot.
      await page.goto(`/libraries/${libraryId}/browse/${plain}`);
      const rate = page.getByTestId('folder-rate-button');
      await expect(rate).toBeVisible();
      await expect(page.getByTestId('series-info-button')).toHaveCount(0);
      await expect(rate.locator('mat-icon')).toHaveText('stars');
      await expect(rate).toHaveAttribute('aria-label', 'Rate this folder');
      if (phone) {
        const b = (await rate.boundingBox())!;
        expect(Math.round(b.width)).toBeGreaterThanOrEqual(40);
        expect(Math.round(b.height)).toBeGreaterThanOrEqual(36);
        await expectFitsScreen(page, 'the browse bar with Rate');
      }
      await shots(page, 'browse-bar-rate', async () => { await expect(page.getByTestId('folder-rate-button')).toBeVisible(); });
      await rate.click();
      const sheet = page.getByTestId('rating-sheet');
      await expect(sheet).toBeVisible();
      await expect(sheet.getByTestId('rating-sheet-archive')).toContainText('This folder');
      await expect(sheet.getByTestId('rating-sheet-folder')).toHaveCount(0);
      const put = page.waitForResponse((r) => r.url().endsWith(`/nodes/${plain}/rating`) && r.request().method() === 'PUT');
      await sheet.getByTestId('rating-half-6').click();
      expect((await put).status()).toBe(204);
      if (phone) await expectFitsScreen(page, 'the one-row Rate sheet');
      await shots(page, 'rate-sheet-one-row');
      await sheet.getByRole('button', { name: 'Close' }).click();
      await expect(sheet).toHaveCount(0);
      await expect(rate).toContainText('3');
      await expect(rate).toHaveAttribute('aria-label', 'Your rating 3 of 5 - rate this folder');

      // The selection bar: "Rate..." right after Favorites, for exactly one selected item.
      await page.goto(`/libraries/${libraryId}/browse`);
      await page.locator('button.select-toggle').click();
      await openSelectionMore(page);
      const action = page.getByTestId('selection-rate');
      await expect(action).toBeDisabled();
      await page.locator('.node-wrap', { hasText: PLAIN }).locator('.node-card').click();
      await openSelectionMore(page);
      await expect(action).toBeEnabled();
      await expect(action.locator('mat-icon')).toHaveText('stars');
      const order = await page.locator('app-selection-bar .actions').evaluate((el) =>
        Array.from(el.querySelectorAll('[data-testid]')).map((e) => e.getAttribute('data-testid')));
      expect(order.indexOf('selection-rate')).toBe(order.indexOf('selection-favorites') + 1);
      await shots(page, 'selection-rate');
      await action.click();
      const fromBar = page.getByTestId('rating-sheet');
      await expect(fromBar.getByTestId('rating-sheet-archive')).toContainText('This folder');
      await expect(fromBar.getByTestId('rating-slider')).toHaveAttribute('aria-valuetext', '3 of 5 stars');
      await fromBar.getByRole('button', { name: 'Close' }).click();
    } finally {
      await restore(page);
    }
  });
}

test('list rows keep the star and (i) in fixed columns whatever the badges (audit V7); the reader Rate icon is stars (V8)', async ({ page }) => {
  test.setTimeout(150_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  await login(page);
  const { libraryId, series, plain } = await ensureFixture(page);
  await restore(page);
  try {
    const headers = await csrf(page.request);
    // One rated row (its rating badge sits in the marker group), the others not - the badge must not push the star.
    expect((await page.request.put(`/api/v1/nodes/${series}/rating`, { headers, data: { score: 9 } })).ok()).toBeTruthy();
    await page.request.put('/api/v1/reading/library-preferences', { headers, data: { ...saved!.prefs, viewMode: 'list' } });
    await page.goto(`/libraries/${libraryId}/browse`);
    await expect(page.locator('.node-wrap', { hasText: SERIES }).getByTestId('rating-badge')).toBeVisible();
    await expect(page.locator('.node-wrap', { hasText: PLAIN })).toBeVisible();
    const gaps = await page.locator('.node-wrap').evaluateAll((rows) => rows.map((row) => {
      const card = row.querySelector('.node-card')!.getBoundingClientRect();
      const star = row.querySelector('.row-markers app-star-toggle');
      const info = row.querySelector('.row-markers app-info-toggle');
      return {
        star: star ? Math.round(card.right - star.getBoundingClientRect().right) : null,
        info: info ? Math.round(card.right - info.getBoundingClientRect().right) : null,
      };
    }));
    const stars = gaps.map((g) => g.star).filter((v): v is number => v !== null);
    const infos = gaps.map((g) => g.info).filter((v): v is number => v !== null);
    expect(stars.length).toBeGreaterThan(1);
    expect(new Set(stars).size, `star distance from the row edge: ${stars.join(', ')}`).toBe(1);
    expect(new Set(infos).size, `(i) distance from the row edge: ${infos.join(', ')}`).toBe(1);
    await shots(page, 'list-rows-columns', async () => { await expect(page.locator('.node-wrap').first()).toBeVisible(); });

    // V8: the reader bar's Rate is the circled star, not a second favourite-like star.
    const archives = (await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?parentId=${plain}&pageSize=10`)).json()).items as Node[];
    await page.goto(`/reader/${archives.find((n) => n.kind === 'Archive')!.id}`);
    await expect(page.getByTestId('reader-rate').locator('mat-icon')).toHaveText('stars');
  } finally {
    await restore(page);
  }
});
