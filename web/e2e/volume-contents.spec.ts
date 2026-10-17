import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * The volume contents scan (1.44.0), OFFLINE, on the owner's path: a folder holds a volume file and chapter files whose pages are
 * inside it (another release: lightly changed pixels). Opening the folder's Volumes view puts it at the front of the scan; the volume
 * file and the chapters found join one stack, and the volume's page says "Chapters 9-10 are inside the volume file." Chapter 11 (not in
 * the volume) stays loose. No series record, no network.
 *
 * Needs `<E2E_SERIES_FIXTURE_ROOT>-volume-contents` from `e2e/fixtures/make-series-fixtures.mjs`, visible to the SERVER at that path.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const CONTENTS_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-volume-contents` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Volume Contents';

test.skip(!CONTENTS_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic volume contents library available');
test.describe.configure({ mode: 'serial' });

interface Node { id: string; displayName: string; kind: string; pageCount?: number | null }

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

/** Registers + scans the library once and waits until its 4 archives are analysed. Returns [libraryId, folderId]. */
async function ensureLibrary(page: Page): Promise<[string, string]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: CONTENTS_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folder = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = ((root.items as Node[]).find((n) => n.displayName === 'Contents Saga')?.id) ?? '';
    if (!folder) return 0;
    const inside = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?parentId=${folder}&pageSize=50&group=flat`)).json();
    return (inside.items as Node[]).filter((n) => (n.pageCount ?? 0) > 0).length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(4);
  return [lib!.id, folder];
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

test('the chapters inside a volume file join its stack, and the volume says so', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);

  // The owner's path: the folder's Volumes view (opening it puts the folder first in the scan) -> the volume -> the line.
  // Before the scan nothing groups (one volume file, loose chapters): the folder lists its files.
  await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
  const volume = page.locator('.node-wrap', { hasText: 'Volume 2' });
  // The scan runs in the background: reload until the volume file and the chapters found inside are one stack.
  await expect.poll(async () => {
    await page.reload();
    await page.locator('.node-wrap').first().waitFor();
    return page.locator('app-stack-card').count();
  }, { timeout: 120_000, intervals: [2000, 3000, 5000] }).toBe(1);
  await expect(volume).toBeVisible();
  await expect(volume.locator('.node-sub')).toContainText('2 chapters');
  // Chapter 11 is not inside: it stays a loose chapter after the volumes.
  await expect(page.locator('.node-wrap', { hasText: 'Chapter 011' })).toBeVisible();
  await shot(page, 'volume-contents-01-folder');

  await volume.locator('a.node-card').click();
  await expect(page).toHaveURL(new RegExp(`/libraries/${libraryId}/browse/${folderId}/volume/2$`));
  await expect(page.getByTestId('stack-title')).toHaveText('Volume 2');
  await expect(page.getByTestId('stack-in-volume-file')).toHaveText('Chapters 9-10 are inside the volume file.');
  // The volume file first, then the two chapters found inside it.
  await expect(page.getByTestId('stack-item')).toHaveCount(3);
  await expectFitsScreen(page);
  await shot(page, 'volume-contents-02-stack');
});

test('the stack page with the line fits a phone', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await page.goto(`/libraries/${libraryId}/browse/${folderId}/volume/2`);
  await expect(page.getByTestId('stack-in-volume-file')).toHaveText('Chapters 9-10 are inside the volume file.', { timeout: 60_000 });
  await expectFitsScreen(page);
  const box = await page.getByTestId('stack-in-volume-file').boundingBox();
  expect(box).not.toBeNull();
  const hit = await page.evaluate(([x, y]) => document.elementFromPoint(x, y)?.getAttribute('data-testid') ?? '',
    [box!.x + 4, box!.y + box!.height / 2]);
  expect(hit).toBe('stack-in-volume-file');
  await shot(page, 'volume-contents-03-phone');
});
