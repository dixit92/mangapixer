import { test, expect, Page, APIRequestContext, Route } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Tankoubon stacks (1.37.0): in a folder that is neither a series nor a collection (an artist's folder of stories), the stories each
 * linked to the SAME collected-volume record show as ONE stacked card - the record's title, "N stories" - and opening it lists them;
 * the Volumes | Folders switch shows the real folder. The files are real (`Story Artist/` in the synthetic volume library); archive
 * links to a record cannot be made offline, so the folder's view state, its Volumes-view page and the stack's stories are
 * contract-shaped answers served in the browser, built from the REAL archive cards (the server side - which folders qualify, the
 * grouping, the cache key, the cover - is covered by `CollectionStackHttpTests` and `CollectionStackEntryTests`). Never the network.
 * Checked at 1280, 820 and 390 px.
 *
 * Needs the fixture library from `e2e/fixtures/make-series-fixtures.mjs` (`<E2E_SERIES_FIXTURE_ROOT>-volumes`, see volume-stacks.spec.ts).
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const VOLUMES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-volumes` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Volume Stacks';
const KEY = 'tank1';
const TITLE = 'Synthetic Collected Volume';

test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic volume library available');
test.describe.configure({ mode: 'serial' });

interface Node { id: string; displayName: string; kind: string; coverUrl?: string | null; parentId?: string }

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

/** The volume fixture library (registered + scanned once, shared with volume-stacks.spec.ts); returns [libraryId, folderId, stories]. */
async function ensureArtist(page: Page): Promise<[string, string, Node[]]> {
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
  let stories: Node[] = [];
  let rescanned = false;
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = ((root.items as Node[]).find((n) => n.displayName === 'Story Artist')?.id) ?? '';
    if (!folder) {
      // Another spec may have registered the library without waiting for its scan: ask for one (refused while one runs).
      if (!rescanned) await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
      rescanned = true;
      return 0;
    }
    const inside = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?parentId=${folder}&pageSize=50&group=flat`)).json();
    stories = inside.items as Node[];
    return stories.length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(3);
  return [lib!.id, folder, stories];
}

