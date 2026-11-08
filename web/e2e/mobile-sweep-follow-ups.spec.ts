import { test, expect, APIRequestContext, Browser, Locator, Page, Route } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * The 1.51.0 mobile sweep's follow-ups (1.52.0). Phones are checked as phones - a touch screen (`hasTouch`, `isMobile`) at 360 / 390 px
 * and held sideways at 844 x 390; a desktop with a mouse keeps the old sizes. Data: the synthetic libraries of
 * `e2e/fixtures/make-series-fixtures.mjs` (`Series Fixtures`, `Volume Stacks`, `Favorites Rail`), throwaway readers, and the Info
 * panel's chips answered in the browser with the fixture's values while the tag index is not built yet. Nothing on the server changes
 * except the throwaway readers, their favorites and the viewer's own view preferences.
 *
 * - m3: the Metadata Manager tab strip scrolls under the finger (no paging chevrons); a tab scrolled into view takes its tap.
 * - m4: the Info sheet's chips and their creator menu are 40 px targets on a touch screen, 32 px with a mouse.
 * - m8: an ungranted library opened by its address says not found / no access - in the same words as an unknown one.
 * - 360 px selection bar: Select, Mark read and More fit the row; More opens the other actions below, with their names.
 * - N2: sideways, a Favorites letter jump that reloads the list lands with the picked row at the top, its titles on screen.
 * - C1: the volume stack's trail links are 40 px tall targets on a touch screen, and the trail's text does not move.
 * - C2: Escape cancels an inline confirm panel and gives focus back to its trigger.
 * - C3: the series page of a folder without its own series link does not ask for a Missing line (no 404 in the console).
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const TOUCH = { hasTouch: true, isMobile: true, deviceScaleFactor: 2 } as const;
const START_PASSWORD = 'SweepFollowStart123!';
const PASSWORD = 'SweepFollowUser123!';
const WORDS = ['Acorn', 'Brook', 'Cinder', 'Dapple', 'Ember', 'Fennel', 'Gale', 'Hollow', 'Ivy', 'Juniper', 'Kelp', 'Lark', 'Moss',
  'Nettle', 'Oriel', 'Pebble', 'Quince', 'Rowan', 'Sorrel', 'Thistle'];

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic libraries available');
test.beforeEach(({ page }) => { page.setDefaultTimeout(20_000); });

interface Node { id: string; displayName: string; kind: string; hasSeriesInfo?: boolean }

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const { token } = await (await request.get('/api/v1/auth/csrf')).json();
  return { 'X-MangaPixer-Csrf': token };
}

