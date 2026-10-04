import { test, expect, Page, APIRequestContext, Route } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * "Collection about" (1.34.0): a folder of works about one series (fan works) - NEVER the real network:
 * - Metadata Manager > Review: a waiting folder that looks like a collection shows "Looks like a collection about <Series>" and
 *   offers "Accept as collection" first; it is sent only when the Undo window closes; the Collections tab lists marked folders;
 *   desktop and phone widths fit the screen;
 * - browse: the selection bar's Series menu offers "Collection about..." for one folder - the identify dialog's "pick the series"
 *   mode, with the Content box, sends one PUT to the collection route (identify answers fulfilled IN THE BROWSER with synthetic,
 *   contract-shaped data).
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
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

/** Records every browser request to a host other than MangaPixer's. */
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

const PNG_1PX = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');

const cand = (rank: number, title: string, score: number, format: string) => ({
  rank, provider: 'mangaupdates', externalId: String(5000 + rank), title, providerType: format === 'Doujinshi' ? 'Doujinshi' : 'Manga',
  format, year: 2010 + rank, volumes: 12, titleScore: score, adjustedScore: score, reasons: ['review_only'], imageToken: `ctok-${rank}`,
});

// A waiting doujin folder named like a series (the suggestion), and a plain waiting series folder.
const WAITING = [
  { nodeId: 'cw1', nodeKind: 'Folder', displayName: 'Starlight Academy', libraryId: 'lib-x', libraryName: 'Sample Library',
    trail: ['Doujin', 'Parody'], workClass: 'Ambiguous', matchLevel: 'ReviewOnly', itemCount: 34, openFlagCount: 0, reasons: ['review_only'],
    candidates: [cand(1, 'Starlight Academy dj - Summer Lesson', 0.7, 'Doujinshi'), cand(2, 'Starlight Academy', 1, 'Comic')],
    collection: { rank: 2, provider: 'mangaupdates', externalId: '5002', title: 'Starlight Academy' } },
  { nodeId: 'cw2', nodeKind: 'Folder', displayName: 'Example Chronicle', libraryId: 'lib-x', libraryName: 'Sample Library',
    trail: ['Manga'], workClass: 'Series', matchLevel: 'Folder', itemCount: 11, openFlagCount: 0, reasons: ['close_second'],
    candidates: [cand(1, 'Example Chronicle', 0.88, 'Comic')] },
];
const MARKED = [
  { nodeId: 'cm1', nodeKind: 'Folder', displayName: 'Moonlit Fan Works', libraryId: 'lib-x', libraryName: 'Sample Library',
    trail: ['Doujin'], itemCount: 12, openFlagCount: 0, reasons: [], candidates: [],
    link: { state: 'CollectionAbout', title: 'Moonlit Academy', updatedAt: '2026-10-04T10:00:00Z' } },
];
const SUMMARY = { needsReview: 2, later: 0, autoLinked: 0, unmatched: 0, openFlags: 0, dontMatch: 0, confirmed: 0, missingFolders: 0,
  pending: 0, recheckPending: 0, collections: 1 };

interface Seen { accepts: { url: string; body: unknown }[]; clears: string[] }

async function mockReview(page: Page): Promise<Seen> {
  const seen: Seen = { accepts: [], clears: [] };
  await page.route('**/api/v1/admin/metadata/review/summary**', (r) => json(r, SUMMARY));
  await page.route(/\/api\/v1\/admin\/metadata\/review\?/, (r) => {
    const tab = new URL(r.request().url()).searchParams.get('tab') ?? 'NeedsReview';
    const items = tab === 'NeedsReview' ? WAITING.filter((i) => !seen.accepts.some((a) => a.url.includes(`/${i.nodeId}/`)))
      : tab === 'Collections' ? MARKED : [];
    return json(r, { tab, items, total: items.length });
  });
  await page.route('**/api/v1/admin/metadata/review/authors**', (r) => json(r, { items: [] }));
  await page.route(/\/api\/v1\/admin\/metadata\/review\/[^/]+\/accept-collection$/, (r) => {
    seen.accepts.push({ url: r.request().url(), body: r.request().postDataJSON() });
    return json(r, { change: { nodeId: 'cw1', link: { nodeId: 'cw1', state: 'CollectionAbout', updatedAt: 'x' } }, contentSet: true, queued: 34 });
  });
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/collection$/, (r) => {
    if (r.request().method() === 'DELETE') seen.clears.push(r.request().url());
    return json(r, { nodeId: 'cm1' });
  });
  await page.route('**/api/v1/admin/metadata/candidates/*/image', (r) => r.fulfill({ status: 200, contentType: 'image/png', body: PNG_1PX }));
  await page.route(/\/api\/v1\/admin\/metadata\/flags(\?|$)/, (r) => json(r, { items: [], total: 0 }));
  await page.route(/\/api\/v1\/admin\/metadata\/runs(\?|$)/, (r) => json(r, { status: { enabled: false, active: false, pending: 0 }, items: [] }));
  return seen;
}

