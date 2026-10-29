import { test, expect, Page, APIRequestContext } from '@playwright/test';

/**
 * 1.50.0 lane D: under a date sort a time chip lands the list mid-way, and scrolling up from there loads the rows above it (before
 * 1.50.0 only the name sort could page backward, so the list started at the chip and nothing above it ever loaded).
 *
 * The level is `Jump Saga` of the jump-rail fixture (`e2e/fixtures/make-series-fixtures.mjs`, `<E2E_SERIES_FIXTURE_ROOT>-jump`, 65
 * archives). One scan gives every row the same "added" time, so the server's own date rail would be a single chip (none at all); the
 * spec feeds the rail a two-chip answer whose second chip carries browse's REAL `recentlyAdded` cursor (read from the browse API) - the
 * landing and every page above it come from the real server. A small page size makes the landing a reload from the cursor, not a scroll.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const JUMP_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-jump` : undefined;
const LIBRARY_NAME = 'Jump Rail';
const PAGE_SIZE = 10;
const LANDING = 55; // the chip's first row: well past the first page

test.skip(!JUMP_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic jump-rail library available');
test.describe.configure({ mode: 'serial' });
test.beforeEach(({ page }) => { page.setDefaultTimeout(20_000); });

interface Node { id: string; displayName: string }

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

/** The jump-rail library (registered + scanned once, shared with jump-rail.spec.ts); returns [libraryId, folderId]. */
async function ensureLibrary(page: Page): Promise<[string, string]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: JUMP_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folder = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = ((root.items as Node[]).find((n) => n.displayName === 'Jump Saga')?.id) ?? '';
    if (!folder) return 0;
    const inside = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?parentId=${folder}&pageSize=100`)).json();
    return inside.totalCount as number;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(65);
  return [lib!.id, folder];
}

/** Writes the viewer's library preferences over the current ones (the PUT overwrites every field); returns the previous ones. */
async function setPreferences(page: Page, change: Record<string, unknown>): Promise<Record<string, unknown>> {
  const headers = await csrf(page.request);
  const current = await (await page.request.get('/api/v1/reading/library-preferences')).json();
  const res = await page.request.put('/api/v1/reading/library-preferences', { headers, data: { ...current, ...change } });
  expect(res.ok(), await res.text()).toBeTruthy();
  return current;
}

async function titles(page: Page): Promise<string[]> {
  return (await page.locator('.node-wrap .node-title').allTextContents()).map((t) => t.trim());
}

test('desktop: after a date chip lands mid-list, scrolling up loads the rows above it (Recently added)', async ({ page }) => {
  test.setTimeout(180_000); // may wait for the library scan
  await page.setViewportSize({ width: 1280, height: 900 });
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);

  // The listing in browse's recentlyAdded order, and browse's own cursor that continues after row LANDING - 1 (it lands on LANDING).
  const all = await (await page.request.get(
    `/api/v1/libraries/${libraryId}/browse?parentId=${folderId}&sort=recentlyAdded&pageSize=100`)).json();
  const listing = (all.items as Node[]).map((n) => n.displayName);
  expect(listing).toHaveLength(65);
  const head = await (await page.request.get(
    `/api/v1/libraries/${libraryId}/browse?parentId=${folderId}&sort=recentlyAdded&pageSize=${LANDING}`)).json();
  const cursor = head.nextCursor as string;
  expect(cursor).toMatch(/^a:/);
  const landingId = (all.items as Node[])[LANDING].id;

  await page.route(/\/api\/v1\/libraries\/[^/]+\/jump-index/, (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      libraryId, parentId: folderId, direction: 'desc', mode: 'dates',
      buckets: [
        { label: 'Today', count: LANDING, firstCursor: null, firstNodeId: (all.items as Node[])[0].id, range: null },
        { label: '2025', count: 65 - LANDING, firstCursor: cursor, firstNodeId: landingId, range: null },
      ],
    }),
  }));

  const previous = await setPreferences(page, { sort: 'recentlyAdded', direction: 'desc', libraryPageSize: PAGE_SIZE, viewMode: 'card' });
  try {
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    const rail = page.locator('nav.jump-rail');
    await expect(rail).toHaveAttribute('aria-label', 'Jump to date');
    await expect(page.locator('.node-wrap .node-title').first()).toHaveText(listing[0]);
    // The chip's first row is not loaded yet (forward infinite scroll settled), so the jump reloads the list from the chip's cursor.
    let loaded = -1;
    await expect.poll(async () => {
      const count = (await titles(page)).length;
      const settled = count === loaded;
      loaded = count;
      return settled;
    }, { intervals: [1000] }).toBe(true);
    await expect(page.locator('.node-wrap .node-title', { hasText: listing[LANDING] })).toHaveCount(0);

    await rail.getByRole('button', { name: '2025', exact: true }).click();
    await expect(page.locator('.node-wrap .node-title', { hasText: listing[LANDING] })).toHaveCount(1);

    // Scroll up until the window reaches the start of the listing: the rows above the chip load page by page, in order, each once.
    await expect.poll(async () => {
      await page.evaluate(() => {
        window.scrollTo(0, 0);
        for (const el of Array.from(document.querySelectorAll<HTMLElement>('*'))) if (el.scrollTop > 0) el.scrollTop = 0;
      });
      return (await titles(page))[0];
    }, { timeout: 60_000, intervals: [500, 1000] }).toBe(listing[0]);
    await expect(page.locator('.top-sentinel')).toHaveCount(0);
    expect(await titles(page)).toEqual(listing);
  } finally {
    await setPreferences(page, {
      sort: previous['sort'] ?? 'name', direction: previous['direction'] ?? 'asc', libraryPageSize: previous['libraryPageSize'] ?? 0,
      viewMode: previous['viewMode'] ?? 'card',
    });
  }
});
