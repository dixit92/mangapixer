import { test, expect, APIRequestContext, Browser, Page } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * 1.51.0 lane F (owner decisions 2, 8-10): the Favorites page's jump rail, Recently read sort and "Group by library", as a throwaway
 * reader granted two synthetic libraries: "Favorites Rail" (`<E2E_SERIES_FIXTURE_ROOT>-favorites`: 60 root archives `<Word> Ravelin 1..3`,
 * words Acorn .. Thistle = letters A .. T - every archive its own entry) and "Series Fixtures" (two starred folders). 62 entries: more than
 * one page of 50, so a chip past the first page reloads the list there and the page then scrolls UP. Checked at 1280, 820, 390 and 360 px.
 *
 * Needs the synthetic fixtures from `e2e/fixtures/make-series-fixtures.mjs`. Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const FAVORITES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-favorites` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const SERIES_LIBRARY = 'Series Fixtures';
const RAIL_LIBRARY = 'Favorites Rail';
const START_PASSWORD = 'FavGroupsStart123!';
const PASSWORD = 'FavGroupsUser123!';
const WORDS = ['Acorn', 'Brook', 'Cinder', 'Dapple', 'Ember', 'Fennel', 'Gale', 'Hollow', 'Ivy', 'Juniper', 'Kelp', 'Lark', 'Moss',
  'Nettle', 'Oriel', 'Pebble', 'Quince', 'Rowan', 'Sorrel', 'Thistle'];
const LETTERS = WORDS.map((w) => w[0]);

test.skip(!FAVORITES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic favorites library available');
test.beforeEach(({ page }) => { page.setDefaultTimeout(20_000); });

interface Node { id: string; displayName: string; kind: string }

let reader: { username: string } | null = null;

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get('/api/v1/auth/csrf');
  const { token } = await res.json();
  return { 'X-MangaPixer-Csrf': token };
}

async function browse(request: APIRequestContext, libraryId: string): Promise<Node[]> {
  return (await (await request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=100`)).json()).items as Node[];
}

