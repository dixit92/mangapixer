import { test, expect, Page, APIRequestContext, Route } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * The Info panel and the tag view (1.45.0, D1 2.6), on the owner's path: browse a folder -> "Info" -> a creator chip -> the search
 * page filtered by that creator -> a WORK card opens the folder where the work lives. On the series library of
 * `e2e/fixtures/make-series-fixtures.mjs` (`Synthetic Series`: ComicInfo series "Synthetic Saga", writer "Test Writer", artist
 * "Test Artist", genres Action / Fantasy).
 *
 * The tag index and its routes come from lanes F / Q of the same cycle. Until the server answers them, the two routes are answered here
 * with the shapes those lanes publish (`page.route`), built from the same fixture values; once the server answers (`buckets` / `works`
 * in its JSON) the real answer is passed through and the spec checks the real index (it waits for the background build).
 * Optional: E2E_SCREENSHOT_DIR saves the screenshots.
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

/** The series library (registered + scanned once, shared with series-info.spec.ts) and its "Synthetic Series" folder. */
async function ensureLibrary(page: Page): Promise<{ libraryId: string; libraryName: string; folder: Node }> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: FIXTURE_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folder: Node | undefined;
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = (root.items as Node[]).find((n) => n.displayName === 'Synthetic Series');
    return folder?.hasSeriesInfo === true;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(true);
  return { libraryId: lib!.id, libraryName: lib!.name, folder: folder! };
}

/** The fixture's tags as lane Q's `GET nodes/{id}/info` answers them (single work: its own values; counts of 1 are left out). */
function fixturePanel(nodeId: string) {
  const chip = (valueId: string, display: string, extra: Record<string, unknown> = {}) => ({ valueId, display, ...extra });
  return {
    nodeId,
    container: false,
    works: 1,
    buckets: [
      { bucket: 'Creators', more: 0, chips: [chip('fv_e2ew', 'Test Writer', { role: 'Writer', creatorId: 'cr_e2ew' }), chip('fv_e2ea', 'Test Artist', { role: 'Artist', creatorId: 'cr_e2ea' })] },
      { bucket: 'Publisher', more: 0, chips: [chip('fv_e2ep', 'Synthetic Press')] },
      { bucket: 'Year', more: 0, chips: [chip('fv_e2ey', '2020')] },
      { bucket: 'Genres', more: 0, chips: [chip('fv_e2eg1', 'Action'), chip('fv_e2eg2', 'Fantasy')] },
    ],
  };
}

/**
 * Answers the Info and tag-search routes with the fixture's shapes while the server does not serve them yet (lanes F / Q), and passes
 * the server's answer through once it does. Returns whether a mock answered (for the note in the report).
 */
