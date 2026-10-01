import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * The Volumes view, OFFLINE (1.29.0): a synthetic library whose archives state their volume in the name
 * (`Stacked Saga v01 c001` ...) groups into volume stacks with no series record and no network; the Volumes | Folders
 * switch shows the real folder; the choice is remembered.
 *
 * Needs the fixture library from `e2e/fixtures/make-series-fixtures.mjs`, written next to the series fixtures as
 * `<E2E_SERIES_FIXTURE_ROOT>-volumes` and visible to the SERVER at that path (`scripts/Verify-E2E.ps1` does both).
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
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

/** Registers + scans the volume library once and waits until the series folder is listed. Returns [libraryId, folderId]. */
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

async function setSwitch(page: Page, mode: 'Volumes' | 'Folders' | null): Promise<void> {
  const headers = await csrf(page.request);
  const current = await (await page.request.get('/api/v1/reading/library-preferences')).json();
  const res = await page.request.put('/api/v1/reading/library-preferences', { headers, data: { ...current, seriesViewMode: mode } });
  expect(res.ok(), await res.text()).toBeTruthy();
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

test('a folder of chapters that state their volume groups into stacks, offline', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  await page.goto(`/libraries/${libraryId}/browse/${folderId}`);

  const cards = page.locator('.node-wrap');
  await expect(cards).toHaveCount(3);
  await expect(cards.nth(0).locator('.node-title')).toHaveText('Volume 1');
  await expect(cards.nth(0).locator('.node-sub')).toContainText('3 chapters');
  await expect(cards.nth(1).locator('.node-title')).toHaveText('Volume 2');
  await expect(cards.nth(1).locator('.node-sub')).toHaveText('2 chapters + 1 extra');
  // Volume 3 is a real volume file with no chapters of its own: a plain card, not a stack.
  await expect(cards.nth(2).locator('.node-title')).toHaveText('Stacked Saga v03.cbz');
  await expect(page.locator('app-stack-card')).toHaveCount(2);
  // Nothing is missing without a volume list: no incomplete mark.
  await expect(page.getByTestId('stack-incomplete')).toHaveCount(0);
  await expect(page.getByTestId('volume-view-switch')).toBeVisible();
  await shot(page, 'volumes-01-stacks');
});

test('a stack opens its chapters, with previous / next volume', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  await page.goto(`/libraries/${libraryId}/browse/${folderId}`);

  await page.locator('.node-wrap', { hasText: 'Volume 1' }).locator('a.node-card').click();
  await expect(page).toHaveURL(new RegExp(`/libraries/${libraryId}/browse/${folderId}/volume/1$`));
  await expect(page.getByTestId('stack-title')).toHaveText('Volume 1');
  await expect(page.getByTestId('stack-counts')).toHaveText('3 chapters');
  await expect(page.getByTestId('stack-item')).toHaveCount(3);
  await expect(page.getByTestId('stack-prev')).toHaveCount(0);
  await shot(page, 'volumes-02-stack-view');

  await page.getByTestId('stack-next').click();
  await expect(page).toHaveURL(/\/volume\/2$/);
  await expect(page.getByTestId('stack-title')).toHaveText('Volume 2');
  // The fractional volume file (v02.5) closes volume 2's stack, as an extra.
  await expect(page.getByTestId('stack-item')).toHaveCount(3);
  await expect(page.getByTestId('stack-item').last()).toContainText('Stacked Saga v02.5.cbz');
  await expect(page.getByTestId('stack-counts')).toHaveText('2 chapters - 1 extra');
  await expect(page.getByTestId('stack-next')).toHaveCount(0);

  // The breadcrumb names the REAL folder.
  await page.getByTestId('stack-folder-link').click();
  await expect(page.locator('.node-wrap')).toHaveCount(3);
});

/** The next save of the viewer's browse preferences (the Volumes | Folders choice). */
function savedPreferences(page: Page) {
  return page.waitForResponse((r) => r.url().includes('/api/v1/reading/library-preferences') && r.request().method() === 'PUT');
}