/** As the admin: finds or registers (and scans) a fixture library by name. */
async function library(request: APIRequestContext, name: string, root: string): Promise<string> {
  const libs: { id: string; name: string }[] = await (await request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === name);
  if (!lib) {
    const headers = await csrf(request);
    const created = await request.post('/api/v1/admin/libraries', { headers, data: { displayName: name, rootPath: root } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  return lib!.id;
}

/**
 * Once per run: both libraries scanned, a reader granted both who starred the 60 Ravelin archives and the folders "Synthetic Series" and
 * "Plain Folder", and read "Moss Ravelin 2".
 */
async function setUp(request: APIRequestContext): Promise<{ username: string }> {
  if (reader) return reader;
  expect((await request.post('/api/v1/auth/login', { data: { username: ADMIN_USER, password: ADMIN_PASSWORD } })).ok()).toBeTruthy();
  const seriesId = await library(request, SERIES_LIBRARY, FIXTURE_ROOT!);
  const railId = await library(request, RAIL_LIBRARY, FAVORITES_ROOT!);
  await expect.poll(async () => (await browse(request, railId)).filter((n) => /Ravelin \d/.test(n.displayName)).length,
    { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(60);
  await expect.poll(async () => (await browse(request, seriesId)).filter((n) => ['Synthetic Series', 'Plain Folder'].includes(n.displayName)).length,
    { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(2);

  let headers = await csrf(request);
  const username = `favgroups${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const created = await request.post('/api/v1/admin/users', { headers, data: { username, password: START_PASSWORD, isAdmin: false } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const userId = (await created.json()).user.id as string;
  for (const lib of [seriesId, railId]) {
    const grant = await request.put(`/api/v1/admin/users/${userId}/grants/${lib}`, { headers });
    expect(grant.ok(), await grant.text()).toBeTruthy();
  }
  await request.post('/api/v1/auth/logout', { headers });

  expect((await request.post('/api/v1/auth/login', { data: { username, password: START_PASSWORD } })).ok()).toBeTruthy();
  headers = await csrf(request);
  const changed = await request.post('/api/v1/auth/change-password', { headers, data: { currentPassword: START_PASSWORD, newPassword: PASSWORD } });
  expect(changed.ok(), await changed.text()).toBeTruthy();
  expect((await request.post('/api/v1/auth/login', { data: { username, password: PASSWORD } })).ok()).toBeTruthy();
  headers = await csrf(request);
  const rail = (await browse(request, railId)).filter((n) => /Ravelin \d/.test(n.displayName));
  const folders = (await browse(request, seriesId)).filter((n) => ['Synthetic Series', 'Plain Folder'].includes(n.displayName));
  for (const n of [...rail, ...folders]) {
    expect((await request.post(`/api/v1/nodes/${n.id}/favorite`, { headers })).ok()).toBeTruthy();
  }
  const moss = rail.find((n) => n.displayName.startsWith('Moss Ravelin 2'))!;
  expect((await request.put(`/api/v1/reading/${moss.id}/read`, { headers })).ok()).toBeTruthy();
  await request.post('/api/v1/auth/logout', { headers: await csrf(request) });
  reader = { username };
  return reader;
}

/** A new browser context = a new device, signed in as the reader, with these Favorites values stored before the first visit. */
async function device(browser: Browser, username: string, width: number, height: number, stored: Record<string, string> = {}): Promise<Page> {
  const context = await browser.newContext({ baseURL: test.info().project.use.baseURL, viewport: { width, height } });
  const page = await context.newPage();
  const res = await page.request.post('/api/v1/auth/login', { data: { username, password: PASSWORD } });
  expect(res.ok(), await res.text()).toBeTruthy();
  if (Object.keys(stored).length > 0) {
    await page.goto('/');
    await page.evaluate((values) => Object.entries(values).forEach(([k, v]) => localStorage.setItem(k, v)), stored);
  }
  return page;
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

const titles = (page: Page) =>
  page.locator('[data-testid="favorites-list"] .node-title, [data-testid="favorites-list"] .fav-title').allTextContents();
const chips = async (page: Page) => (await page.locator('nav.jump-rail .jump-chip').allTextContents()).map((t) => t.trim());
const headers = async (page: Page) =>
  (await page.getByTestId('favorites-group-header').allTextContents()).map((t) => t.replace(/\s+/g, ' ').trim());
const stored = (page: Page, key: string) => page.evaluate((k) => localStorage.getItem(k), key);

/** Scrolls the list's scroller (the shell's content pane, else the window) to its top or bottom. */
async function scrollList(page: Page, to: 'top' | 'bottom'): Promise<void> {
  await page.evaluate((where) => {
    let el: HTMLElement | null = document.querySelector('[data-testid="favorites-list"]');
    while (el && el !== document.body) {
      const oy = getComputedStyle(el).overflowY;
      if ((oy === 'auto' || oy === 'scroll') && el.scrollHeight > el.clientHeight) break;
      el = el.parentElement;
    }
    const target = el && el !== document.body ? el : (document.scrollingElement as HTMLElement);
    target.scrollTop = where === 'top' ? 0 : target.scrollHeight;
  }, to);
}

/** The whole list in name order: the two folders, then the 60 archives A .. T. */
const NAME_ORDER = ['Plain Folder', 'Synthetic Series', ...WORDS.flatMap((w) => [1, 2, 3].map((n) => `${w} Ravelin ${n}`))];

test('the jump rail under Name: a loaded chip scrolls in place, a later one reloads there, and the list then pages up to the top', async ({ browser, request }) => {
  test.setTimeout(240_000);
  const { username } = await setUp(request);
  const page = await device(browser, username, 1280, 900, { 'mangapixer-favorites-sort': 'name' });
  try {
    let listRequests = 0;
    page.on('request', (r) => { if (/\/api\/v1\/favorites\?/.test(r.url())) listRequests++; });
    await page.goto('/favorites');
    await expect(page.locator('.fav-card')).toHaveCount(50);
    // Letters from the favorites' own names; the two folders are one chip (fewer than 20), in front as they sort.
    await expect.poll(() => chips(page)).toEqual(['Folders', ...LETTERS]);
    await shot(page, 'favorites-rail-desktop');

    // C (entries 9-11) is loaded: the chip scrolls to it, nothing is asked again.
    const before = listRequests;
    await page.locator('nav.jump-rail').getByRole('button', { name: 'C', exact: true }).click();
    await expect(page.locator('.fav-card', { hasText: 'Cinder Ravelin 1' })).toBeInViewport();
    await expect(page.locator('nav.jump-rail .jump-chip.active')).toHaveText('C', { useInnerText: true });
    expect(listRequests).toBe(before);

    // S (entries 57-59) is past the first page: the list reloads from S's cursor - S at the top, nothing of A loaded yet.
    const landed = page.waitForResponse((r) => /\/api\/v1\/favorites\?.*cursor=/.test(r.url()));
    await page.locator('nav.jump-rail').getByRole('button', { name: 'S', exact: true }).click();
    expect((await landed).ok()).toBeTruthy();
    // (The top sentinel may already have prepended the page above - anchored, so S stays where it was.)
    await expect(page.locator('.fav-card', { hasText: 'Sorrel Ravelin 1' })).toBeInViewport();
    await expect(page.locator('.fav-card', { hasText: 'Acorn Ravelin 1' })).toHaveCount(0);
    await expect(page.getByTestId('favorites-top-sentinel')).toBeAttached();
    await expect(page.locator('nav.jump-rail .jump-chip.active')).toHaveText('S');
    await shot(page, 'favorites-rail-landed-desktop');

    // Scrolling up pages back to the top: every entry exactly once, in name order.
    await expect.poll(async () => {
      await scrollList(page, 'top');
      return (await titles(page)).length;
    }, { timeout: 60_000, intervals: [500, 1000, 2000] }).toBe(62);
    const all = (await titles(page)).map((t) => t.trim());
    expect(all.map((t, i) => t.startsWith(NAME_ORDER[i]))).toEqual(NAME_ORDER.map(() => true));
    await expect(page.getByTestId('favorites-top-sentinel')).toHaveCount(0);
    await expectFitsScreen(page, 'favorites with the rail (desktop)');

    // Z-A: the same chips, reversed.
    await page.getByTestId('favorites-view-button').click();
    await page.getByTestId('favorites-sort-name-desc').click();
    await expect.poll(() => chips(page)).toEqual([...LETTERS].reverse().concat('Folders'));
    // Any other sort has no rail.
    await page.getByTestId('favorites-view-button').click();
    await page.getByTestId('favorites-sort-recentlyAdded').click();
    await expect(page.locator('nav.jump-rail')).toHaveCount(0);
  } finally {
    await page.context().close();
  }
});

test('Recently read: the read archive first, the never-read after it by name', async ({ browser, request }) => {
  test.setTimeout(120_000);
  const { username } = await setUp(request);
  const page = await device(browser, username, 1280, 900);
  try {
    await page.goto('/favorites');
    await expect(page.locator('.fav-card').first()).toBeVisible();
    await page.getByTestId('favorites-view-button').click();
    const answered = page.waitForResponse((r) => /\/api\/v1\/favorites\?.*sort=recentlyRead/.test(r.url()));
    await page.getByTestId('favorites-sort-recentlyRead').click();
    expect((await answered).ok()).toBeTruthy();
    await expect.poll(async () => (await titles(page)).slice(0, 3).map((t) => t.trim()))
      .toEqual([expect.stringMatching(/^Moss Ravelin 2/), 'Plain Folder', 'Synthetic Series']);
    expect(await stored(page, 'mangapixer-favorites-sort')).toBe('recentlyRead');
    await expect(page.locator('nav.jump-rail')).toHaveCount(0);
  } finally {
    await page.context().close();
  }
});

test('Group by library: a header per library in both views, no rail, remembered on this device only', async ({ browser, request }) => {
  test.setTimeout(180_000);
  const { username } = await setUp(request);
  const page = await device(browser, username, 1280, 900, { 'mangapixer-favorites-sort': 'name' });
  try {
    await page.goto('/favorites');
    await expect.poll(() => chips(page)).toEqual(['Folders', ...LETTERS]);
    const toggle = page.getByTestId('favorites-group-button');
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    const grouped = page.waitForResponse((r) => /\/api\/v1\/favorites\?.*group=library/.test(r.url()));
    await toggle.click();
    expect((await grouped).ok()).toBeTruthy();
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await expect.poll(() => stored(page, 'mangapixer-favorites-group')).toBe('library');
    // Decision 8: no rail while grouped. The library picker's order (by name): Favorites Rail, then Series Fixtures.
    await expect(page.locator('nav.jump-rail')).toHaveCount(0);
    await expect.poll(() => headers(page)).toEqual(['Favorites Rail 60']);
    await expect.poll(async () => {
      await scrollList(page, 'bottom');
      return headers(page);
    }, { timeout: 30_000 }).toEqual(['Favorites Rail 60', 'Series Fixtures 2']);
    const list = await titles(page);
    expect(list.slice(-2).map((t) => t.trim())).toEqual(['Plain Folder', 'Synthetic Series']);
    await expectFitsScreen(page, 'favorites grouped (desktop, cards)');
    await shot(page, 'favorites-grouped-desktop');

    // The list view has the same headers.
    await page.getByTestId('favorites-view-button').click();
    await page.getByTestId('favorites-view-list').click();
    await expect(page.locator('[data-testid="favorites-list"] app-node-row').first()).toBeVisible();
    await expect.poll(() => headers(page)).toEqual(['Favorites Rail 60', 'Series Fixtures 2']);
    await expectFitsScreen(page, 'favorites grouped (desktop, list)');

    // One library filtered: no header (one library), and the rail is back under Name.
    await page.getByTestId('favorites-filter-button').click();
    await page.locator('[data-testid^="favorites-library-"]', { hasText: SERIES_LIBRARY }).click();
    await expect.poll(() => titles(page)).toEqual(['Plain Folder', 'Synthetic Series']);
    await expect(page.getByTestId('favorites-group-header')).toHaveCount(0);

    // A fresh visit on this device is still grouped; another device is not.
    await page.goto('/');
    await page.goto('/favorites');
    await expect.poll(() => headers(page)).toEqual(['Favorites Rail 60']);
    const other = await device(browser, username, 1280, 900);
    try {
      await other.goto('/favorites');
      await expect(other.locator('.fav-card').first()).toBeVisible();
      await expect(other.getByTestId('favorites-group-button')).toHaveAttribute('aria-pressed', 'false');
      await expect(other.getByTestId('favorites-group-header')).toHaveCount(0);
    } finally {
      await other.context().close();
    }
  } finally {
    await page.context().close();
  }
});

for (const [width, height] of [[820, 1180], [390, 844], [360, 780]] as const) {
  test(`at ${width} px: the rail (the phone picker below 600 px) lands on a letter, and the grouped list fits the screen`, async ({ browser, request }) => {
    test.setTimeout(180_000);
    const { username } = await setUp(request);
    const page = await device(browser, username, width, height, { 'mangapixer-favorites-sort': 'name' });
    try {
      await page.goto('/favorites');
      await expect(page.locator('.fav-card').first()).toBeVisible();
      const phone = width < 600;
      if (phone) {
        await expect(page.locator('nav.jump-rail')).toBeHidden();
        const trigger = page.locator('.jump-menu-trigger');
        await expect(trigger).toBeVisible();
        await trigger.click();
        await page.locator('.jump-picker-menu').getByRole('button', { name: 'S', exact: true }).click();
      } else {
        await expect.poll(() => chips(page)).toEqual(['Folders', ...LETTERS]);
        await page.locator('nav.jump-rail').getByRole('button', { name: 'S', exact: true }).click();
      }
      await expect(page.locator('.fav-card', { hasText: 'Sorrel Ravelin 1' })).toBeInViewport();
      await expectFitsScreen(page, `favorites with the rail (${width} px)`);
      await shot(page, `favorites-rail-${width}`);

      await page.getByTestId('favorites-group-button').click();
      await expect.poll(() => headers(page)).toEqual(['Favorites Rail 60']);
      await expect(page.locator('.jump-menu-trigger, nav.jump-rail')).toHaveCount(0);
      await expectFitsScreen(page, `favorites grouped (${width} px)`);
      await shot(page, `favorites-grouped-${width}`);
    } finally {
      await page.context().close();
    }
  });
}
