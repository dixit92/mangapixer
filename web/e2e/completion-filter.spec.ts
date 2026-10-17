import { test, expect, Page, APIRequestContext, Route } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * The browse filter by series completion (1.44.0, lane C) on the owner's path: open a library -> Filter -> Completion -> pick an answer
 * -> only the series with that answer remain; Any brings the rest back. The synthetic volume library (`<E2E_SERIES_FIXTURE_ROOT>-volumes`)
 * has no linked series offline (no provider is reachable), so the browse answers are the REAL server's, with the linked-series count and
 * the server's filtering stood in by a route (`linkedSeries`, `completionCounts`, and only `Stacked Saga` for "Have it all"). Checks the
 * request carries `completion`, the menu's counts, the button label, the phone and tablet fit with the menu open, and that the new
 * items are the top element where they are drawn.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const VOLUMES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-volumes` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Volume Stacks';
const COUNTS = { haveItAll: 1, finishedMissing: 0, upToDate: 0, missingSome: 1, cantTell: 0 };

test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic volume library available');
test.describe.configure({ mode: 'serial' });
test.beforeEach(({ page }) => { page.setDefaultTimeout(20_000); });

interface Node { id: string; displayName: string; kind: string }

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

/** The volume library (registered by whichever spec runs first), scanned: its id. */
async function ensureLibrary(page: Page): Promise<string> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: VOLUMES_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    return (root.items as Node[]).filter((n) => n.displayName === 'Stacked Saga' || n.displayName === 'Doubled Saga').length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(2);
  return lib!.id;
}

/**
 * Stands in for linked series at the library root: the real browse answer, plus the level's linked-series count, and - when the request
 * carries `completion` - the server's filtering (only `Stacked Saga` has "Have it all"; nothing else) with the counts. Records the
 * `completion` values the page sent.
 */
async function standInLinkedSeries(page: Page, libraryId: string, sent: (string | null)[]): Promise<void> {
  await page.route(new RegExp(`/api/v1/libraries/${libraryId}/browse\\?`), async (route: Route) => {
    const url = new URL(route.request().url());
    if (url.searchParams.has('parentId')) return route.continue();
    const completion = url.searchParams.get('completion');
    sent.push(completion);
    const real = await route.fetch();
    const body = await real.json();
    const folders = (body.items as Node[]).filter((n) => n.kind === 'Folder').length;
    if (completion) {
      const items = completion === 'HaveItAll' ? (body.items as Node[]).filter((n) => n.displayName === 'Stacked Saga') : [];
      Object.assign(body, { items, totalCount: items.length, nextCursor: null, hasMore: false, completionCounts: COUNTS });
    }
    body.linkedSeries = folders;
    await route.fulfill({ response: real, json: body });
  });
}

async function openFilter(page: Page): Promise<void> {
  await page.locator('.filter-toggle').click();
  await expect(page.getByTestId('completion-any')).toBeVisible();
}

/** The new item is what a tap at its centre hits (not covered by the menu's scroller or anything else). */
async function expectOnTop(page: Page, testId: string): Promise<void> {
  // Polled on the settled menu: Material ignores pointer events while the panel still animates in (1.41.0 lesson).
  await expect.poll(() => page.getByTestId(testId).evaluate((el) => {
    const r = el.getBoundingClientRect();
    const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!top && (top === el || el.contains(top));
  }), { message: `${testId} is the top element at its centre`, timeout: 5000 }).toBe(true);
}

test('library -> Filter -> Completion -> only the series with that answer; Any brings the rest back', async ({ page }) => {
  await login(page);
  const libraryId = await ensureLibrary(page);
  const sent: (string | null)[] = [];
  await standInLinkedSeries(page, libraryId, sent);

  await page.goto(`/libraries/${libraryId}/browse`);
  await expect(page.getByText('Doubled Saga').first()).toBeVisible();
  await expect(page.getByText('Stacked Saga').first()).toBeVisible();
  expect(sent[0]).toBeNull();

  await openFilter(page);
  for (const label of ['Have it all', 'Finished, missing some', 'Everything so far', 'Missing some', 'Can\'t tell'])
    await expect(page.getByRole('menuitemradio', { name: label, exact: true })).toBeVisible();
  await expectOnTop(page, 'completion-HaveItAll');
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/completion-menu-1280.png` });

  const filtered = page.waitForResponse((r) => r.url().includes('completion=HaveItAll'));
  await page.getByTestId('completion-HaveItAll').click();
  await filtered;
  await page.keyboard.press('Escape');
  await expect(page.getByText('Stacked Saga').first()).toBeVisible();
  await expect(page.getByText('Doubled Saga')).toHaveCount(0);
  await expect(page.locator('.filter-toggle')).toContainText('Have it all');
  expect(sent.at(-1)).toBe('HaveItAll');

  // The counts the server returned for the level, in the menu.
  await openFilter(page);
  await expect(page.getByTestId('completion-HaveItAll')).toContainText('Have it all (1)');
  await expect(page.getByTestId('completion-MissingSome')).toContainText('Missing some (1)');
  await expect(page.getByTestId('completion-HaveItAll')).toHaveAttribute('aria-checked', 'true');

  const all = page.waitForResponse((r) => r.url().includes('/browse?') && !r.url().includes('completion='));
  await page.getByTestId('completion-any').click();
  await all;
  await page.keyboard.press('Escape');
  await expect(page.getByText('Doubled Saga').first()).toBeVisible();
  await expect(page.locator('.filter-toggle')).toContainText('Filter');
});

test('the Filter menu with the Completion section fits a phone and a tablet', async ({ page }) => {
  await login(page);
  const libraryId = await ensureLibrary(page);
  await standInLinkedSeries(page, libraryId, []);
  for (const size of [{ width: 390, height: 844 }, { width: 820, height: 1180 }]) {
    await page.setViewportSize(size);
    await page.goto(`/libraries/${libraryId}/browse`);
    await expect(page.getByText('Stacked Saga').first()).toBeVisible();
    await openFilter(page);
    await page.getByTestId('completion-CantTell').scrollIntoViewIfNeeded();
    await expectOnTop(page, 'completion-CantTell');
    if (SHOTS) await page.screenshot({ path: `${SHOTS}/completion-menu-${size.width}.png` });
    await expectFitsScreen(page, `Filter menu with Completion (${size.width} px)`);
    await page.keyboard.press('Escape');
  }
});
