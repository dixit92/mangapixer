import { test, expect, APIRequestContext, Browser, Page } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * The Favorites view (1.50.0 lane F, the owner's "the favorites tab is barren"): a reader's Favorites page starts from browse's display
 * values on the device, switches to List, changes the sizes, sorts by name, filters to unread, opens an archive and comes Back to the
 * same place (same list, sort, filter, no reload). Runs with a throwaway reader granted the synthetic series library, so the admin
 * account the other specs share keeps its favorites and preferences.
 *
 * Needs the synthetic ComicInfo library from `e2e/fixtures/make-series-fixtures.mjs` (as series-info.spec.ts).
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';
const START_PASSWORD = 'FavViewStart123!';
const PASSWORD = 'FavViewUser123!';

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic ComicInfo library available');
test.describe.configure({ mode: 'serial' });

interface Node { id: string; displayName: string; kind: string }

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get('/api/v1/auth/csrf');
  const { token } = await res.json();
  return { 'X-MangaPixer-Csrf': token };
}

/** As the admin: registers + scans the fixture library once (whichever spec runs first), creates a reader granted it. */
async function setUp(request: APIRequestContext): Promise<{ username: string; libraryId: string }> {
  expect((await request.post('/api/v1/auth/login', { data: { username: ADMIN_USER, password: ADMIN_PASSWORD } })).ok()).toBeTruthy();
  let headers = await csrf(request);
  const libs: { id: string; name: string }[] = await (await request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: FIXTURE_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  await expect.poll(async () => {
    const root = await (await request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    return (root.items as Node[]).filter((n) => ['Synthetic Series', 'Synthetic Anthology', 'Plain Folder'].includes(n.displayName)).length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(3);

  const username = `favview${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const created = await request.post('/api/v1/admin/users', { headers, data: { username, password: START_PASSWORD, isAdmin: false } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const userId = (await created.json()).user.id as string;
  const grant = await request.put(`/api/v1/admin/users/${userId}/grants/${lib!.id}`, { headers });
  expect(grant.ok(), await grant.text()).toBeTruthy();
  await request.post('/api/v1/auth/logout', { headers });

  expect((await request.post('/api/v1/auth/login', { data: { username, password: START_PASSWORD } })).ok()).toBeTruthy();
  headers = await csrf(request);
  const changed = await request.post('/api/v1/auth/change-password', {
    headers, data: { currentPassword: START_PASSWORD, newPassword: PASSWORD },
  });
  expect(changed.ok(), await changed.text()).toBeTruthy();
  await request.post('/api/v1/auth/logout', { headers: await csrf(request) });
  return { username, libraryId: lib!.id };
}

/** A new browser context = a new device, signed in as the reader. */
async function device(browser: Browser, username: string, width = 1280, height = 900): Promise<Page> {
  const context = await browser.newContext({ baseURL: test.info().project.use.baseURL, viewport: { width, height } });
  const page = await context.newPage();
  const res = await page.request.post('/api/v1/auth/login', { data: { username, password: PASSWORD } });
  expect(res.ok(), await res.text()).toBeTruthy();
  return page;
}

async function children(page: Page, libraryId: string, parentId?: string): Promise<Node[]> {
  const q = parentId ? `&parentId=${parentId}` : '';
  return (await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=100${q}`)).json()).items as Node[];
}

/**
 * The reader's favorites: the folders "Synthetic Series" and "Plain Folder", both archives of "Synthetic Anthology" (a stack), "No Info
 * 01" and "Synthetic Saga v01" - five entries. "Synthetic Saga v01" is read (so "Synthetic Series" is Reading). Returns that archive's id.
 */
async function starAndRead(page: Page, libraryId: string): Promise<{ noInfo: string }> {
  const headers = await csrf(page.request);
  const root = await children(page, libraryId);
  const folder = (name: string) => root.find((n) => n.displayName === name)!.id;
  const series = folder('Synthetic Series');
  const plain = folder('Plain Folder');
  const anthology = await children(page, libraryId, folder('Synthetic Anthology'));
  const sagas = await children(page, libraryId, series);
  const plainItems = await children(page, libraryId, plain);
  const saga1 = sagas.find((n) => n.displayName.startsWith('Synthetic Saga v01'))!.id;
  const noInfo = plainItems.find((n) => n.displayName.startsWith('No Info 01'))!.id;
  for (const id of [series, plain, ...anthology.map((n) => n.id), noInfo, saga1]) {
    expect((await page.request.post(`/api/v1/nodes/${id}/favorite`, { headers })).ok()).toBeTruthy();
  }
  expect((await page.request.put(`/api/v1/reading/${saga1}/read`, { headers })).ok()).toBeTruthy();
  return { noInfo };
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

const titles = (page: Page) => page.locator('[data-testid="favorites-list"] .node-title, [data-testid="favorites-list"] .fav-title').allTextContents();
const stored = (page: Page, key: string) => page.evaluate((k) => localStorage.getItem(k), key);

/** Picks a menu item after opening its menu, and waits for the favorites request it starts (when it starts one). */
async function pick(page: Page, button: string, item: string, expectRequest: RegExp | null): Promise<void> {
  await page.getByTestId(button).click();
  const menuItem = page.getByTestId(item);
  await expect(menuItem).toBeVisible();
  const answered = expectRequest ? page.waitForResponse((r) => expectRequest.test(r.url()) && r.request().method() === 'GET') : null;
  await menuItem.click();
  if (answered) expect((await answered).ok()).toBeTruthy();
  await expect(page.locator('.cdk-overlay-pane .mat-mdc-menu-panel')).toHaveCount(0);
}

test('the Favorites view: list, sizes, name sort, unread filter, and Back from the reader to the same place', async ({ browser, request }) => {
  test.setTimeout(180_000);
  const { username, libraryId } = await setUp(request);
  const page = await device(browser, username);
  try {
    const { noInfo } = await starAndRead(page, libraryId);

    // A device whose BROWSE card size is 200 px: the first Favorites visit starts from it (seeded once, stored as Favorites' own).
    await page.goto('/');
    await page.evaluate(() => localStorage.setItem('mangapixer-card-size', '200'));
    await page.goto('/favorites');
    await expect(page.locator('.fav-card')).toHaveCount(5);
    await expect(page.getByTestId('favorites-card-size')).toHaveValue('200');
    await expect.poll(() => stored(page, 'mangapixer-favorites-card-size')).toBe('200');
    await expect.poll(() => stored(page, 'mangapixer-favorites-view-mode')).toBe('card');
    // Every archive card has its cover from the server (1.50.0).
    await expect(page.locator('.fav-card img').first()).toBeVisible();
    // Lane W's context line: the two single archives name their folders; the folders and the stack carry none (membership, not position).
    const contextLines = page.locator('[data-testid="favorites-list"] [data-testid="context-line"]');
    await expect(contextLines).toHaveCount(2);
    expect((await contextLines.allTextContents()).map((t) => t.trim()).sort()).toEqual(['Plain Folder', 'Synthetic Series']);

    // Card size: the slider changes the grid and is stored for Favorites only (browse keeps its 200).
    await page.getByTestId('favorites-card-size').fill('130');
    await expect.poll(() => stored(page, 'mangapixer-favorites-card-size')).toBe('130');
    expect(await stored(page, 'mangapixer-card-size')).toBe('200');
    await expect(page.getByTestId('favorites-list')).toHaveAttribute('style', /--card-size: 130px/);

    // List view through the shared row; the columns slider.
    await pick(page, 'favorites-view-button', 'favorites-view-list', null);
    await expect(page.locator('[data-testid="favorites-list"] app-node-row')).toHaveCount(5);
    await expect.poll(() => stored(page, 'mangapixer-favorites-view-mode')).toBe('list');
    await page.getByTestId('favorites-list-columns').fill('1');
    await expect.poll(() => stored(page, 'mangapixer-favorites-list-columns')).toBe('1');
    expect(await stored(page, 'mangapixer-list-columns')).not.toBe('1');
    // A stack row carries its count and opens its folder filtered to favorites.
    await expect(page.getByTestId('row-stack-count')).toHaveText('2');
    // The list rows carry the same context lines.
    await expect(contextLines).toHaveCount(2);
    expect((await contextLines.allTextContents()).map((t) => t.trim()).sort()).toEqual(['Plain Folder', 'Synthetic Series']);

    // Sort by name: folders (and the stack after the folders it sorts with) first, then the archives - stored for this device.
    await pick(page, 'favorites-view-button', 'favorites-sort-name', /\/api\/v1\/favorites\?.*sort=name/);
    await expect.poll(() => titles(page)).toEqual([
      'Plain Folder', 'Synthetic Anthology', 'Synthetic Series', expect.stringMatching(/^No Info 01/), expect.stringMatching(/^Synthetic Saga v01/),
    ]);
    expect(await stored(page, 'mangapixer-favorites-sort')).toBe('name');

    // Filter unread: the read archive and the partly read folder go.
    await pick(page, 'favorites-filter-button', 'favorites-read-unread', /\/api\/v1\/favorites\?.*readState=unread/);
    await expect.poll(() => titles(page)).toEqual(['Plain Folder', 'Synthetic Anthology', expect.stringMatching(/^No Info 01/)]);
    await expect(page.getByTestId('favorites-filter-button')).toContainText('Unread');
    await shot(page, 'favorites-view-list-desktop');

    // Open an archive and come back: the same list, sort and filter, without asking the server again.
    let favoritesRequests = 0;
    page.on('request', (r) => { if (r.url().includes('/api/v1/favorites')) favoritesRequests++; });
    await page.locator(`app-node-row a[href="/reader/${noInfo}"]`).click();
    await expect(page).toHaveURL(new RegExp(`/reader/${noInfo}`));
    await page.goBack();
    await expect(page).toHaveURL(/\/favorites$/);
    await expect.poll(() => titles(page)).toEqual(['Plain Folder', 'Synthetic Anthology', expect.stringMatching(/^No Info 01/)]);
    await expect(page.getByTestId('favorites-filter-button')).toContainText('Unread');
    await expect(page.locator('[data-testid="favorites-list"] app-node-row')).toHaveCount(3);
    expect(favoritesRequests).toBe(0);

    // A fresh visit (not Back) keeps the device's view and sort.
    await page.goto('/');
    await page.goto('/favorites');
    await expect(page.locator('[data-testid="favorites-list"] app-node-row')).toHaveCount(5);
    await expect.poll(() => titles(page)).toEqual([
      'Plain Folder', 'Synthetic Anthology', 'Synthetic Series', expect.stringMatching(/^No Info 01/), expect.stringMatching(/^Synthetic Saga v01/),
    ]);
    await expectFitsScreen(page, 'favorites list (desktop)');
  } finally {
    await page.context().close();
  }
});

test('the Favorites view fits a phone: the sliders sit in the View menu, the bar stays one screen wide', async ({ browser, request }) => {
  test.setTimeout(180_000);
  const { username, libraryId } = await setUp(request);
  const page = await device(browser, username, 390, 844);
  try {
    await starAndRead(page, libraryId);
    await page.goto('/favorites');
    await expect(page.locator('.fav-card')).toHaveCount(5);
    await expect(page.getByTestId('favorites-card-size')).toBeHidden();
    await expectFitsScreen(page, 'favorites cards (phone)');

    await page.getByTestId('favorites-view-button').click();
    await expect(page.locator('.view-options-menu .view-size-section input[aria-label="Card size"]')).toBeVisible();
    await shot(page, 'favorites-view-menu-phone');
    await expectFitsScreen(page, 'favorites View menu (phone)');
    await page.getByTestId('favorites-view-list').click();
    await expect(page.locator('[data-testid="favorites-list"] app-node-row')).toHaveCount(5);
    await expectFitsScreen(page, 'favorites list (phone)');

    await page.getByTestId('favorites-filter-button').click();
    await expect(page.getByTestId('favorites-kind-archives')).toBeVisible();
    await expectFitsScreen(page, 'favorites Filter menu (phone)');
    await page.getByTestId('favorites-kind-archives').click();
    await expect(page.locator('[data-testid="favorites-list"] app-node-row')).toHaveCount(3);
    await shot(page, 'favorites-view-list-phone');
  } finally {
    await page.context().close();
  }
});