test('the Folders switch shows the real chapters and the choice is remembered', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
  await expect(page.locator('.node-wrap')).toHaveCount(3);

  // Wait for the choice to be saved before the reload below (it raced the save on the GitHub runner and on the box).
  const savedFolders = savedPreferences(page);
  await page.getByTestId('view-folders').click();
  await savedFolders;
  await expect(page.locator('.node-wrap')).toHaveCount(7);
  await expect(page.locator('app-stack-card')).toHaveCount(0);
  await expect(page.locator('.node-wrap', { hasText: 'Stacked Saga v01 c001' })).toBeVisible();
  await shot(page, 'volumes-03-folders');

  // Remembered per user: a reload stays on Folders.
  await page.reload();
  await expect(page.locator('.node-wrap')).toHaveCount(7);
  await expect(page.getByTestId('view-folders')).toHaveAttribute('aria-pressed', 'true');

  const savedVolumes = savedPreferences(page);
  await page.getByTestId('view-volumes').click();
  await savedVolumes;
  await expect(page.locator('.node-wrap')).toHaveCount(3);
  await page.reload();
  await expect(page.locator('.node-wrap')).toHaveCount(3);
  await expect(page.getByTestId('view-volumes')).toHaveAttribute('aria-pressed', 'true');
  await setSwitch(page, null);
});

test('the phone layout groups too', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  await page.goto(`/libraries/${libraryId}/browse/${folderId}`);

  await expect(page.locator('.node-wrap')).toHaveCount(3);
  await expect(page.getByTestId('volume-view-switch')).toBeVisible();
  await shot(page, 'volumes-04-phone');
});

test('a stack shows a star when one of its chapters is starred', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  const flat = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?parentId=${folderId}&pageSize=50&group=flat`)).json();
  const chapter = (flat.items as Node[]).find((n) => n.displayName === 'Stacked Saga v01 c002.cbz')!;
  const headers = await csrf(page.request);
  expect((await page.request.post(`/api/v1/nodes/${chapter.id}/favorite`, { headers })).ok()).toBeTruthy();
  try {
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    const cards = page.locator('.node-wrap');
    await expect(cards).toHaveCount(3);
    await expect(cards.nth(0).getByTestId('stack-star')).toBeVisible();
    await expect(cards.nth(1).getByTestId('stack-star')).toHaveCount(0);
    await shot(page, 'volumes-05-stack-star');
  } finally {
    await page.request.delete(`/api/v1/nodes/${chapter.id}/favorite`, { headers });
  }
});

// --- Selection and List view (1.30.0) ---

/** Puts the fixture library back as the tests found it: every chapter unread and unstarred. */
async function resetChapters(page: Page, libraryId: string, folderId: string): Promise<void> {
  const headers = await csrf(page.request);
  const flat = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?parentId=${folderId}&pageSize=50&group=flat`)).json();
  for (const n of flat.items as Node[]) {
    await page.request.delete(`/api/v1/reading/${n.id}/read`, { headers });
    await page.request.delete(`/api/v1/nodes/${n.id}/favorite`, { headers });
  }
}

/** Writes the viewer's browse preferences and returns the previous ones. */
async function setPreferences(page: Page, change: Record<string, unknown>): Promise<Record<string, unknown>> {
  const headers = await csrf(page.request);
  const current = await (await page.request.get('/api/v1/reading/library-preferences')).json();
  const res = await page.request.put('/api/v1/reading/library-preferences', { headers, data: { ...current, ...change } });
  expect(res.ok(), await res.text()).toBeTruthy();
  return current;
}

