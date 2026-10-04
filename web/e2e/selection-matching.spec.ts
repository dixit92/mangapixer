import { test, expect, Page, APIRequestContext, Route } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * Library multi-selection matching and the Unmatched groups (1.34.0) - NEVER the real network:
 * - browse: selecting two folders and choosing the Series menu's "Identify one by one" steps through them ("1 of 2" ... Link /
 *   Skip / Stop, then "Linked 1 · skipped 1"); "Re-run matching" says in plain words what was queued and what was refused (identify
 *   and review answers fulfilled IN THE BROWSER with synthetic, contract-shaped data);
 * - Metadata Manager > Review > Unmatched: the "more by" / "more in" chips, the group filter and the Authors list.
 * Desktop and phone widths fit the screen. Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';

test.describe.configure({ mode: 'serial' });

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

async function shot(page: Page, name: string, fullPage = false): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage });
}

function watchForeignRequests(page: Page, baseURL: string): string[] {
  const own = new URL(baseURL).host;
  const foreign: string[] = [];
  page.on('request', (r) => {
    const url = new URL(r.url());
    if ((url.protocol === 'http:' || url.protocol === 'https:') && url.host !== own) foreign.push(url.host);
  });
  return foreign;
}

const json = (route: Route, body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

interface Node { id: string; displayName: string; kind: string }

async function fixtureLibrary(page: Page): Promise<string> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: FIXTURE_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    return (root.items as Node[]).some((n) => n.displayName === 'Synthetic Anthology');
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(true);
  return lib!.id;
}

const NAMES = ['Synthetic Anthology', 'Synthetic Series'];

/** Identify answers per node (the context carries the node's own name, so the step title shows it). */
async function mockIdentify(page: Page, ids: Record<string, string>): Promise<{ links: { nodeId: string; body: unknown }[] }> {
  const seen = { links: [] as { nodeId: string; body: unknown }[] };
  const nodeOf = (url: string) => decodeURIComponent(/\/nodes\/([^/]+)\//.exec(url)![1]);
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/identify$/, (r) => {
    const id = nodeOf(r.request().url());
    const name = ids[id] ?? 'Unknown';
    return json(r, {
      nodeId: id, nodeKind: 'Folder', displayName: name, libraryId: 'lib', provider: 'mangaupdates', providerName: 'MangaUpdates',
      fetchAvailable: true, suggestions: [name], budgetUsedToday: 0, dailyBudget: 500, local: { displayName: name, itemCount: 3 },
      doujinshiContent: false, sites: [{ id: 'mangaupdates', name: 'MangaUpdates', available: true }],
    });
  });
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/search$/, (r) => json(r, {
    provider: 'mangaupdates', page: 1, totalHits: 1, budgetUsedToday: 1, dailyBudget: 500,
    candidates: [{ externalId: '6001', title: 'Synthetic Match', providerType: 'Manga', format: 'Comic', year: 2012, score: 0.9, strength: 'Strong' }],
  }));
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/preview$/, (r) => json(r, {
    provider: 'mangaupdates', providerName: 'MangaUpdates', externalId: '6001', title: 'Synthetic Match', description: 'A synthetic series.',
    score: 0.9, strength: 'Strong', fetchedAt: '2026-10-04T00:00:00Z', local: { displayName: ids[nodeOf(r.request().url())] ?? '', itemCount: 3 }, warnings: [],
  }));
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/link$/, (r) => {
    const nodeId = nodeOf(r.request().url());
    seen.links.push({ nodeId, body: r.request().postDataJSON() });
    return json(r, { nodeId, link: { nodeId, state: 'Confirmed', updatedAt: 'x' }, previous: null });
  });
  return seen;
}

