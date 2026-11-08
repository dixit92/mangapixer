import { test, expect, Page, APIRequestContext, Route, Locator } from '@playwright/test';

import { routeFakeItem } from './reader-fixture';

/**
 * Back closes an open surface and only that (1.52.0, mobile sweep finding m1). A phone's back gesture and a browser's Back button
 * with a menu, a bottom sheet, a dialog or a select's list open close that surface and stay on the page; the next Back navigates as
 * before. Closing a surface any other way (Escape, a tap outside, a choice) leaves no extra history entry behind, so one Back then
 * leaves the page; a choice that navigates (a menu item, a chip in the Info sheet) needs one Back to return, not two. Nested
 * surfaces (a menu opened from the Info sheet) take one Back each.
 *
 * Every check starts on a page reached by a fresh load after another page, so "a second Back leaves" has somewhere to go. Data: the
 * synthetic series library of `e2e/fixtures/make-series-fixtures.mjs` (`Series Fixtures`, folder `Synthetic Series` with series
 * information); the Info panel's chips are answered in the browser with the fixture's own values when the tag index is not built yet
 * (as `info-panel.spec.ts`). Nothing is changed on the server.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const LIBRARY_NAME = 'Series Fixtures';
const TOUCH = { hasTouch: true, isMobile: true, deviceScaleFactor: 2 } as const;

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

/** The series library (registered + scanned by whichever spec runs first) and its "Synthetic Series" folder. */
async function seriesFolder(page: Page): Promise<{ libraryId: string; folder: Node }> {
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
  return { libraryId: lib!.id, folder: folder! };
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

/** Loads `first`, then `page` (two document loads), so the history has an entry before the page under test. */
async function arrive(page: Page, first: string, url: string): Promise<void> {
  await page.goto(first);
  await page.goto(url);
  await page.waitForLoadState('networkidle', { timeout: 5000 }).catch(() => { /* a page that keeps polling */ });
}

const path = (page: Page): string => { const u = new URL(page.url()); return u.pathname + u.search; };

/** Browser / phone Back; resolves once the history step has been taken. */
async function back(page: Page): Promise<void> {
  await page.goBack({ waitUntil: 'commit' }).catch(() => { /* a same-document step has no response */ });
  await page.waitForTimeout(400);
}

/** Back with `surface` open: the surface closes, the page stays; the next Back leaves for `previous`. */
async function expectBackClosesOnly(page: Page, surface: Locator, previous: RegExp, what: string): Promise<void> {
  const here = path(page);
  await back(page);
  await expect(surface, `${what}: closed by Back`).toHaveCount(0);
  expect(path(page), `${what}: still on the page after Back`).toBe(here);
  await back(page);
  await expect.poll(() => path(page), { message: `${what}: the second Back leaves the page` }).toMatch(previous);
}

const menuPanel = (page: Page) => page.locator('.mat-mdc-menu-panel');
const selectPanel = (page: Page) => page.locator('.mat-mdc-select-panel');
const sheet = (page: Page) => page.locator('mat-bottom-sheet-container');
const dialog = (page: Page) => page.locator('mat-dialog-container');

test.describe('phone, touch', () => {
  test.use({ ...TOUCH, viewport: { width: 390, height: 844 } });

  test('menu: Back closes the account menu and stays; the next Back leaves', async ({ page }) => {
    await login(page);
    await arrive(page, '/settings', '/');
    await page.getByRole('button', { name: /^Account menu/ }).tap();
    await expect(menuPanel(page)).toBeVisible();
    await expectBackClosesOnly(page, menuPanel(page), /^\/settings/, 'account menu');
  });

  test('menu closed by Escape or by a tap outside leaves no extra entry: one Back leaves', async ({ page }) => {
    await login(page);
    await arrive(page, '/settings', '/');
    const trigger = page.getByRole('button', { name: /^Account menu/ });
    await trigger.tap();
    await expect(menuPanel(page)).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(menuPanel(page)).toHaveCount(0);
    await trigger.tap();
    await expect(menuPanel(page)).toBeVisible();
    await page.locator('.cdk-overlay-backdrop').last().click({ position: { x: 5, y: 600 } });
    await expect(menuPanel(page)).toHaveCount(0);
    await page.waitForTimeout(300);
    await back(page);
    await expect.poll(() => path(page), { message: 'one Back leaves after the menu was closed twice' }).toMatch(/^\/settings/);
  });

  test('menu item that navigates: one Back returns to the page, not two', async ({ page }) => {
    await login(page);
    await arrive(page, '/favorites', '/');
    await page.getByRole('button', { name: /^Account menu/ }).tap();
    await page.getByRole('menuitem', { name: 'Settings' }).tap();
    await expect(page).toHaveURL(/\/settings$/);
    await page.waitForTimeout(300);
    await back(page);
    await expect.poll(() => path(page), { message: 'Back from the page the menu opened' }).toBe('/');
    await back(page);
    await expect.poll(() => path(page)).toMatch(/^\/favorites/);
  });

  test('select list: Back closes the list and stays; a choice leaves no extra entry', async ({ page }) => {
    await login(page);
    await arrive(page, '/favorites', '/settings');
    const select = page.getByRole('combobox', { name: 'Stacks per library' });
    await select.tap();
    await expect(selectPanel(page)).toBeVisible();
    await expectBackClosesOnly(page, selectPanel(page), /^\/favorites/, 'select list');

    await arrive(page, '/favorites', '/settings');
    await select.tap();
    await expect(selectPanel(page)).toBeVisible();
    await page.keyboard.press('Escape'); // closed without a choice (a choice would save a preference)
    await expect(selectPanel(page)).toHaveCount(0);
    await page.waitForTimeout(300);
    await back(page);
    await expect.poll(() => path(page)).toMatch(/^\/favorites/);
  });

  test('Info bottom sheet and a menu inside it: one Back each, then the page; a chip that navigates needs one Back', async ({ page }) => {
    test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic series library available');
    test.setTimeout(240_000);
    await login(page);
    const { libraryId, folder } = await seriesFolder(page);
    await serveInfoChips(page, folder.id);
    const browse = `/libraries/${libraryId}/browse/${folder.id}`;
    await arrive(page, '/settings', browse);
    await page.getByRole('button', { name: 'Info', exact: true }).tap();
    await expect(sheet(page)).toBeVisible();
    const creatorMenu = sheet(page).getByTestId('info-chip-menu').first();
    await expect(creatorMenu).toBeVisible();
    await creatorMenu.tap();
    await expect(menuPanel(page)).toBeVisible();

    await back(page);
    await expect(menuPanel(page), 'the inner menu closes first').toHaveCount(0);
    await expect(sheet(page), 'the sheet stays open').toBeVisible();
    expect(path(page)).toBe(browse);
    await expectBackClosesOnly(page, sheet(page), /^\/settings/, 'Info sheet');

    // A chip that navigates closes the sheet: Back returns to the folder in one step.
    await arrive(page, '/settings', browse);
    await page.getByRole('button', { name: 'Info', exact: true }).tap();
    await sheet(page).getByTestId('info-chip').filter({ hasText: 'Test Writer' }).tap();
    await expect(page).toHaveURL(/\/search\?f=creator(:|%3A)/);
    await expect(sheet(page)).toHaveCount(0);
    await page.waitForTimeout(300);
    await back(page);
    await expect.poll(() => path(page), { message: 'one Back from the search returns to the folder' }).toBe(browse);
    await expect(sheet(page)).toHaveCount(0);
  });
});

test.describe('desktop, mouse', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test('dialog: Back closes the Info side sheet (a dialog) and stays; the next Back leaves', async ({ page }) => {
    test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic series library available');
    test.setTimeout(240_000);
    await login(page);
    const { libraryId, folder } = await seriesFolder(page);
    const browse = `/libraries/${libraryId}/browse/${folder.id}`;
    await arrive(page, '/settings', browse);
    await page.getByRole('button', { name: 'Info', exact: true }).click();
    await expect(dialog(page)).toBeVisible();
    await expectBackClosesOnly(page, dialog(page), /^\/settings/, 'Info dialog');
  });

  test('reader: a menu closed with Escape leaves no extra step - Back with the help open leaves the reader, the menu Back stays', async ({ page }) => {
    await login(page);
    await routeFakeItem(page, { id: 'e2eback1', pageCount: 4, pageWidth: 300, pageHeight: 420, readerMode: 'PagedLtr' });
    await arrive(page, '/settings', '/reader/e2eback1');
    const display = page.getByRole('button', { name: 'Display', exact: true });
    // Back with the Display menu open closes it and stays in the reader.
    await display.click();
    await expect(menuPanel(page)).toBeVisible();
    await back(page);
    await expect(menuPanel(page)).toHaveCount(0);
    expect(path(page)).toBe('/reader/e2eback1');
    // Closed another way (Escape): no extra step - with the help overlay open (not a menu), Back leaves the reader as before.
    await display.click();
    await expect(menuPanel(page)).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(menuPanel(page)).toHaveCount(0);
    await page.getByRole('button', { name: 'Reading help' }).click();
    await expect(page.locator('.help-overlay')).toBeVisible();
    await back(page);
    await expect.poll(() => path(page), { message: 'Back from the reader with its help open' }).toMatch(/^\/settings/);
  });

  test('menu: Back in a desktop browser closes the menu too', async ({ page }) => {
    await login(page);
    await arrive(page, '/settings', '/');
    await page.getByRole('button', { name: /^Account menu/ }).click();
    await expect(menuPanel(page)).toBeVisible();
    await expectBackClosesOnly(page, menuPanel(page), /^\/settings/, 'account menu (desktop)');
  });
});