async function serveTagRoutes(page: Page, ids: { libraryId: string; libraryName: string; folder: Node }): Promise<{ mocked: boolean }> {
  const state = { mocked: false };
  await page.route('**/api/v1/nodes/*/info', async (route: Route) => {
    const res = await route.fetch();
    const body = res.ok() ? await res.json().catch(() => null) : null;
    if (body && Array.isArray(body.buckets)) return route.fulfill({ response: res });
    state.mocked = true;
    const nodeId = new URL(route.request().url()).pathname.split('/').at(-2)!;
    const panel = nodeId === ids.folder.id ? fixturePanel(nodeId) : { nodeId, container: false, works: 0, buckets: [] };
    return route.fulfill({ json: panel });
  });
  await page.route(/\/api\/v1\/search\?/, async (route: Route) => {
    const url = new URL(route.request().url());
    const f = url.searchParams.getAll('f');
    if (f.length === 0) return route.continue();
    const res = await route.fetch();
    const body = res.ok() ? await res.json().catch(() => null) : null;
    if (body && Array.isArray(body.works)) return route.fulfill({ response: res });
    state.mocked = true;
    const names: Record<string, [string, string]> = { 'creator:fv_e2ew': ['Creators', 'Test Writer'], 'genre:fv_e2eg1': ['Genres', 'Action'] };
    const filters = f.filter((p) => names[p]).map((p) => ({ param: p, bucket: names[p][0], valueId: p.split(':')[1], display: names[p][1] }));
    const works = filters.length === f.length
      ? [{
          workId: 'w_e2e', title: 'Synthetic Saga', coverUrl: null, locationCount: 1,
          locations: [{ nodeId: ids.folder.id, libraryId: ids.libraryId, kind: 'Folder', displayName: 'Synthetic Series', trail: [{ id: ids.libraryId, displayName: ids.libraryName }] }],
        }]
      : [];
    return route.fulfill({ json: { query: url.searchParams.get('q') ?? '', items: [], totalCount: works.length, nextCursor: null, hasMore: false, works, filters } });
  });
  return state;
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/info-${name}.png`, fullPage: false });
}

/** Whether a tap at the element's centre reaches it (not an overlay or a neighbour on top). */
async function tapHits(page: Page, selector: string): Promise<boolean> {
  return page.locator(selector).first().evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!hit && (hit === el || el.contains(hit));
  });
}

/** Opens the folder's Info panel; with the real index, waits until its background build shows the fixture's chips. */
async function openInfo(page: Page, libraryId: string, folderId: string): Promise<void> {
  await expect(async () => {
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    await page.getByRole('button', { name: 'Info', exact: true }).click();
    await expect(page.getByTestId('series-overlay').getByTestId('info-tags')).toBeVisible({ timeout: 5_000 });
  }).toPass({ timeout: 120_000, intervals: [2_000, 5_000, 10_000] });
}

test('browse -> Info -> a creator chip -> the tag search -> a work card opens its folder', async ({ page }) => {
  test.setTimeout(240_000);
  await login(page);
  const ids = await ensureLibrary(page);
  await serveTagRoutes(page, ids);

  await openInfo(page, ids.libraryId, ids.folder.id);
  const overlay = page.getByTestId('series-overlay');
  await expect(overlay.locator('.kicker')).toHaveText('Info');
  const creators = overlay.locator('[data-testid="info-bucket"][data-bucket="Creators"]');
  await expect(creators.locator('.label')).toHaveText('Creators');
  const writer = creators.getByTestId('info-chip').filter({ hasText: 'Test Writer' });
  await expect(writer).toBeVisible();
  // The chips replace the plain-text credits line; a count of 1 is not shown.
  await expect(overlay.locator('.credits')).toHaveCount(0);
  await expect(writer.getByTestId('info-chip-count')).toHaveCount(0);
  // Admins get the creator menu next to the chip.
  await expect(creators.getByTestId('info-chip-menu').first()).toBeVisible();
  await shot(page, '01-overlay-desktop');

  await writer.click();
  await expect(page).toHaveURL(/\/search\?f=creator(:|%3A)[A-Za-z0-9_]+$/);
  await expect(overlay).toHaveCount(0);
  const filterChip = page.getByTestId('search-filter-chip');
  await expect(filterChip).toHaveCount(1);
  await expect(filterChip).toContainText('Test Writer');
  const card = page.getByTestId('work-card').filter({ hasText: 'Synthetic Saga' });
  await expect(card).toBeVisible();
  await expect(card.locator('.crumb-line')).toContainText(`${ids.libraryName} › Synthetic Series`);
  await expectFitsScreen(page, 'tag search');
  await shot(page, '02-tag-search');

  // A reload keeps the filter (it is in the URL).
  await page.reload();
  await expect(page.getByTestId('work-card').filter({ hasText: 'Synthetic Saga' })).toBeVisible();

  await page.getByTestId('work-card').filter({ hasText: 'Synthetic Saga' }).locator('a.work-link').click();
  await expect(page).toHaveURL(new RegExp(`/libraries/${ids.libraryId}/browse/${ids.folder.id}$`));
  await expect(page.locator('.node-wrap').first()).toBeVisible();
});

test('removing the filter chip leaves the tag search', async ({ page }) => {
  test.setTimeout(240_000);
  await login(page);
  const ids = await ensureLibrary(page);
  await serveTagRoutes(page, ids);
  await openInfo(page, ids.libraryId, ids.folder.id);
  await page.getByTestId('series-overlay').getByTestId('info-chip').filter({ hasText: 'Test Writer' }).click();
  await expect(page.getByTestId('work-card').first()).toBeVisible();

  await page.getByTestId('search-filter-chip').getByRole('button', { name: /Remove filter/ }).click();
  await expect(page).toHaveURL(/\/search$/);
  await expect(page.getByTestId('search-filter-chip')).toHaveCount(0);
  await expect(page.getByTestId('work-card')).toHaveCount(0);
});

test('phone: the Info bottom sheet shows the chips, and a tap reaches them', async ({ page }) => {
  test.setTimeout(240_000);
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const ids = await ensureLibrary(page);
  await serveTagRoutes(page, ids);
  await openInfo(page, ids.libraryId, ids.folder.id);

  await expect(page.locator('.series-info-bottom-sheet')).toBeVisible();
  const chip = '.series-info-bottom-sheet [data-testid="info-chip"]';
  await expect(page.locator(chip).first()).toBeVisible();
  const box = await page.locator(chip).first().boundingBox();
  expect(Math.round(box!.height)).toBe(32);
  await expect.poll(() => tapHits(page, chip), { timeout: 3_000 }).toBe(true);
  await expect.poll(() => tapHits(page, '.series-info-bottom-sheet [data-testid="info-chip-menu"]'), { timeout: 3_000 }).toBe(true);
  await expectFitsScreen(page, 'Info sheet');
  await shot(page, '03-sheet-phone');

  await page.locator(chip).filter({ hasText: 'Test Writer' }).click();
  await expect(page).toHaveURL(/\/search\?f=creator(:|%3A)/);
  await expect(page.locator('.series-info-bottom-sheet')).toHaveCount(0);
  await expect(page.getByTestId('work-card').first()).toBeVisible();
  await expectFitsScreen(page, 'tag search on a phone');
});
