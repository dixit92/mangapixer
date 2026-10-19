import { test, expect, Page, APIRequestContext, Route } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * Author search by name (1.45.0) on the owner's path - NEVER the real network (the author search and the marks are answered IN THE
 * BROWSER with synthetic, contract-shaped data; the preview is the REAL server's):
 * - browse > select two folders > Series > "Match folders by name..." > "As artist folders": the real server gives each folder without a
 *   match its search text; "Search MangaUpdates for the rest" is off until ticked, then sends only the ticked folders' text to the author
 *   search; the authors are only offered (name, other name, series count, three genres, a no-referrer MangaUpdates link, no images); a
 *   picked author is marked with its MangaUpdates id in the one apply call;
 * - the Artist folder dialog's "Find on MangaUpdates" searches the Artist field once; a pick goes with the mark;
 * - desktop, tablet and phone widths fit the screen.
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

/** Ten synthetic authors (the provider's first page): the first has the folder's name, the second matched by another name. */
function authors(text: string) {
  return {
    total: 14,
    candidates: Array.from({ length: 10 }, (_, i) => ({
      provider: 'mangaupdates',
      externalId: String(48151 + i),
      name: i === 0 ? text : `${text} Lookalike Number ${i} With A Long Name`,
      hitName: i === 1 ? `${text} (pen name)` : null,
      totalSeries: 12 - i,
      genres: ['Drama', 'Romance', 'Comedy', 'Horror'],
      siteUrl: `https://www.mangaupdates.com/author/syn${i}/synthetic`,
    })),
  };
}

interface Seen {
  searches: { url: string; body: { text: string } }[];
  applies: { kind: string; items: Record<string, string>[] }[];
  marks: { url: string; body: Record<string, string> }[];
}

async function mock(page: Page): Promise<Seen> {
  const seen: Seen = { searches: [], applies: [], marks: [] };
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/author-search$/, (r) => {
    const body = r.request().postDataJSON() as { text: string };
    seen.searches.push({ url: r.request().url(), body });
    return json(r, authors(body.text));
  });
  await page.route('**/api/v1/admin/metadata/folder-match/apply', (r) => {
    const body = r.request().postDataJSON() as Seen['applies'][number];
    seen.applies.push(body);
    return json(r, { kind: body.kind, succeeded: body.items.length, failed: 0,
      results: body.items.map((i) => ({ nodeId: i['nodeId'], code: 'ok', queued: 0 })) });
  });
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/artist-folder$/, (r) => {
    if (r.request().method() !== 'PUT') return r.continue();
    const body = r.request().postDataJSON() as Record<string, string>;
    seen.marks.push({ url: r.request().url(), body });
    const nodeId = decodeURIComponent(/\/nodes\/([^/]+)\//.exec(r.request().url())![1]);
    return json(r, { change: { nodeId, link: { nodeId, state: 'ArtistFolder', updatedAt: '2026-10-19T08:00:00Z' } },
      artist: { name: body['name'], role: body['role'] }, creatorAdded: true, queued: 0, authorRef: `mangaupdates:${body['externalId']}` });
  });
  return seen;
}

async function selectFolders(page: Page, libraryId: string, names: string[]): Promise<void> {
  await page.goto(`/libraries/${libraryId}/browse`);
  await page.locator('button.select-toggle').click();
  for (const name of names) await page.locator('.node-wrap', { hasText: name }).locator('.node-card').click();
  await page.getByTestId('series-selection-menu').click();
}