test('a stack page selects chapters (tap, Shift-click range) and marks them read, then unread', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  try {
    await page.goto(`/libraries/${libraryId}/browse/${folderId}/volume/1`);
    await page.getByTestId('stack-select').click();
    await expect(page.getByTestId('selection-count')).toHaveText('0 selected');

    const chapters = page.getByTestId('stack-item');
    await chapters.nth(0).click();
    // The tap selected the chapter; it did not open the reader.
    await expect(page).toHaveURL(/\/volume\/1$/);
    await expect(page.getByTestId('selection-count')).toHaveText('1 selected');
    await chapters.nth(2).click({ modifiers: ['Shift'] });
    await expect(page.getByTestId('selection-count')).toHaveText('3 selected');
    await shot(page, 'volumes-06-stack-select');

    await page.getByTestId('selection-mark-read').click();
    await expect(page.getByText('Marked read: 3 items')).toBeVisible();
    await expect(page.locator('.badge.read')).toHaveCount(3);

    // Saved for real: still read after a reload.
    await page.reload();
    await expect(page.locator('.badge.read')).toHaveCount(3);

    await page.getByTestId('stack-select').click();
    await page.getByTestId('selection-select-menu').click();
    await page.getByRole('menuitem', { name: 'Select all read' }).click();
    await expect(page.getByTestId('selection-count')).toHaveText('3 selected');
    await page.getByTestId('selection-mark-unread').click();
    await expect(page.getByText('Marked unread: 3 items')).toBeVisible();
    await expect(page.locator('.badge.read')).toHaveCount(0);

    // Done leaves select mode: a tap opens the reader again.
    await page.getByTestId('selection-done').click();
    await expect(page.getByTestId('stack-select')).toBeVisible();
    await chapters.nth(0).click();
    await expect(page).toHaveURL(/\/reader\//);
  } finally {
    await resetChapters(page, libraryId, folderId);
  }
});

test('a stack page stars the selected chapters', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  try {
    await page.goto(`/libraries/${libraryId}/browse/${folderId}/volume/1`);
    await page.getByTestId('stack-select').click();
    await page.getByTestId('stack-item').nth(0).click();
    await page.getByTestId('stack-item').nth(1).click();
    await page.getByTestId('selection-favorites').click();
    await page.getByTestId('selection-add-favorite').click();
    await expect(page.getByText('Added to favorites: 2 items')).toBeVisible();

    // Leaving select mode shows the stars (the two starred chapters are filled).
    await page.getByTestId('selection-done').click();
    await expect(page.locator('app-star-toggle .star-btn.active')).toHaveCount(2);
  } finally {
    await resetChapters(page, libraryId, folderId);
  }
});

test('a whole volume is selected in the Volumes view and marked read in one step', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  try {
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    const cards = page.locator('.node-wrap');
    await expect(cards).toHaveCount(3);

    await page.locator('button.select-toggle').click();
    await cards.nth(0).locator('a.node-card').click();
    // The tap selected the stack (it did not open it); its incomplete mark, if any, makes room for the check.
    await expect(page).toHaveURL(new RegExp(`/browse/${folderId}$`));
    await expect(page.getByTestId('selection-count')).toHaveText('1 selected');
    await expect(cards.nth(0)).toHaveClass(/selected/);
    await shot(page, 'volumes-07-stack-selected');

    await page.getByTestId('selection-mark-read').click();
    await expect(page.getByText('Marked read: 3 items')).toBeVisible();
    await expect(cards.nth(0).locator('.badge.read')).toBeVisible();
    await expect(cards.nth(1).locator('.badge.read')).toHaveCount(0);

    // Every chapter of the volume is read - and only those.
    await page.goto(`/libraries/${libraryId}/browse/${folderId}/volume/1`);
    await expect(page.locator('.badge.read')).toHaveCount(3);
    await page.goto(`/libraries/${libraryId}/browse/${folderId}/volume/2`);
    await expect(page.locator('.badge.read')).toHaveCount(0);

    // And back to unread in one step.
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    await page.locator('button.select-toggle').click();
    await cards.nth(0).locator('a.node-card').click();
    await page.getByTestId('selection-mark-unread').click();
    await expect(page.getByText('Marked unread: 3 items')).toBeVisible();
    await expect(cards.nth(0).locator('.badge.read')).toHaveCount(0);
  } finally {
    await resetChapters(page, libraryId, folderId);
  }
});

test('the folder list shows the fresh state of a volume after chapters change on its page', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  try {
    // The browse page is retained while the stack page is open: mark a whole volume read there and step back.
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    await expect(page.locator('.node-wrap')).toHaveCount(3);
    await page.locator('.node-wrap', { hasText: 'Volume 1' }).locator('a.node-card').click();
    await expect(page.getByTestId('stack-title')).toHaveText('Volume 1');
    await page.getByTestId('stack-select').click();
    await page.getByTestId('selection-select-menu').click();
    await page.getByRole('menuitem', { name: 'Select all', exact: true }).click();
    await page.getByTestId('selection-mark-read').click();
    await expect(page.getByText('Marked read: 3 items')).toBeVisible();

    await page.goBack();
    await expect(page.locator('.node-wrap').nth(0).locator('.badge.read')).toBeVisible();
    await expect(page.locator('.node-wrap').nth(1).locator('.badge.read')).toHaveCount(0);
  } finally {
    await resetChapters(page, libraryId, folderId);
  }
});

