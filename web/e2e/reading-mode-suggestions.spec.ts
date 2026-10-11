import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * 1.41.0 (lane R): Administration > Reading mode suggestions. A folder of webtoon-shaped tall strips (real files, measured by
 * analysis) is listed as "Vertical (webtoon)"; Apply sets its folder reader default, which the reader's effective mode of its archives
 * then reports, and the folder leaves the list. The page (with long, contract-shaped rows) fits a phone and a tablet screen.
 *
 * Needs the fixture library from `e2e/fixtures/make-series-fixtures.mjs` (`<E2E_SERIES_FIXTURE_ROOT>-reading-modes`).
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const MODES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-reading-modes` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Reading Modes';
const SUGGESTIONS = '/api/v1/admin/reading-mode-suggestions';

test.skip(!MODES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic reading-modes library available');
test.describe.configure({ mode: 'serial' });

interface Node { id: string; displayName: string; kind: string }
interface Row { nodeId: string; folderName: string; suggestedMode: string; currentSource: string }

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

/** The fixture library (registered + scanned once); returns [libraryId, tall folder id, an archive in it]. */
async function ensureLibrary(page: Page): Promise<[string, string, string]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: MODES_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folder = '';
  let archive = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = ((root.items as Node[]).find((n) => n.displayName === 'Tall Strip Tale')?.id) ?? '';
    if (!folder) return 0;
    const inside = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?parentId=${folder}&pageSize=50&group=flat`)).json();
    archive = ((inside.items as Node[]).find((n) => n.kind === 'Archive')?.id) ?? '';
    return (inside.items as Node[]).length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(3);
  return [lib!.id, folder, archive];
}

async function effectiveMode(page: Page, archiveId: string): Promise<string> {
  return (await (await page.request.get(`/api/v1/reading/${archiveId}/effective-mode`)).json()).readerMode;
}

test('a tall-strip folder is suggested Vertical; Apply sets its reading mode', async ({ page }) => {
  test.setTimeout(240_000);
  await login(page);
  const [, folderId, archiveId] = await ensureLibrary(page);

  // Analysis measures the pages after the scan: wait until the folder is suggested.
  await expect.poll(async () => {
    const list = await (await page.request.get(SUGGESTIONS)).json();
    return ((list.items as Row[]).find((r) => r.nodeId === folderId)?.suggestedMode) ?? '';
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe('VerticalWebtoon');
  expect(await effectiveMode(page, archiveId)).not.toBe('VerticalWebtoon');

  await page.goto('/admin');
  await page.getByTestId('reading-modes-link').click();
  await expect(page).toHaveURL(/\/admin\/reading-modes$/);
  const row = page.getByTestId('reading-mode-row').filter({ hasText: 'Tall Strip Tale' });
  await expect(row).toHaveCount(1);
  await expect(row.getByTestId('reading-mode-suggested')).toHaveText('Vertical (webtoon)');
  await expect(row).toContainText('24 of 24 sampled pages are tall strips');
  await expect(page.getByTestId('reading-mode-row').filter({ hasText: 'Paged Tale' })).toHaveCount(0);
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/reading-modes-1280.png` });

  const applied = page.waitForResponse((r) => r.request().method() === 'POST' && r.url().endsWith(`${SUGGESTIONS}/apply`));
  await row.getByTestId('reading-mode-apply').click();
  expect((await applied).ok()).toBeTruthy();
  await expect(page.getByTestId('reading-modes-message')).toContainText('Applied to 1 folder.');
  await expect(page.getByTestId('reading-mode-row').filter({ hasText: 'Tall Strip Tale' })).toHaveCount(0);

  // The reader now opens the folder's archives vertically; the folder's own reader default says so too.
  expect(await effectiveMode(page, archiveId)).toBe('VerticalWebtoon');

  // Leave the instance as it was for a re-run (the existing folder reader-default DELETE).
  const headers = await csrf(page.request);
  expect((await page.request.delete(`/api/v1/admin/folders/${folderId}/reader-default`, { headers })).ok()).toBeTruthy();
});

test('the suggestions page fits a phone and a tablet screen', async ({ page }) => {
  const long = 'A Remarkably Long Folder Name For A Webtoon That Keeps Going Past The Edge Of A Phone Screen';
  const row = (i: number, extra: Record<string, unknown> = {}) => ({
    nodeId: `n${i}`, libraryId: 'lib1', libraryName: 'A Library With A Long Name', folderName: `${long} ${i}`, parentName: 'Manhwa And Webtoons',
    suggestedMode: 'VerticalWebtoon', currentMode: 'PagedRtl', currentSource: 'Library', confidence: 'High', signal: 'TallPages',
    measuredPages: 400, tallPages: 398, comicInfoRightToLeft: 0, comicInfoStated: 0, linkedOrigin: 'Korea', linkedWebtoon: true, subfolders: 12,
    ...extra,
  });
  await page.route(new RegExp(`${SUGGESTIONS}$`), (r) => r.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      items: [row(1), row(2, { suggestedMode: 'PagedRtl', confidence: 'Medium', signal: 'ComicInfo', comicInfoRightToLeft: 12, comicInfoStated: 14,
        linkedOrigin: 'Japan', linkedWebtoon: null, currentSource: 'None', currentMode: null }), row(3, { confidence: 'Low', signal: 'LinkedRecord' })],
      setByAdmin: [row(4, { currentSource: 'FolderAbove' })],
      foldersChecked: 1234, total: 3, limit: 500,
    }),
  }));
  await login(page);
  for (const size of [{ width: 390, height: 844 }, { width: 820, height: 1180 }]) {
    await page.setViewportSize(size);
    await page.goto('/admin/reading-modes');
    await expect(page.getByTestId('reading-mode-row')).toHaveCount(3);
    await page.getByTestId('reading-modes-admin-set-toggle').click();
    await expect(page.getByTestId('reading-mode-admin-row')).toHaveCount(1);
    await expectFitsScreen(page, `reading mode suggestions at ${size.width}`);
    // The Apply button is reachable (nothing covers it).
    const apply = page.getByTestId('reading-mode-apply').first();
    await apply.scrollIntoViewIfNeeded();
    const box = (await apply.boundingBox())!;
    const hit = await page.evaluate(([x, y]) => document.elementFromPoint(x, y)?.closest('[data-testid="reading-mode-apply"]') !== null,
      [box.x + box.width / 2, box.y + box.height / 2]);
    expect(hit).toBeTruthy();
    if (SHOTS) await page.screenshot({ path: `${SHOTS}/reading-modes-${size.width}.png`, fullPage: true });
  }
});
