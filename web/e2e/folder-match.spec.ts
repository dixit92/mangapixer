import { test, expect, Page, APIRequestContext, Route } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * "Match folders by name" (1.38.0) from the browse Series menu - NEVER the real network:
 * - select two folders, Series > "Match folders by name...": nothing is asked of the server until a kind is picked; "As artist folders"
 *   previews through the REAL server (stored data only - the synthetic library has no stored names: no match);
 * - "As collections" (answers fulfilled IN THE BROWSER with synthetic, contract-shaped data, a long list): a proposal is ticked, an
 *   ambiguous row needs a pick, "Search the web for the rest" shows the exact search text per folder and sends only ticked rows through
 *   the Identify search; a web result is only offered; Apply marks the local matches in one call and the picked web result through
 *   Collection about;
 * - desktop, tablet and phone widths fit the screen, and nothing covers the dialog's buttons.
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';
const NAMES = ['Synthetic Anthology', 'Synthetic Series'];

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

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${SHOTS}/${name}.png` });
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
    return NAMES.every((n) => (root.items as Node[]).some((x) => x.displayName === n));
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(true);
  return lib!.id;
}

/** The buttons of the open dialog are the topmost element at their centre (no snackbar or overlay over them). */
async function expectButtonsUncovered(page: Page, ids: string[]): Promise<void> {
  for (const id of ids) {
    const covered = await page.getByTestId(id).evaluate((el) => {
      const box = el.getBoundingClientRect();
      const top = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
      return !(top && (top === el || el.contains(top)));
    });
    expect(covered, `${id} is covered`).toBe(false);
  }
}

const record = (externalId: string, title: string, linked = false, matchedTitle = title) => ({
  provider: 'mangaupdates', externalId, title, matchedTitle, year: 2005, providerType: 'Manga', linkedAsSeries: linked,
});

function collectionsPreview(nodes: Node[]) {
  const [anthology, series] = nodes;
  const extra = Array.from({ length: 10 }, (_, i) => ({
    nodeId: `syn-${i}`, displayName: `Synthetic Parody Collection Number ${i + 1} With A Rather Long Folder Name`,
    status: i % 3 === 0 ? 'NoMatch' : 'Proposed',
    records: i % 3 === 0 ? [] : [record(String(9100 + i), `Synthetic Source Title ${i + 1}`)],
    searchText: i % 3 === 0 ? `Synthetic Parody Collection Number ${i + 1}` : null,
  }));
  return {
    kind: 'Collections',
    compared: 42,
    rows: [
      { nodeId: anthology.id, displayName: anthology.displayName, status: 'Ambiguous',
        records: [record('9001', 'Synthetic Anthology', true), record('9002', 'Synthetic Anthology (Remake)', false, 'Synthetic Anthology')] },
      { nodeId: series.id, displayName: series.displayName, status: 'NoMatch', records: [], searchText: 'Synthetic Series' },
      ...extra,
    ],
  };
}

for (const viewport of [
  { width: 1280, height: 900, name: 'desktop' }, { width: 820, height: 1180, name: 'tablet' }, { width: 390, height: 844, name: 'phone' },
]) {
  test(`browse (${viewport.name}): Match folders by name - preview with tick boxes, opt-in web search, apply`, async ({ page, baseURL }) => {
    test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
    const foreign = watchForeignRequests(page, baseURL!);
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await login(page);
    const libraryId = await fixtureLibrary(page);
    const root = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=50`)).json();
    const nodes = NAMES.map((n) => (root.items as Node[]).find((x) => x.displayName === n)!);

    const previews: { kind: string; nodeIds: string[] }[] = [];
    const applies: unknown[] = [];
    const searches: { url: string; body: { query: string; hideDoujinshiAndNovels: boolean; provider?: string } }[] = [];
    const collections: { url: string; body: unknown }[] = [];
    // Artists: the REAL server answers (stored names only). Collections: answered here.
    await page.route('**/api/v1/admin/metadata/folder-match/preview', async (r) => {
      const body = r.request().postDataJSON() as { kind: string; nodeIds: string[] };
      previews.push(body);
      if (body.kind === 'Artists') return r.continue();
      return json(r, collectionsPreview(nodes));
    });
    await page.route('**/api/v1/admin/metadata/folder-match/apply', (r) => {
      const body = r.request().postDataJSON() as { kind: string; items: { nodeId: string }[] };
      applies.push(body);
      return json(r, { kind: body.kind, succeeded: body.items.length, failed: 0,
        results: body.items.map((i) => ({ nodeId: i.nodeId, code: 'ok', queued: 2 })) });
    });
    await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/search$/, (r) => {
      searches.push({ url: r.request().url(), body: r.request().postDataJSON() });
      return json(r, { provider: 'mangaupdates', page: 1, totalHits: 1, budgetUsedToday: 1, dailyBudget: 500,
        candidates: [{ externalId: '6001', title: 'Synthetic Series Source', providerType: 'Manga', year: 2012, score: 0.9, strength: 'Strong' }] });
    });
    await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/collection$/, (r) => {
      collections.push({ url: r.request().url(), body: r.request().postDataJSON() });
      const nodeId = decodeURIComponent(/\/nodes\/([^/]+)\//.exec(r.request().url())![1]);
      return json(r, { change: { nodeId, link: { nodeId, state: 'CollectionAbout', updatedAt: 'x' } }, contentSet: true, queued: 1 });
    });

    await page.goto(`/libraries/${libraryId}/browse`);
    await page.locator('button.select-toggle').click();
    for (const name of NAMES) await page.locator('.node-wrap', { hasText: name }).locator('.node-card').click();
    await page.getByTestId('series-selection-menu').click();
    await expect(page.getByTestId('bulk-folder-match')).toContainText('Match folders by name');
    await page.getByTestId('bulk-folder-match').click();

    await expect(page.getByTestId('folder-match-lead')).toContainText('2 folders selected');
    expect(previews).toEqual([]); // nothing is asked before a kind is picked

    // Artists, through the real server: the synthetic library stores no artist names - no match, nothing ticked.
    await page.getByTestId('folder-match-artists').click();
    await expect(page.getByTestId('folder-match-summary')).toContainText('0 matched');
    await expect(page.getByTestId(`folder-match-row-${nodes[0].id}`)).toContainText('No known artist has this name');
    await expect(page.getByTestId('folder-match-apply')).toBeDisabled();

    // Collections (synthetic answers): a long list.
    await page.getByTestId('folder-match-collections').click();
    await expect(page.getByTestId('folder-match-summary')).toContainText('6 matched, 1 with several matches, 5 without a match');
    await expect(page.getByTestId('folder-match-apply')).toContainText('Mark 6 folders');
    await expectFitsScreen(page, `match folders by name, preview (${viewport.name})`);
    await expectButtonsUncovered(page, ['folder-match-apply', 'folder-match-close']);
    await shot(page, `m-01-folder-match-preview-${viewport.name}`);

    // The ambiguous folder: pick the series (keyboard on the closed select).
    const pick = page.getByTestId(`folder-match-pick-${nodes[0].id}`);
    await pick.focus();
    await page.keyboard.press('Enter');
    await page.getByRole('option', { name: /Synthetic Anthology - 2005, Manga, linked here/ }).click();
    await expect(page.getByTestId('folder-match-apply')).toContainText('Mark 7 folders');

    // Opt in to the web search; only the ticked rows are sent, with the text shown.
    await page.getByTestId('folder-match-web').click();
    await expect(page.getByTestId(`folder-match-web-text-${nodes[1].id}`)).toHaveValue('Synthetic Series');
    for (const i of [0, 3, 6, 9]) await page.getByTestId(`folder-match-web-tick-syn-${i}`).locator('input').uncheck();
    await expect(page.getByTestId('folder-match-web-search')).toContainText('Search 1 folder');
    await expectFitsScreen(page, `match folders by name, web search (${viewport.name})`);
    await shot(page, `m-02-folder-match-web-${viewport.name}`);
    await page.getByTestId('folder-match-web-search').click();
    await expect(page.getByTestId('folder-match-web-note')).toContainText('1 search sent');
    expect(searches).toHaveLength(1);
    expect(searches[0].url).toContain(`/nodes/${nodes[1].id}/search`);
    expect(searches[0].body).toMatchObject({ query: 'Synthetic Series', hideDoujinshiAndNovels: true, provider: 'mangaupdates' });
    await expect(page.getByTestId('folder-match-apply')).toContainText('Mark 7 folders'); // a web result is only offered

    const webPick = page.getByTestId(`folder-match-web-pick-${nodes[1].id}`);
    await webPick.focus();
    await page.keyboard.press('Enter');
    await page.getByRole('option', { name: /Synthetic Series Source/ }).click();
    await expect(page.getByTestId('folder-match-apply')).toContainText('Mark 8 folders');

    await page.getByTestId('folder-match-apply').click();
    await expect(page.getByTestId(`folder-match-result-${nodes[1].id}`)).toContainText('Marked');
    expect(applies).toHaveLength(1);
    const applied = applies[0] as { kind: string; items: { nodeId: string; externalId: string }[]; setDoujinContent: boolean };
    expect(applied.kind).toBe('Collections');
    expect(applied.setDoujinContent).toBe(true);
    expect(applied.items).toHaveLength(7);
    expect(applied.items[0]).toEqual({ nodeId: nodes[0].id, provider: 'mangaupdates', externalId: '9001' });
    expect(collections).toHaveLength(1);
    expect(collections[0].url).toContain(`/nodes/${nodes[1].id}/collection`);
    expect(collections[0].body).toEqual({ provider: 'mangaupdates', externalId: '6001', matchMethod: 'Search', setDoujinContent: true });
    await expectFitsScreen(page, `match folders by name, applied (${viewport.name})`);
    await shot(page, `m-03-folder-match-applied-${viewport.name}`);

    await page.getByTestId('folder-match-close').click();
    await expect(page.getByText(/8 collections marked - 15 works inside will be matched/)).toBeVisible();
    expect(foreign).toEqual([]);
  });
}