async function browseItems(request: APIRequestContext, libraryId: string, parentId?: string, query = ''): Promise<Node[]> {
  const p = parentId ? `&parentId=${parentId}` : '';
  return (await (await request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=100${p}${query}`)).json()).items as Node[];
}

/** A synthetic library by name (registered + scanned by whichever spec runs first), once `ready` says its scan is far enough. */
async function library(request: APIRequestContext, name: string, root: string, ready: (id: string) => Promise<boolean>): Promise<string> {
  const headers = await csrf(request);
  const libs: { id: string; name: string }[] = await (await request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === name);
  if (!lib) {
    const created = await request.post('/api/v1/admin/libraries', { headers, data: { displayName: name, rootPath: root } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  await expect.poll(() => ready(lib!.id), { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(true);
  return lib!.id;
}

const seriesLibrary = (request: APIRequestContext) => library(request, 'Series Fixtures', FIXTURE_ROOT!,
  async (id) => (await browseItems(request, id)).some((n) => n.displayName === 'Synthetic Series' && n.hasSeriesInfo === true));
const volumeLibrary = (request: APIRequestContext) => library(request, 'Volume Stacks', `${FIXTURE_ROOT}-volumes`, async (id) => {
  const folder = (await browseItems(request, id)).find((n) => n.displayName === 'Stacked Saga');
  return !!folder && (await browseItems(request, id, folder.id, '&group=flat')).length === 7;
});
const railLibrary = (request: APIRequestContext) => library(request, 'Favorites Rail', `${FIXTURE_ROOT}-favorites`,
  async (id) => (await browseItems(request, id)).filter((n) => /Ravelin \d/.test(n.displayName)).length === 60);

const folderNamed = async (request: APIRequestContext, libraryId: string, name: string) =>
  (await browseItems(request, libraryId)).find((n) => n.displayName === name)!;

/** A throwaway reader granted `libraries` (admin's request context; signs the admin out of it). */
async function newReader(request: APIRequestContext, libraries: string[]): Promise<string> {
  let headers = await csrf(request);
  const username = `sweepu${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const created = await request.post('/api/v1/admin/users', { headers, data: { username, password: START_PASSWORD, isAdmin: false } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const userId = (await created.json()).user.id as string;
  for (const lib of libraries) {
    const grant = await request.put(`/api/v1/admin/users/${userId}/grants/${lib}`, { headers });
    expect(grant.ok(), await grant.text()).toBeTruthy();
  }
  await request.post('/api/v1/auth/logout', { headers });
  expect((await request.post('/api/v1/auth/login', { data: { username, password: START_PASSWORD } })).ok()).toBeTruthy();
  headers = await csrf(request);
  const changed = await request.post('/api/v1/auth/change-password', { headers, data: { currentPassword: START_PASSWORD, newPassword: PASSWORD } });
  expect(changed.ok(), await changed.text()).toBeTruthy();
  await request.post('/api/v1/auth/logout', { headers: await csrf(request) });
  return username;
}

/** A new device (browser context) signed in as `username`, touch when asked. */
async function device(browser: Browser, username: string, password: string, viewport: { width: number; height: number }, touch: boolean,
  stored: Record<string, string> = {}): Promise<Page> {
  const context = await browser.newContext({ baseURL: test.info().project.use.baseURL, viewport, ...(touch ? TOUCH : {}) });
  const page = await context.newPage();
  page.setDefaultTimeout(20_000);
  const res = await page.request.post('/api/v1/auth/login', { data: { username, password } });
  expect(res.ok(), await res.text()).toBeTruthy();
  if (Object.keys(stored).length > 0) {
    await page.goto('/');
    await page.evaluate((values) => Object.entries(values).forEach(([k, v]) => localStorage.setItem(k, v)), stored);
  }
  return page;
}

async function settle(page: Page): Promise<void> {
  await page.waitForLoadState('networkidle', { timeout: 5000 }).catch(() => { /* a page that keeps polling */ });
  await page.waitForTimeout(400);
}

async function shot(page: Page, name: string): Promise<void> {
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/sweepu-${name}.png` });
}

async function box(locator: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  await locator.scrollIntoViewIfNeeded();
  const b = await locator.boundingBox();
  expect(b, 'element has a box').not.toBeNull();
  return b!;
}

/** The Info panel of the fixture folder with its creator chips (the server's answer once the tag index has them, else the fixture's). */
async function serveInfoChips(page: Page, folderId: string): Promise<void> {
  const chip = (token: string, id: string, display: string, roles = 'None') => ({ id, display, roles, conflict: false, filter: `${token}:${id}` });
  await page.route('**/api/v1/nodes/*/info', async (route: Route) => {
    const res = await route.fetch();
    const body = res.ok() ? await res.json().catch(() => null) : null;
    if (body && Array.isArray(body.buckets) && body.buckets.length) return route.fulfill({ response: res });
    const nodeId = new URL(route.request().url()).pathname.split('/').at(-2)!;
    if (nodeId !== folderId) return route.fulfill({ response: res });
    return route.fulfill({ json: {
      nodeId, mode: 'Work',
      buckets: [{ bucket: 'Creators', more: 0, chips: [chip('creator', 'fv_e2ew', 'Test Writer', 'Writer'), chip('creator', 'fv_e2ea', 'Test Artist', 'Artist')] }],
      work: { title: 'Synthetic Saga', locationCount: 1, locations: [] },
    } });
  });
}

/** Whether a tap at the element's centre reaches it. */
const tapHits = (locator: Locator) => locator.evaluate((el) => {
  const r = el.getBoundingClientRect();
  const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
  return !!hit && (hit === el || el.contains(hit));
});

test.describe('phone, touch', () => {
  test.use({ ...TOUCH, viewport: { width: 360, height: 780 } });

  test('m3: the Metadata Manager tab strip scrolls under the finger; the first tab, scrolled to, takes its tap', async ({ page }) => {
    await login(page);
    await page.goto('/admin/metadata?tab=completion');
    const strip = page.locator('[data-testid="admin-metadata"] .mat-mdc-tab-label-container');
    const completion = page.getByRole('tab', { name: 'Completion' });
    await expect(completion).toHaveAttribute('aria-selected', 'true');
    await settle(page);
    // No paging chevrons on a touch screen, and the selected tab is wholly inside the strip.
    await expect(page.locator('[data-testid="admin-metadata"] .mat-mdc-tab-header-pagination:visible')).toHaveCount(0);
    const s = (await strip.boundingBox())!;
    const c = (await completion.boundingBox())!;
    expect(c.x, 'the selected tab starts inside the strip').toBeGreaterThanOrEqual(s.x - 0.5);
    expect(c.x + c.width, 'the selected tab ends inside the strip').toBeLessThanOrEqual(s.x + s.width + 0.5);
    await shot(page, 'm3-completion');
    // The first tab: scrolled into the strip, a tap reaches it and opens it.
    const settings = page.getByRole('tab', { name: 'Settings' });
    await settings.scrollIntoViewIfNeeded();
    expect(await tapHits(settings), 'a tap on the Settings tab reaches it').toBe(true);
    await settings.tap();
    await expect(settings).toHaveAttribute('aria-selected', 'true');
    await expectFitsScreen(page, 'Metadata Manager, 360 px');
  });

  test('m4: the Info sheet chips and their creator menu are 40 px targets on a touch screen', async ({ page }) => {
    test.setTimeout(240_000);
    await login(page);
    const libraryId = await seriesLibrary(page.request);
    const folder = await folderNamed(page.request, libraryId, 'Synthetic Series');
    await serveInfoChips(page, folder.id);
    await page.goto(`/libraries/${libraryId}/browse/${folder.id}`);
    await page.getByRole('button', { name: 'Info', exact: true }).tap();
    const chip = page.locator('.series-info-bottom-sheet [data-testid="info-chip"]').first();
    await expect(chip).toBeVisible();
    const c = await box(chip);
    expect(Math.round(c.height), 'chip height').toBeGreaterThanOrEqual(40);
    const menu = await box(page.locator('.series-info-bottom-sheet [data-testid="info-chip-menu"]').first());
    expect(Math.round(menu.width), 'creator menu width').toBeGreaterThanOrEqual(40);
    expect(Math.round(menu.height), 'creator menu height').toBeGreaterThanOrEqual(40);
    expect(await tapHits(chip)).toBe(true);
    await expectFitsScreen(page, 'Info sheet, touch');
    await shot(page, 'm4-info-sheet');
  });

  test('360 px selection bar: Select, Mark read and More fit the row; More opens the rest with names', async ({ page }) => {
    test.setTimeout(240_000);
    await login(page);
    const libraryId = await volumeLibrary(page.request);
    await page.goto(`/libraries/${libraryId}/browse`);
    await page.locator('button.select-toggle').tap();
    await page.locator('.node-wrap .node-card').first().tap();
    await expect(page.getByTestId('selection-count')).toHaveText('1 selected');
    await settle(page);
    const bar = page.locator('.browse-bar.selecting');
    expect((await bar.boundingBox())!.height, 'one short row').toBeLessThanOrEqual(80);
    const actions = bar.locator('.actions');
    const a = (await actions.boundingBox())!;
    for (const id of ['selection-select-menu', 'selection-mark-read', 'selection-more']) {
      const b = (await page.getByTestId(id).boundingBox())!;
      expect(b, `${id} is drawn`).not.toBeNull();
      expect(b.x, `${id} starts inside the row`).toBeGreaterThanOrEqual(a.x - 0.5);
      expect(b.x + b.width, `${id} ends inside the row (not behind a sideways scroll)`).toBeLessThanOrEqual(a.x + a.width + 0.5);
      expect(Math.round(b.width), `${id}: width`).toBeGreaterThanOrEqual(40);
    }
    const done = (await page.getByTestId('selection-done').boundingBox())!;
    expect(done.x + done.width).toBeLessThanOrEqual(360);
    await expect(page.getByTestId('selection-mark-unread')).toBeHidden();
    await shot(page, 'select-360-closed');

    await page.getByTestId('selection-more').tap();
    await expect(page.getByTestId('selection-more')).toHaveAttribute('aria-expanded', 'true');
    for (const id of ['selection-mark-unread', 'selection-favorites', 'series-selection-menu']) {
      await expect(page.getByTestId(id), `${id} behind More`).toBeVisible();
      await expect(page.getByTestId(id)).toHaveAccessibleName(/\S/);
    }
    await expect(page.getByTestId('selection-mark-unread')).toContainText('Mark unread');
    await expectFitsScreen(page, '360 px selection bar, More open');
    await shot(page, 'select-360-more');
  });

  test('C1: the volume stack trail links are 40 px tall targets on a touch screen; the trail does not move', async ({ page, browser }) => {
    test.setTimeout(240_000);
    await login(page);
    const libraryId = await volumeLibrary(page.request);
    const folder = await folderNamed(page.request, libraryId, 'Stacked Saga');
    const url = `/libraries/${libraryId}/browse/${folder.id}/volume/1`;
    await page.goto(url);
    const link = page.getByTestId('stack-folder-link');
    await expect(link).toBeVisible();
    const touch = await box(link);
    expect(Math.round(touch.height), 'folder link height on touch').toBeGreaterThanOrEqual(40);
    const trailTouch = (await page.locator('.crumbs').first().boundingBox())!;
    const textTouch = await link.evaluate((el) => { const r = el.getClientRects()[0]; const range = document.createRange(); range.selectNodeContents(el); const t = range.getBoundingClientRect(); return { top: t.top - el.closest('.crumbs')!.getBoundingClientRect().top, h: t.height, r: r.height }; });

    // The same page with a mouse: the same trail box and text position.
    const context = await browser.newContext({ baseURL: test.info().project.use.baseURL, viewport: { width: 360, height: 780 } });
    try {
      const mouse = await context.newPage();
      expect((await mouse.request.post('/api/v1/auth/login', { data: { username: ADMIN_USER, password: ADMIN_PASSWORD } })).ok()).toBeTruthy();
      await mouse.goto(url);
      const mouseLink = mouse.getByTestId('stack-folder-link');
      await expect(mouseLink).toBeVisible();
      const trailMouse = (await mouse.locator('.crumbs').first().boundingBox())!;
      const textMouse = await mouseLink.evaluate((el) => { const range = document.createRange(); range.selectNodeContents(el); const t = range.getBoundingClientRect(); return { top: t.top - el.closest('.crumbs')!.getBoundingClientRect().top, h: t.height }; });
      expect(Math.round(trailTouch.height), 'the trail is as tall as with a mouse').toBe(Math.round(trailMouse.height));
      expect(Math.round(textTouch.top), 'the link text sits where it sits with a mouse').toBe(Math.round(textMouse.top));
      expect(Math.round((await mouseLink.boundingBox())!.height), 'a mouse keeps the text-high link').toBeLessThan(40);
    } finally {
      await context.close();
    }
  });
});

test.describe('phone held sideways, touch', () => {
  test.use({ ...TOUCH, viewport: { width: 844, height: 390 } });

  test('N2: a Favorites letter jump that reloads the list lands with the picked row at the top, its titles on screen', async ({ browser, request }) => {
    test.setTimeout(300_000);
    expect((await request.post('/api/v1/auth/login', { data: { username: ADMIN_USER, password: ADMIN_PASSWORD } })).ok()).toBeTruthy();
    const railId = await railLibrary(request);
    const username = await newReader(request, [railId]);
    expect((await request.post('/api/v1/auth/login', { data: { username, password: PASSWORD } })).ok()).toBeTruthy();
    const headers = await csrf(request);
    for (const n of (await browseItems(request, railId)).filter((x) => /Ravelin \d/.test(x.displayName))) {
      expect((await request.post(`/api/v1/nodes/${n.id}/favorite`, { headers })).ok()).toBeTruthy();
    }
    await request.post('/api/v1/auth/logout', { headers: await csrf(request) });

    const page = await device(browser, username, PASSWORD, { width: 844, height: 390 }, true, { 'mangapixer-favorites-sort': 'name' });
    try {
      await page.goto('/favorites');
      await expect(page.locator('.fav-card').first()).toBeVisible();
      await settle(page);
      const landed = page.waitForResponse((r) => /\/api\/v1\/favorites\?.*cursor=/.test(r.url()));
      await page.locator('.jump-menu-trigger').tap();
      await page.locator('.jump-picker-menu').getByRole('button', { name: 'S', exact: true }).tap();
      expect((await landed).ok()).toBeTruthy();
      const card = page.locator('.fav-card', { hasText: `${WORDS[18]} Ravelin 1` });
      await expect(card).toBeInViewport();
      await settle(page);
      // The picked row at the top of the screen and its title wholly on screen (it sat at y 272 of 390, title below the edge).
      const title = card.locator('.node-title, .fav-title').first();
      await expect(title, 'the picked entry\'s title is on screen').toBeInViewport({ ratio: 1 });
      expect((await card.boundingBox())!.y, 'the picked row starts near the top').toBeLessThan(140);
      await shot(page, 'n2-landed');
    } finally {
      await page.context().close();
    }
  });
});

test.describe('access and errors', () => {
  test('m8: an ungranted library opened by its address says not found / no access, in the same words as an unknown one', async ({ browser, request }) => {
    test.setTimeout(240_000);
    expect((await request.post('/api/v1/auth/login', { data: { username: ADMIN_USER, password: ADMIN_PASSWORD } })).ok()).toBeTruthy();
    const seriesId = await seriesLibrary(request);
    const volumesId = await volumeLibrary(request);
    const username = await newReader(request, [volumesId]);

    const page = await device(browser, username, PASSWORD, { width: 390, height: 844 }, true);
    try {
      await page.goto(`/libraries/${seriesId}/browse`);
      const notFound = page.getByTestId('browse-not-found');
      await expect(notFound).toBeVisible();
      await expect(page.getByText('This folder is empty.')).toHaveCount(0);
      const words = (await notFound.innerText()).trim();
      // Nothing of the library shows: not its name.
      await expect(page.getByText('Series Fixtures')).toHaveCount(0);
      await shot(page, 'm8-ungranted');

      await page.goto('/libraries/zz0nosuchlib0/browse');
      await expect(notFound).toBeVisible();
      expect((await notFound.innerText()).trim(), 'the same words for an unknown library').toBe(words);
      // A granted library still browses.
      await page.goto(`/libraries/${volumesId}/browse`);
      await expect(page.locator('.node-wrap').first()).toBeVisible();
      await expect(notFound).toHaveCount(0);
    } finally {
      await page.context().close();
    }
  });

  test('C2: Escape cancels the inline confirm (automatic cleaning) and focus goes back to its switch', async ({ page }) => {
    await login(page);
    await page.goto('/admin');
    const toggle = page.getByTestId('job-trash-auto');
    await expect(toggle).toBeVisible();
    const sw = toggle.getByRole('switch');
    test.skip((await sw.getAttribute('aria-checked')) === 'true', 'automatic cleaning is on in this instance - nothing to ask');
    await sw.click();
    const confirm = page.getByTestId('job-trash-confirm');
    await expect(confirm).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(confirm).toHaveCount(0);
    await expect(sw).toHaveAttribute('aria-checked', 'false');
    await expect(sw).toBeFocused();
  });

  test('C3: the series page of a folder without its own series link asks for no Missing line', async ({ page }) => {
    test.setTimeout(240_000);
    await login(page);
    const libraryId = await seriesLibrary(page.request);
    const folder = await folderNamed(page.request, libraryId, 'Synthetic Series');
    const asked: string[] = [];
    page.on('request', (r) => { if (/\/api\/v1\/nodes\/[^/]+\/missing/.test(r.url())) asked.push(r.url()); });
    const info = page.waitForResponse((r) => /\/api\/v1\/nodes\/[^/]+\/series-info/.test(r.url()));
    await page.goto(`/series/${folder.id}`);
    expect((await info).ok()).toBeTruthy();
    await settle(page);
    await expect(page.getByTestId('series-page').locator('header.hero')).toBeVisible();
    expect(asked, 'no request for a Missing line the server can only answer 404').toEqual([]);
  });
});
