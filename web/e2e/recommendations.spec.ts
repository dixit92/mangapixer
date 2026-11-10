import { test, expect, APIRequestContext, Browser, Page } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Recommendations (1.53.0, lane C): Home "Recommended for you" (after Continue reading, audit N3) and the series page's "More like this"
 * (after About, audit N2), on the REAL tag index of `<E2E_SERIES_FIXTURE_ROOT>-recommendations` (five ComicInfo series - see
 * `fixtures/make-series-fixtures.mjs`): a throwaway reader granted only that library rates "Quartz Harbor" five stars, and both rows
 * then show "Quartz Tide" (the same writer) and "Velvet Orchard" (the shared Lighthouse tag and Mystery genre) with their reasons - not
 * the rated series itself, nor the unrelated ones. Home's card-size slider sits in the first strip's heading row from 600 px and is not
 * shown on a phone (audit V10). Both rows fit 360 / 390 / 820 px. No mocked routes. Optional: E2E_SCREENSHOT_DIR keeps desktop + 390 px
 * shots in dark and light.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const RECOMMENDATIONS_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-recommendations` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Recommendation Fixtures';
const START_PASSWORD = 'RecStart123!';
const PASSWORD = 'RecReader123!';

test.skip(!RECOMMENDATIONS_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic recommendation library available');
test.describe.configure({ mode: 'serial' });

interface Node { id: string; displayName: string; kind: string }

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get('/api/v1/auth/csrf');
  const { token } = await res.json();
  return { 'X-MangaPixer-Csrf': token };
}

let world: { username: string; harborId: string } | null = null;

/** Once per run: the library scanned and indexed, a reader granted only it who rated "Quartz Harbor" 10 (five stars). */
async function setUp(request: APIRequestContext): Promise<{ username: string; harborId: string }> {
  if (world) return world;
  expect((await request.post('/api/v1/auth/login', { data: { username: ADMIN_USER, password: ADMIN_PASSWORD } })).ok()).toBeTruthy();
  let headers = await csrf(request);
  const libs: { id: string; name: string }[] = await (await request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: RECOMMENDATIONS_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folders: Node[] = [];
  await expect.poll(async () => {
    folders = (await (await request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json()).items as Node[];
    return folders.filter((n) => n.kind === 'Folder').length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(5);
  const harbor = folders.find((n) => n.displayName === 'Quartz Harbor')!;
  // The tag index builds in the background: wait until the writer of "Quartz Harbor" is a chip of its Info panel.
  await expect.poll(async () => {
    const info = await request.get(`/api/v1/nodes/${harbor.id}/info`);
    return info.ok() ? JSON.stringify(await info.json()).includes('Ilse Marrow') : false;
  }, { timeout: 120_000, intervals: [2000, 5000] }).toBe(true);

  const username = `recreader${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const created = await request.post('/api/v1/admin/users', { headers, data: { username, password: START_PASSWORD, isAdmin: false } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const userId = (await created.json()).user.id as string;
  expect((await request.put(`/api/v1/admin/users/${userId}/grants/${lib!.id}`, { headers })).ok()).toBeTruthy();
  await request.post('/api/v1/auth/logout', { headers });

  expect((await request.post('/api/v1/auth/login', { data: { username, password: START_PASSWORD } })).ok()).toBeTruthy();
  headers = await csrf(request);
  const changed = await request.post('/api/v1/auth/change-password', { headers, data: { currentPassword: START_PASSWORD, newPassword: PASSWORD } });
  expect(changed.ok(), await changed.text()).toBeTruthy();
  expect((await request.post('/api/v1/auth/login', { data: { username, password: PASSWORD } })).ok()).toBeTruthy();
  headers = await csrf(request);
  expect((await request.put(`/api/v1/nodes/${harbor.id}/rating`, { headers, data: { score: 10 } })).status()).toBe(204);
  await request.post('/api/v1/auth/logout', { headers: await csrf(request) });
  world = { username, harborId: harbor.id };
  return world;
}

/** A new device signed in as the reader, at this size and theme. */
async function device(browser: Browser, username: string, width: number, height: number, theme: 'dark' | 'light' = 'dark'): Promise<Page> {
  const context = await browser.newContext({ baseURL: test.info().project.use.baseURL, viewport: { width, height } });
  const page = await context.newPage();
  const res = await page.request.post('/api/v1/auth/login', { data: { username, password: PASSWORD } });
  expect(res.ok(), await res.text()).toBeTruthy();
  const saved = await page.request.put('/api/v1/reading/preferences/appearance', {
    headers: await csrf(page.request), data: { theme, accent: null },
  });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  return page;
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${SHOTS}/recommendations-${name}.png`, fullPage: true });
}

const reasons = (row: ReturnType<Page['getByTestId']>) => row.getByTestId('recommendation-reason');

/** The card whose TITLE is `title` (a reason line may name another series, so the card's whole text is not enough). */
const card = (row: ReturnType<Page['getByTestId']>, title: string) =>
  row.getByTestId('recommendation-card').filter({ has: row.page().getByTestId('recommendation-title').getByText(new RegExp(`^${title}`)) });

async function expectPicks(row: ReturnType<Page['getByTestId']>): Promise<void> {
  await expect(row).toBeVisible();
  await expect(card(row, 'Quartz Tide')).toHaveCount(1);
  await expect(card(row, 'Quartz Tide').getByTestId('recommendation-reason')).toHaveText('Same author: Ilse Marrow');
  await expect(card(row, 'Velvet Orchard')).toHaveCount(1);
  await expect(card(row, 'Quartz Harbor')).toHaveCount(0); // the rated series itself
  await expect(card(row, 'Saffron Kite')).toHaveCount(0); // nothing in common
  await expect(reasons(row).first()).toHaveAttribute('title', /.+/);
}

test('Home shows "Recommended for you" from the reader\'s own rating, with the card-size slider in its heading row', async ({ browser, request }) => {
  test.setTimeout(240_000);
  const { username } = await setUp(request);
  const page = await device(browser, username, 1280, 900);
  await page.goto('/');
  const row = page.getByTestId('home-recommendations');
  await expectPicks(row);
  await expect(row.getByRole('heading', { name: 'Recommended for you' })).toBeVisible();
  const velvet = card(row, 'Velvet Orchard').getByTestId('recommendation-reason');
  await expect(velvet).toHaveText(/^Like Quartz Harbor: /);
  // V10: the reader has no Continue reading, so this is the first strip: its heading row carries the one slider; no caption row.
  await expect(page.locator('.home-toolbar')).toHaveCount(0);
  await expect(row.getByRole('slider', { name: 'Card size' })).toBeVisible();
  await expect(page.getByRole('slider', { name: 'Card size' })).toHaveCount(1);
  await shot(page, 'home-1280-dark');
  await expectFitsScreen(page, 'Home with Recommended for you');
  await page.context().close();
});

test('the series page shows "More like this" after About and before Details', async ({ browser, request }) => {
  test.setTimeout(240_000);
  const { username, harborId } = await setUp(request);
  const page = await device(browser, username, 1280, 900);
  await page.goto(`/series/${harborId}`);
  const row = page.getByTestId('series-more-like-this');
  await expectPicks(row);
  await expect(card(row, 'Velvet Orchard').getByTestId('recommendation-reason')).not.toHaveText(/^Like /); // no "Like <this page>" here
  // Order: About, then the row, then the next block.
  const order = await page.evaluate(() => {
    const blocks = Array.from(document.querySelectorAll('[data-testid="series-page"] details.section > summary, [data-testid="series-more-like-this"] h3'));
    return blocks.map((b) => (b.textContent ?? '').trim());
  });
  expect(order[0]).toBe('About');
  expect(order[1]).toBe('More like this');
  await shot(page, 'series-1280-dark');
  await expectFitsScreen(page, 'series page with More like this');
  await page.context().close();
});

test('both rows fit 360, 390 and 820 px; on a phone Home shows no card-size slider', async ({ browser, request }) => {
  test.setTimeout(300_000);
  const { username, harborId } = await setUp(request);
  for (const [width, height] of [[360, 780], [390, 844], [820, 1180]] as const) {
    const page = await device(browser, username, width, height);
    await page.goto('/');
    await expect(page.getByTestId('home-recommendations')).toBeVisible();
    const slider = page.getByRole('slider', { name: 'Card size' });
    if (width < 600) await expect(slider).toBeHidden();
    else await expect(slider).toBeVisible();
    await expectFitsScreen(page, `Home at ${width} px`);
    if (width === 390) await shot(page, 'home-390-dark');
    await page.goto(`/series/${harborId}`);
    await expect(page.getByTestId('series-more-like-this')).toBeVisible();
    await expectFitsScreen(page, `series page at ${width} px`);
    if (width === 390) await shot(page, 'series-390-dark');
    await page.context().close();
  }
});

test('light theme: both rows at desktop and 390 px (screenshots for the visual check)', async ({ browser, request }) => {
  test.setTimeout(240_000);
  const { username, harborId } = await setUp(request);
  for (const [width, height] of [[1280, 900], [390, 844]] as const) {
    const page = await device(browser, username, width, height, 'light');
    await page.goto('/');
    await expect(page.getByTestId('home-recommendations')).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    await shot(page, `home-${width}-light`);
    await page.goto(`/series/${harborId}`);
    await expect(page.getByTestId('series-more-like-this')).toBeVisible();
    await shot(page, `series-${width}-light`);
    await page.context().close();
  }
});
