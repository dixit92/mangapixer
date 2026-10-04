import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * 1.34.0 (lane V): the Volumes view of a webtoon without a volume list is its CHAPTER list - the switch reads Chapters, the status
 * line counts chapters and never "volumes missing", and no missing-volume placeholder is drawn. The files are real (a synthetic
 * folder named `0003 [0001 - Some Title]` - a running index, the chapter in brackets); the series link and its view state cannot be
 * made offline, so the view state is a contract-shaped `volume-view` answer served in the browser (the server side is covered by
 * `WebtoonVolumesHttpTests`). Checked at 1280 and 390 px.
 *
 * Needs the fixture library from `e2e/fixtures/make-series-fixtures.mjs` (`<E2E_SERIES_FIXTURE_ROOT>-volumes`, see volume-stacks.spec.ts).
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const VOLUMES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-volumes` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Volume Stacks';

test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic volume library available');
test.describe.configure({ mode: 'serial' });

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

/** The volume fixture library (registered + scanned once, shared with volume-stacks.spec.ts); returns [libraryId, webtoonFolderId]. */
async function ensureWebtoon(page: Page): Promise<[string, string]> {
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
    folder = ((root.items as Node[]).find((n) => n.displayName === 'Indexed Webtoon')?.id) ?? '';
    if (!folder) return 0;
    const inside = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?parentId=${folder}&pageSize=50&group=flat`)).json();
    return (inside.items as Node[]).length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(10);
  return [lib!.id, folder];
}

/** The view state the server gives a linked webtoon whose MangaDex list is near-empty (contract-shaped VolumeViewDto). */
async function serveChapterView(page: Page, folderId: string): Promise<void> {
  await page.route(new RegExp(`/api/v1/nodes/${folderId}/volume-view$`), (r) => r.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      nodeId: folderId, available: true, active: true, defaultActive: true, consolidated: false, stackCount: 0, chaptersOnly: true,
      hasSeriesStatus: true, seriesStatus: 'Ongoing', origin: 'Korea', missingVolumes: 0, missingChapters: 2, releaseKnown: true,
      language: 'en', releasedVolumes: 12, coversPending: 0,
    }),
  }));
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

for (const [label, width, height] of [['desktop', 1280, 900], ['phone', 390, 844]] as const) {
  test(`a webtoon without a volume list lists its chapters in order, no volume missing (${label})`, async ({ page }) => {
    await page.setViewportSize({ width, height });
    await login(page);
    const [libraryId, folderId] = await ensureWebtoon(page);
    await serveChapterView(page, folderId);
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);

    const cards = page.locator('.node-wrap');
    await expect(cards).toHaveCount(10);
    await expect(cards.nth(0).locator('.node-title')).toHaveText('0001 [0000].cbz');
    await expect(cards.nth(2).locator('.node-title')).toHaveText('0003 [0001 - Some Title].cbz');
    await expect(page.getByTestId('missing-volume')).toHaveCount(0);
    await expect(page.locator('app-stack-card')).toHaveCount(0);

    const viewSwitch = page.getByTestId('view-volumes');
    await expect(viewSwitch).toHaveAttribute('aria-pressed', 'true');
    if (width >= 600) await expect(viewSwitch).toContainText('Chapters');
    await expect(viewSwitch.locator('mat-icon')).toHaveText('format_list_numbered');

    const status = page.getByTestId('series-status');
    await expect(status).toContainText('2 chapters missing');
    await expect(status).not.toContainText('volumes missing');
    await expectFitsScreen(page, 'the webtoon chapter list');
    await shot(page, `webtoon-chapters-${label}`);
  });
}