for (const viewport of [
  { width: 1280, height: 900, name: 'desktop' }, { width: 820, height: 1180, name: 'tablet' }, { width: 390, height: 844, name: 'phone' },
]) {
  test(`browse (${viewport.name}): Match folders by name as artists - search MangaUpdates for the rest, pick, Mark`, async ({ page, baseURL }) => {
    test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
    test.setTimeout(180_000);
    const foreign = watchForeignRequests(page, baseURL!);
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await login(page);
    const libraryId = await fixtureLibrary(page);
    const root = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=50`)).json();
    const nodes = NAMES.map((n) => (root.items as Node[]).find((x) => x.displayName === n)!);
    const seen = await mock(page);

    await selectFolders(page, libraryId, NAMES);
    await page.getByTestId('bulk-folder-match').click();
    // The REAL server's preview: no stored artist has these names, so each row carries the text the author search would send.
    await page.getByTestId('folder-match-artists').click();
    await expect(page.getByTestId('folder-match-summary')).toContainText('2 without a match');
    const section = page.getByTestId('folder-match-web-section');
    await expect(section).toContainText('Search MangaUpdates for the rest');
    await expect(page.getByTestId(`folder-match-web-text-${nodes[0].id}`)).toHaveCount(0); // shown once opted in
    expect(seen.searches).toEqual([]);

    await page.getByTestId('folder-match-web').click();
    await expect(page.getByTestId(`folder-match-web-text-${nodes[0].id}`)).toHaveValue(NAMES[0]);
    await expect(page.getByTestId(`folder-match-web-text-${nodes[1].id}`)).toHaveValue(NAMES[1]);
    await page.getByTestId(`folder-match-web-tick-${nodes[0].id}`).locator('input').uncheck();
    await expect(page.getByTestId('folder-match-web-search')).toContainText('Search 1 folder');
    await page.getByTestId('folder-match-web-search').click();
    await expect(page.getByTestId('folder-match-web-note')).toContainText('1 search sent');
    expect(seen.searches).toHaveLength(1);
    expect(seen.searches[0].url).toContain(`/nodes/${nodes[1].id}/author-search`);
    expect(seen.searches[0].body).toEqual({ text: NAMES[1] });

    // Only offered: at most 8, none chosen; the other name, the series count, three genres and a no-referrer link; no images.
    const list = page.getByTestId(`folder-match-author-${nodes[1].id}`);
    await expect(list.locator('mat-radio-button')).toHaveCount(9); // 8 authors + "None of these"
    await expect(list).toContainText(`(as ${NAMES[1]} (pen name))`);
    await expect(list).toContainText('12 series - Drama, Romance, Comedy');
    await expect(list).not.toContainText('Horror');
    await expect(list.locator('img')).toHaveCount(0);
    const link = page.getByTestId(`folder-match-author-${nodes[1].id}-link-0`);
    await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    await expect(link).toHaveAttribute('target', '_blank');
    await expect(page.getByTestId('folder-match-apply')).toBeDisabled();
    await expectFitsScreen(page, `match folders by name, author candidates (${viewport.name})`);
    await shot(page, `a-01-author-candidates-${viewport.name}`);

    await page.getByTestId(`folder-match-author-${nodes[1].id}-0`).click();
    await expect(page.getByTestId('folder-match-apply')).toContainText('Mark 1 folder');
    await page.getByTestId('folder-match-apply').click();
    await expect(page.getByTestId(`folder-match-result-${nodes[1].id}`)).toContainText('Marked');
    expect(seen.applies).toEqual([{ kind: 'Artists', setDoujinContent: true,
      items: [{ nodeId: nodes[1].id, name: NAMES[1], role: 'author', provider: 'mangaupdates', externalId: '48151' }] }]);
    expect(seen.searches).toHaveLength(1); // marking sent nothing more
    await expectFitsScreen(page, `match folders by name, author marked (${viewport.name})`);
    await page.getByTestId('folder-match-close').click();
    expect(foreign).toEqual([]);
  });
}

test('the Artist folder dialog: Find on MangaUpdates searches the Artist field once, and a pick goes with the mark', async ({ page, baseURL }) => {
  test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
  test.setTimeout(180_000);
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  const libraryId = await fixtureLibrary(page);
  const root = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=50`)).json();
  const folder = (root.items as Node[]).find((x) => x.displayName === NAMES[0])!;
  const seen = await mock(page);

  for (const size of [{ width: 1280, height: 900 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(size);
    await selectFolders(page, libraryId, [NAMES[0]]);
    await page.getByTestId('bulk-artist-folder').click();
    await expect(page.getByTestId('artist-folder-name')).toHaveValue(NAMES[0]);
    await expect(page.getByTestId('artist-folder-find-section')).toContainText('only the text in the Artist field');
    await page.getByTestId('artist-folder-name').fill('Synthetic   Anthology Artist');
    await page.getByTestId('artist-folder-find').click();
    const list = page.getByTestId('artist-folder-candidates');
    await expect(list.locator('mat-radio-button')).toHaveCount(9);
    await expectFitsScreen(page, `artist folder dialog with authors (${size.width} px)`);
    await shot(page, `a-02-artist-folder-find-${size.width}`);
    await page.getByTestId('artist-folder-candidates-1').click();
    await expect(page.getByTestId('artist-folder-name')).toHaveValue('Synthetic Anthology Artist Lookalike Number 1 With A Long Name');
    await page.getByTestId('artist-folder-save').click();
    await expect(page.getByText(/Artist folder: Synthetic Anthology Artist Lookalike/)).toBeVisible();
  }
  expect(seen.searches.map((s) => [s.url.includes(`/nodes/${folder.id}/author-search`), s.body.text]))
    .toEqual([[true, 'Synthetic Anthology Artist'], [true, 'Synthetic Anthology Artist']]);
  expect(seen.marks).toHaveLength(2);
  expect(seen.marks[0].url).toContain(`/nodes/${folder.id}/artist-folder`);
  expect(seen.marks[0].body).toEqual({ name: 'Synthetic Anthology Artist Lookalike Number 1 With A Long Name', role: 'author',
    provider: 'mangaupdates', externalId: '48152' });
  expect(foreign).toEqual([]);
});