test('a stack page follows the List view: shared rows, select through the checkbox', async ({ page }) => {
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await setSwitch(page, null);
  const saved = await setPreferences(page, { viewMode: 'list' });
  try {
    await page.goto(`/libraries/${libraryId}/browse/${folderId}/volume/1`);
    const rows = page.getByTestId('stack-row');
    await expect(rows).toHaveCount(3);
    await expect(page.getByTestId('stack-item')).toHaveCount(0);
    await expect(rows.nth(0).locator('.node-title')).toContainText('Stacked Saga v01 c001');
    await expect(rows.nth(0).locator('.node-sub')).toContainText('Ch. 1');
    await shot(page, 'volumes-08-stack-list');

    // The leading checkbox selects without entering select mode first, and the bar appears.
    await rows.nth(0).locator('.row-select').click();
    await expect(page.getByTestId('selection-count')).toHaveText('1 selected');
    await rows.nth(2).locator('a.node-card').click({ modifiers: ['Shift'] });
    await expect(page.getByTestId('selection-count')).toHaveText('3 selected');
    await expect(page).toHaveURL(/\/volume\/1$/);
    await shot(page, 'volumes-09-stack-list-select');

    // The Volumes view itself in List view: a stack is a row that can be selected too.
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    await expect(page.locator('.node-wrap').first().locator('.row-select')).toBeVisible();
    await page.locator('.node-wrap').first().locator('.row-select').click();
    await expect(page.getByTestId('selection-count')).toHaveText('1 selected');
    await shot(page, 'volumes-10-volumes-list-select');
  } finally {
    await setPreferences(page, { viewMode: saved['viewMode'] });
    await resetChapters(page, libraryId, folderId);
  }
});

test('1.31.0: the same chapter in two files is counted once and marked as a duplicate, at desktop and phone width', async ({ page }) => {
  await login(page);
  const [libraryId] = await ensureLibrary(page);
  let folderId = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=50`)).json();
    folderId = ((root.items as Node[]).find((n) => n.displayName === 'Doubled Saga')?.id) ?? '';
    return folderId;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).not.toBe('');
  await setSwitch(page, null);

  // The card: one stack of three different chapters, marked "2 duplicates".
  await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
  const card = page.locator('.node-wrap', { hasText: 'Volume 1' });
  await expect(page.locator('app-stack-card')).toHaveCount(1);
  await expect(card.getByTestId('stack-duplicates')).toHaveText('2 duplicates');
  await shot(page, 'duplicates-01-card');

  // The stack page counts the chapters once, says which repeat, and keeps all five files.
  await card.locator('a.node-card').click();
  await expect(page.getByTestId('stack-title')).toHaveText('Volume 1');
  await expect(page.getByTestId('stack-counts')).toHaveText('3 chapters');
  await expect(page.getByTestId('stack-duplicates')).toHaveText('2 duplicate chapters: Chapter 1: 2 files, Chapter 2: 2 files');
  await expect(page.getByTestId('stack-item')).toHaveCount(5);
  await expect(page.locator('.slot .sub', { hasText: '2 files' })).toHaveCount(4);
  await expectFitsScreen(page, 'duplicate chapters, stack page, desktop');
  await shot(page, 'duplicates-02-stack-desktop');

  await page.setViewportSize({ width: 390, height: 844 });
  await page.reload();
  await expect(page.getByTestId('stack-duplicates')).toBeVisible();
  await expectFitsScreen(page, 'duplicate chapters, stack page, phone');
  await shot(page, 'duplicates-03-stack-phone');
  await page.goBack();
  await expect(card.getByTestId('stack-duplicates')).toBeVisible();
  await expectFitsScreen(page, 'duplicate chapters, stack card, phone');
});
