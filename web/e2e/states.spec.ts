import { test, expect, Page, APIRequestContext, Route } from '@playwright/test';

/**
 * 1.53.0 (lane V, visual audit V2): the first answer of browse, Home and Search is never read as "empty". A slow first answer shows the
 * series page's spinner (after ~300 ms) and no empty line; a failed one shows the reader's error block with a "Try again" that repeats the
 * request; an unknown address goes Home. And (the 1.52.0 C3 residue) a folder with no series information asks for no Missing line.
 * Real data from the synthetic volume library (`e2e/fixtures/make-series-fixtures.mjs`, as phone-layout.spec.ts); only the first request
 * of each page is held or failed, by its exact API path.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const VOLUMES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-volumes` : undefined;
const LIBRARY_NAME = 'Volume Stacks';

test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic volume library available');

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

/** The volume library (registered by whichever spec runs first) and its series folder: [libraryId, folderId]. */
async function ensureLibrary(page: Page): Promise<[string, string]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: VOLUMES_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folder = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = ((root.items as Node[]).find((n) => n.displayName === 'Stacked Saga')?.id) ?? '';
    if (!folder) return 0;
    const inside = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?parentId=${folder}&pageSize=50&group=flat`)).json();
    return (inside.items as Node[]).length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(7);
  return [lib!.id, folder];
}

/** The API paths each page's first request uses (exact, so the sidebar's or quick results' other calls are not caught by accident). */
const isBrowse = (url: URL) => /^\/api\/v1\/libraries\/[^/]+\/browse$/.test(url.pathname);
const isLibraries = (url: URL) => url.pathname === '/api/v1/libraries';
const isSearch = (url: URL) => url.pathname === '/api/v1/search';

/** Holds every matching GET until `release()`; then lets them through. */
async function hold(page: Page, match: (url: URL) => boolean): Promise<{ release: () => void; held: () => number }> {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  let count = 0;
  await page.route(match, async (route: Route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    count++;
    await gate;
    await route.continue().catch(() => { /* the page moved on */ });
  });
  return { release, held: () => count };
}

/** Answers the first `times` matching GETs with a 500, then lets them through; returns how many were failed. */
async function failFirst(page: Page, match: (url: URL) => boolean, times = 1): Promise<() => number> {
  let failed = 0;
  await page.route(match, async (route: Route) => {
    if (route.request().method() !== 'GET' || failed >= times) return route.fallback();
    failed++;
    await route.fulfill({ status: 500, json: { error: 'synthetic_failure' } });
  });
  return () => failed;
}

test.describe('first-answer states (audit V2)', () => {
  test('browse: a slow first page shows a spinner, never "This folder is empty."', async ({ page }) => {
    await login(page);
    const [libraryId] = await ensureLibrary(page);
    const gate = await hold(page, isBrowse);
    await page.goto(`/libraries/${libraryId}/browse`);
    await expect(page.getByTestId('browse-loading')).toBeVisible();
    await expect(page.getByText('This folder is empty.')).toHaveCount(0);
    expect(gate.held()).toBeGreaterThan(0);
    gate.release();
    await expect(page.locator('.node-title', { hasText: 'Stacked Saga' })).toBeVisible();
    await expect(page.getByTestId('browse-loading')).toHaveCount(0);
  });

  test('browse: a failed first page says so, and Try again loads it', async ({ page }) => {
    await login(page);
    const [libraryId] = await ensureLibrary(page);
    const failed = await failFirst(page, isBrowse);
    await page.goto(`/libraries/${libraryId}/browse`);
    const block = page.getByTestId('browse-failed');
    await expect(block).toBeVisible();
    await expect(block).toContainText('Could not load this folder.');
    await expect(page.getByText('This folder is empty.')).toHaveCount(0);
    expect(failed()).toBe(1);
    const again = page.waitForResponse((r) => isBrowse(new URL(r.url())) && r.ok());
    await block.getByRole('button', { name: 'Try again' }).click();
    await again;
    await expect(page.locator('.node-title', { hasText: 'Stacked Saga' })).toBeVisible();
    await expect(block).toHaveCount(0);
  });

  test('Home: a slow libraries answer shows a spinner; a failed one says so, and Try again loads it', async ({ page }) => {
    await login(page);
    await ensureLibrary(page);
    const gate = await hold(page, isLibraries);
    await page.goto('/');
    await expect(page.getByTestId('home-libraries-loading')).toBeVisible();
    await expect(page.getByText(/No libraries yet/)).toHaveCount(0);
    gate.release();
    await expect(page.locator('.library-card', { hasText: LIBRARY_NAME })).toBeVisible();
    await page.unrouteAll({ behavior: 'ignoreErrors' });

    const failed = await failFirst(page, isLibraries, Number.POSITIVE_INFINITY); // Home's and the sidebar's, until unrouted
    await page.goto('/');
    const block = page.getByTestId('home-libraries-failed');
    await expect(block).toBeVisible();
    await expect(block).toContainText('Could not load your libraries.');
    await expect(page.getByText(/No libraries yet/)).toHaveCount(0);
    expect(failed()).toBeGreaterThan(0);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
    await block.getByRole('button', { name: 'Try again' }).click();
    await expect(page.locator('.library-card', { hasText: LIBRARY_NAME })).toBeVisible();
    await expect(block).toHaveCount(0);
  });

  test('Search: a slow first page shows a spinner; a failed one says so (not "No results found."), and Try again searches again', async ({ page }) => {
    await login(page);
    await ensureLibrary(page);
    const gate = await hold(page, isSearch);
    await page.goto('/search?q=Stacked');
    await expect(page.getByTestId('search-loading')).toBeVisible();
    await expect(page.getByText('No results found.')).toHaveCount(0);
    gate.release();
    await expect(page.locator('.result-card', { hasText: 'Stacked Saga' }).first()).toBeVisible();
    await page.unrouteAll({ behavior: 'ignoreErrors' });

    const failed = await failFirst(page, isSearch);
    await page.goto('/search?q=Stacked');
    const block = page.getByTestId('search-failed');
    await expect(block).toBeVisible();
    await expect(block).toContainText('The search did not answer.');
    await expect(page.getByText('No results found.')).toHaveCount(0);
    expect(failed()).toBe(1);
    await block.getByRole('button', { name: 'Try again' }).click();
    await expect(page.locator('.result-card', { hasText: 'Stacked Saga' }).first()).toBeVisible();
    await expect(block).toHaveCount(0);
  });

  test('an unknown address goes Home', async ({ page }) => {
    await login(page);
    await page.goto('/no-such-page');
    await expect(page).toHaveURL(/\/$/);
    await expect(page.locator('app-home')).toBeVisible();
    await page.goto('/libraries/x/browse/y/no/such/page');
    await expect(page).toHaveURL(/\/$/);
  });
});

test('C3 residue: the series page of a folder with no series information at all asks for no Missing line (1.53.0)', async ({ page }) => {
  test.setTimeout(180_000);
  await login(page);
  const [, folderId] = await ensureLibrary(page);
  const asked: string[] = [];
  page.on('request', (r) => { if (/\/api\/v1\/nodes\/[^/]+\/missing/.test(r.url())) asked.push(r.url()); });
  const info = page.waitForResponse((r) => /\/api\/v1\/nodes\/[^/]+\/series-info/.test(r.url()));
  await page.goto(`/series/${folderId}`);
  const answer = await info;
  expect(answer.ok()).toBeTruthy();
  expect((await answer.json()).missingReportPossible).toBe(false);
  await expect(page.getByTestId('series-page')).toBeVisible();
  await page.waitForLoadState('networkidle', { timeout: 5000 }).catch(() => { /* a page that keeps polling */ });
  expect(asked, 'no request for a Missing line the server can only answer 404').toEqual([]);
});