for (const viewport of [{ width: 1280, height: 900, name: 'desktop' }, { width: 390, height: 844, name: 'phone' }]) {
  test(`browse (${viewport.name}): two selected folders are identified one at a time - link one, skip one`, async ({ page, baseURL }) => {
    test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
    const foreign = watchForeignRequests(page, baseURL!);
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await login(page);
    const libraryId = await fixtureLibrary(page);
    const root = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=50`)).json();
    const nodes = NAMES.map((n) => (root.items as Node[]).find((x) => x.displayName === n)!);
    const seen = await mockIdentify(page, Object.fromEntries(nodes.map((n) => [n.id, n.displayName])));

    await page.goto(`/libraries/${libraryId}/browse`);
    await page.locator('button.select-toggle').click();
    for (const name of NAMES) await page.locator('.node-wrap', { hasText: name }).locator('.node-card').click();
    await page.getByTestId('series-selection-menu').click();
    await expect(page.getByTestId('bulk-identify')).toContainText('one by one');
    await page.getByTestId('bulk-identify').click();

    const step = page.getByTestId('identify-step');
    await expect(step).toContainText('1 of 2');
    await page.getByTestId('identify-search').click();
    await page.getByTestId('identify-results').getByRole('button', { name: 'Preview' }).click();
    await expect(page.getByTestId('identify-link')).toBeVisible();
    await expect(page.getByTestId('identify-skip')).toBeVisible();
    await expect(page.getByTestId('identify-stop')).toBeVisible();
    await expectFitsScreen(page, `identify dialog, stepping preview (${viewport.name})`);
    await shot(page, `g-01-identify-step-preview-${viewport.name}`);
    await page.getByTestId('identify-link').click();

    // Step 2 starts clean: its own name, the search step, nothing of the first preview.
    await expect(step).toContainText('2 of 2');
    await expect(page.getByTestId('identify-query')).toBeVisible();
    await expect(page.getByTestId('identify-link')).toHaveCount(0);
    await expectFitsScreen(page, `identify dialog, stepping search (${viewport.name})`);
    await shot(page, `g-02-identify-step-search-${viewport.name}`);
    await page.getByTestId('identify-skip').click();

    await expect(page.getByText('Linked 1 · skipped 1')).toBeVisible();
    await expect(page.getByTestId('identify-step')).toHaveCount(0);
    expect(seen.links).toHaveLength(1);
    expect(seen.links[0].body).toMatchObject({ provider: 'mangaupdates', externalId: '6001', matchMethod: 'Search' });
    expect(foreign).toEqual([]);
  });
}

test('browse: Re-run matching queues the selection and says why an item was not queued', async ({ page, baseURL }) => {
  test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  const libraryId = await fixtureLibrary(page);
  const root = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=50`)).json();
  const nodes = NAMES.map((n) => (root.items as Node[]).find((x) => x.displayName === n)!);
  const bulks: { action: string; nodeIds: string[] }[] = [];
  await page.route('**/api/v1/admin/metadata/review/bulk', (r) => {
    const body = r.request().postDataJSON() as { action: string; nodeIds: string[] };
    bulks.push(body);
    return json(r, {
      action: body.action, succeeded: 1, failed: 1,
      results: [{ nodeId: body.nodeIds[0], code: 'ok' }, { nodeId: body.nodeIds[1], code: 'covered_by_folder' }],
    });
  });

  await page.goto(`/libraries/${libraryId}/browse`);
  await page.locator('button.select-toggle').click();
  for (const name of NAMES) await page.locator('.node-wrap', { hasText: name }).locator('.node-card').click();
  await page.getByTestId('series-selection-menu').click();
  await page.getByTestId('bulk-rerun').click();
  await expect(page.getByText(/1 item queued to match again\. 1 not queued: 1 inside a folder that is linked, marked Don't match or waiting in review/)).toBeVisible();
  expect(bulks).toHaveLength(1);
  expect(bulks[0].action).toBe('RerunMatching');
  expect(bulks[0].nodeIds.sort()).toEqual(nodes.map((n) => n.id).sort());
  expect(foreign).toEqual([]);
});

// --- Metadata Manager > Review > Unmatched: Same folder / Same author ---

const unmatchedRow = (nodeId: string, name: string, author: string, others: number) => ({
  nodeId, nodeKind: 'Archive', displayName: name, libraryId: 'lib-x', libraryName: 'Sample Library', trail: ['Doujin', 'Mixed'],
  itemCount: 1, openFlagCount: 0, reasons: [], candidates: [],
  sameAuthor: { key: 'syntheticcircle', label: author, others },
  sameFolder: { key: 'folder-1', label: 'Mixed Doujins', others },
});
const UNMATCHED = [
  unmatchedRow('um1', '[Synthetic Circle] First Story.cbz', 'Synthetic Circle', 1),
  unmatchedRow('um2', 'Synthetic Circle] Second Story.cbz', 'Synthetic Circle', 1),
];
const SUMMARY = { needsReview: 0, later: 0, autoLinked: 0, unmatched: 2, openFlags: 0, dontMatch: 0, confirmed: 0, missingFolders: 0,
  pending: 0, recheckPending: 0, collections: 0 };

for (const viewport of [{ width: 1280, height: 900, name: 'desktop' }, { width: 390, height: 844, name: 'phone' }]) {
  test(`review (${viewport.name}): Unmatched shows Same author / Same folder chips, the group filter and the Authors list`, async ({ page, baseURL }) => {
    const foreign = watchForeignRequests(page, baseURL!);
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await login(page);
    const requests: URL[] = [];
    await page.route('**/api/v1/admin/metadata/review/summary**', (r) => json(r, SUMMARY));
    await page.route(/\/api\/v1\/admin\/metadata\/review\?/, (r) => {
      const url = new URL(r.request().url());
      requests.push(url);
      const tab = url.searchParams.get('tab') ?? 'NeedsReview';
      const items = tab === 'Unmatched' ? UNMATCHED : [];
      return json(r, { tab, items, total: items.length });
    });
    await page.route('**/api/v1/admin/metadata/review/authors**', (r) => {
      requests.push(new URL(r.request().url()));
      return json(r, { items: [{ key: 'syntheticcircle', label: 'Synthetic Circle', count: 2 }] });
    });
    await page.route(/\/api\/v1\/admin\/metadata\/flags(\?|$)/, (r) => json(r, { items: [], total: 0 }));
    await page.route(/\/api\/v1\/admin\/metadata\/runs(\?|$)/, (r) => json(r, { status: { enabled: false, active: false, pending: 0 }, items: [] }));

    await page.goto('/admin/metadata?tab=review');
    await page.getByTestId('review-tab-Unmatched').click();
    const rows = page.getByTestId('review-row');
    await expect(rows).toHaveCount(2);
    await expect(rows.first().getByTestId('review-same-author')).toContainText('1 more by Synthetic Circle');
    await expect(rows.first().getByTestId('review-same-folder')).toContainText('1 more in Mixed Doujins');
    await expect(page.getByTestId('review-later-filter')).toHaveCount(0);
    await expectFitsScreen(page, `Unmatched with group chips (${viewport.name})`);
    await shot(page, `g-03-unmatched-chips-${viewport.name}`, true);

    await rows.first().getByTestId('review-same-author').click();
    await expect(page.getByTestId('review-group-chip')).toContainText('By Synthetic Circle');
    await expect.poll(() => requests.some((u) => u.searchParams.get('tab') === 'Unmatched' && u.searchParams.get('author') === 'syntheticcircle')).toBe(true);
    await expectFitsScreen(page, `Unmatched filtered by author (${viewport.name})`);
    await page.getByTestId('review-group-clear').click();
    await expect(page.getByTestId('review-group-chip')).toHaveCount(0);

    await page.getByTestId('review-authors').click();
    await expect.poll(() => requests.some((u) => u.pathname.endsWith('/review/authors') && u.searchParams.get('tab') === 'Unmatched')).toBe(true);
    await expect(page.getByText('Synthetic Circle').first()).toBeVisible();
    await shot(page, `g-04-unmatched-authors-${viewport.name}`);
    expect(foreign).toEqual([]);
  });
}