test('review: "Looks like a collection about" a series, Accept as collection after the Undo window, the Collections tab', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  const seen = await mockReview(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/admin/metadata?tab=review');
  const rows = page.getByTestId('review-row');
  await expect(rows).toHaveCount(2);
  const first = rows.first();
  await expect(first.getByTestId('review-collection-hint')).toContainText('Looks like a collection about Starlight Academy');
  await expect(first.locator('.actions button').first()).toHaveAttribute('data-testid', 'review-acceptCollection');
  await expect(rows.nth(1).getByTestId('review-collection-hint')).toHaveCount(0);
  await expect(rows.nth(1).locator('.actions button').first()).toHaveAttribute('data-testid', 'review-accept');
  await expectFitsScreen(page, 'review row with a collection suggestion (desktop)');
  await shot(page, 'f-01-review-collection-desktop', true);

  await first.getByTestId('review-acceptCollection').click();
  await expect(rows).toHaveCount(1);
  expect(seen.accepts).toEqual([]); // nothing sent inside the Undo window
  await expect.poll(() => seen.accepts.length, { timeout: 12_000 }).toBe(1);
  expect(seen.accepts[0].url).toMatch(/\/review\/cw1\/accept-collection$/);
  expect(seen.accepts[0].body).toEqual({ rank: 2 }); // the suggested series, not the doujinshi record at rank 1

  await page.getByTestId('review-tab-Collections').click();
  await expect(rows).toHaveCount(1);
  await expect(rows.first().getByTestId('review-link')).toContainText('Collection about Moonlit Academy');
  await expectFitsScreen(page, 'Collections tab (desktop)');
  await shot(page, 'f-02-collections-tab-desktop', true);
  await rows.first().getByTestId('review-clearCollection').click();
  await expect.poll(() => seen.clears.length, { timeout: 12_000 }).toBe(1);
  expect(foreign).toEqual([]);
});

test('phone: the collection suggestion and its bottom-bar action fit the screen', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  await mockReview(page);
  await page.goto('/admin/metadata?tab=review');
  await expect(page.getByTestId('review-row')).toHaveCount(2);
  await page.getByTestId('review-name').first().click();
  const bar = page.getByTestId('review-bottombar');
  await expect(bar.getByTestId('bar-acceptCollection')).toContainText('Collection');
  await expectFitsScreen(page, 'review row with a collection suggestion (phone)');
  await shot(page, 'f-03-review-collection-phone');
  await page.getByTestId('review-tab-Collections').click();
  await expect(page.getByTestId('review-row')).toHaveCount(1);
  await expectFitsScreen(page, 'Collections tab (phone)');
  expect(foreign).toEqual([]);
});

// --- Browse: the Series menu's "Collection about..." (needs the synthetic fixture library) ---

interface Node { id: string; displayName: string; kind: string; hasSeriesInfo?: boolean }

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

async function mockIdentify(page: Page, nodeId: string): Promise<{ puts: unknown[] }> {
  const seen = { puts: [] as unknown[] };
  const local = { displayName: 'Synthetic Anthology', itemCount: 3 };
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/identify$/, (r) => json(r, {
    nodeId, nodeKind: 'Folder', displayName: 'Synthetic Anthology', libraryId: 'lib', provider: 'mangaupdates', providerName: 'MangaUpdates',
    fetchAvailable: true, suggestions: ['Synthetic Anthology'], budgetUsedToday: 0, dailyBudget: 500, local, doujinshiContent: false,
    sites: [{ id: 'mangaupdates', name: 'MangaUpdates', available: true }],
  }));
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/search$/, (r) => json(r, {
    provider: 'mangaupdates', page: 1, totalHits: 1, budgetUsedToday: 1, dailyBudget: 500,
    candidates: [{ externalId: '6001', title: 'Starlight Academy', providerType: 'Manga', format: 'Comic', year: 2012, score: 0.6, strength: 'Weak' }],
  }));
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/preview$/, (r) => json(r, {
    provider: 'mangaupdates', providerName: 'MangaUpdates', externalId: '6001', title: 'Starlight Academy', description: 'A synthetic series.',
    score: 0.6, strength: 'Weak', fetchedAt: '2026-10-04T00:00:00Z', local, warnings: [],
  }));
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/collection$/, (r) => {
    seen.puts.push(r.request().postDataJSON());
    return json(r, { change: { nodeId, link: { nodeId, state: 'CollectionAbout', updatedAt: 'x' } }, contentSet: true, queued: 3 });
  });
  await page.route('**/api/v1/admin/metadata/candidates/*/image', (r) => r.fulfill({ status: 200, contentType: 'image/png', body: PNG_1PX }));
  return seen;
}

for (const viewport of [{ width: 1280, height: 900, name: 'desktop' }, { width: 390, height: 844, name: 'phone' }]) {
  test(`browse (${viewport.name}): the Series menu marks one folder "Collection about" a series through the identify dialog`, async ({ page, baseURL }) => {
    test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
    const foreign = watchForeignRequests(page, baseURL!);
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await login(page);
    const libraryId = await fixtureLibrary(page);
    const root = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=50`)).json();
    const anthology = (root.items as Node[]).find((n) => n.displayName === 'Synthetic Anthology')!;
    const seen = await mockIdentify(page, anthology.id);

    await page.goto(`/libraries/${libraryId}/browse`);
    await page.locator('button.select-toggle').click();
    await page.locator('.node-wrap', { hasText: 'Synthetic Anthology' }).locator('.node-card').click();
    await page.getByTestId('series-selection-menu').click();
    await page.getByTestId('bulk-collection').click();
    await expect(page.getByTestId('identify-collection-title')).toContainText('pick the series');
    await page.getByTestId('identify-search').click();
    await page.getByTestId('identify-results').getByRole('button', { name: 'Preview' }).click();
    await expect(page.getByTestId('identify-collection-content')).toBeVisible();
    await expectFitsScreen(page, `identify dialog, collection mode (${viewport.name})`);
    await shot(page, `f-04-identify-collection-${viewport.name}`);
    await page.getByTestId('identify-set-collection').click();
    await expect.poll(() => seen.puts.length).toBe(1);
    expect(seen.puts[0]).toEqual({ provider: 'mangaupdates', externalId: '6001', matchMethod: 'Search', setDoujinContent: true });
    await expect(page.getByText(/Collection about Starlight Academy - 3 works inside will be matched/)).toBeVisible();
    expect(foreign).toEqual([]);
  });
}