async function setSwitch(page: Page, mode: 'Volumes' | 'Folders' | null): Promise<void> {
  const headers = await csrf(page.request);
  const current = await (await page.request.get('/api/v1/reading/library-preferences')).json();
  const res = await page.request.put('/api/v1/reading/library-preferences', { headers, data: { ...current, seriesViewMode: mode } });
  expect(res.ok(), await res.text()).toBeTruthy();
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

/**
 * What the server answers once Beta and Gamma are linked to one collected volume (contract-shaped): the view state, the Volumes-view
 * page (Alpha, then the stack in Beta's place) and the stack's stories. The Folders view (`group=flat`) stays the real server's.
 */
async function serveCollection(page: Page, libraryId: string, folderId: string, stories: Node[]): Promise<void> {
  const [alpha, beta, gamma] = stories;
  const card = {
    id: `cs.${folderId}.${KEY}`, parentId: folderId, libraryId, kind: 'VolumeStack', displayName: TITLE, availability: 'Available',
    coverUrl: beta.coverUrl ?? null, childFolderCount: null, childArchiveCount: null, pageCount: null, readingState: null, lastReadPage: null,
    readerDefault: null, isRead: false, isFavorite: false, readRollup: 'Unread', hasSeriesInfo: false, volumeStack: null,
    collectionStack: { key: KEY, title: TITLE, storyCount: 2 },
  };
  await page.route(new RegExp(`/api/v1/nodes/${folderId}/volume-view$`), (r) => json(r, {
    nodeId: folderId, available: true, active: true, defaultActive: true, consolidated: false, stackCount: 0, collectionStackCount: 1,
    chaptersOnly: false, hasSeriesStatus: false, missingVolumes: 0, missingChapters: 0, releaseKnown: false, coversPending: 0,
  }));
  await page.route(new RegExp(`/api/v1/libraries/${libraryId}/browse\\?.*parentId=${folderId}`), (r) => {
    if (new URL(r.request().url()).searchParams.get('group') === 'flat') return r.continue();
    return json(r, { items: [alpha, card], totalCount: 2, nextCursor: null, hasMore: false });
  });
  await page.route(new RegExp(`/api/v1/nodes/${folderId}/collection-stacks/${KEY}$`), (r) => json(r, {
    folderId, key: KEY, title: TITLE, coverUrl: beta.coverUrl ?? null, storyCount: 2, items: [beta, gamma], previousKey: null, nextKey: null,
  }));
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

for (const [label, width, height] of [['desktop', 1280, 900], ['tablet', 820, 1180], ['phone', 390, 844]] as const) {
  test(`the stories of one collected volume show as one stacked card that opens their list (${label})`, async ({ page, baseURL }) => {
    test.setTimeout(180_000); // the first spec to use the volume library waits for its scan
    const foreign = watchForeignRequests(page, baseURL!);
    await page.setViewportSize({ width, height });
    await login(page);
    const [libraryId, folderId, stories] = await ensureArtist(page);
    await setSwitch(page, null);
    await serveCollection(page, libraryId, folderId, stories);
    try {
      await page.goto(`/libraries/${libraryId}/browse/${folderId}`);

      // One plain card (Alpha) and ONE stacked card for Beta + Gamma: the record's title, "2 stories", no volume badges.
      const cards = page.locator('.node-wrap');
      await expect(cards).toHaveCount(2);
      await expect(cards.nth(0).locator('.node-title')).toHaveText('Story Artist - Alpha Tale.cbz');
      const stack = cards.nth(1);
      await expect(stack.getByTestId('collection-stack-card')).toHaveCount(1);
      await expect(stack.locator('.node-title')).toHaveText(TITLE);
      await expect(stack.locator('.node-sub')).toHaveText('2 stories');
      await expect(stack.getByTestId('stack-incomplete')).toHaveCount(0);
      await expect(page.getByTestId('view-volumes')).toHaveAttribute('aria-pressed', 'true');
      await expectFitsScreen(page, `artist folder with a tankoubon stack (${label})`);
      await shot(page, `collection-folder-${label}`);

      // Opening it lists the two stories in their folder order, under the real folder's breadcrumb.
      await stack.locator('a.node-card').click();
      await expect(page).toHaveURL(new RegExp(`/libraries/${libraryId}/browse/${folderId}/collection/${KEY}$`));
      await expect(page.getByTestId('stack-title')).toHaveText(TITLE);
      await expect(page.getByTestId('stack-counts')).toHaveText('2 stories');
      const items = page.getByTestId('stack-item');
      await expect(items).toHaveCount(2);
      await expect(items.nth(0).locator('.title')).toHaveText('Story Artist - Beta Tale.cbz');
      await expect(items.nth(1).locator('.title')).toHaveText('Story Artist - Gamma Tale.cbz');
      await expect(items.nth(0)).toHaveAttribute('href', `/reader/${stories[1].id}`);
      await expect(page.getByTestId('stack-folder-link')).toHaveText('Story Artist');
      await expect(page.getByTestId('missing-chapter')).toHaveCount(0);
      await expectFitsScreen(page, `tankoubon stack page (${label})`);
      await shot(page, `collection-stack-${label}`);

      // Back in the folder, Folders shows every story as its own card (the real server's list).
      await page.getByTestId('stack-folder-link').click();
      await expect(page.locator('.node-wrap')).toHaveCount(2);
      const flat = page.waitForResponse((r) => r.url().includes(`parentId=${folderId}`) && r.url().includes('group=flat'));
      await page.getByTestId('view-folders').click();
      await flat;
      await expect(page.locator('.node-wrap')).toHaveCount(3);
      await expect(page.getByTestId('collection-stack-card')).toHaveCount(0);
      await expectFitsScreen(page, `artist folder, Folders view (${label})`);
      expect(foreign).toEqual([]);
    } finally {
      await setSwitch(page, null);
    }
  });
}
