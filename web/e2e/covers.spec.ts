import { test, expect, Page, APIRequestContext } from '@playwright/test';

/**
 * The cover layer (1.29.0) in a real browser against a real server: an admin opens "Choose cover..." from the browse
 * selection bar, picks another item's cover for a series folder, and the card changes to the new (versioned, layered)
 * cover; "This file's cover" pins it back to the file; Automatic removes the choice. Phone: the picker is full-screen.
 *
 * Needs the synthetic fixture library from `e2e/fixtures/make-series-fixtures.mjs` visible to the SERVER at
 * E2E_SERIES_FIXTURE_ROOT (as `series-info.spec.ts`); without it the suite skips. Local data only: no provider request.
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
test.describe.configure({ mode: 'serial' });

interface Node { id: string; displayName: string; kind: string; coverUrl?: string | null; coverSource?: string | null }

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

/** Registers + scans the fixture library once (shared with series-info.spec.ts), then waits for the series' covers. */
async function ensureLibrary(page: Page): Promise<string> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', {
      headers, data: { displayName: LIBRARY_NAME, rootPath: FIXTURE_ROOT },
    });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    return (root.items as Node[]).find((n) => n.displayName === 'Synthetic Series')?.coverUrl ?? null;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).not.toBeNull();
  return lib!.id;
}

async function children(page: Page, libraryId: string, parentId?: string): Promise<Node[]> {
  const q = parentId ? `&parentId=${parentId}` : '';
  return (await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?pageSize=50&sort=name${q}`)).json()).items as Node[];
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

async function openPickerFor(page: Page, cardText: string): Promise<void> {
  await page.locator('button.select-toggle').click();
  await page.locator('.node-wrap', { hasText: cardText }).locator('.node-card').click();
  await page.getByTestId('selection-cover').click();
  await expect(page.getByTestId('cover-picker-current')).toBeVisible();
}

test('the picker opens from the selection bar and a choice changes the card', async ({ page }) => {
  await login(page);
  const libraryId = await ensureLibrary(page);
  const series = (await children(page, libraryId)).find((n) => n.displayName === 'Synthetic Series')!;
  const volumes = await children(page, libraryId, series.id);
  const v02 = volumes.find((n) => n.displayName.includes('v02'))!;
  const fileCover = series.coverUrl!;
  const card = page.locator('.node-wrap', { hasText: 'Synthetic Series' }).locator('img').first();

  await page.goto(`/libraries/${libraryId}/browse`);
  await expect(card).toHaveAttribute('src', fileCover);
  await openPickerFor(page, 'Synthetic Series');
  await expect(page.getByTestId('cover-pick-automatic')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('cover-picker-web-unavailable')).toBeVisible(); // not linked: no web covers
  await shot(page, 'covers-01-picker-desktop');

  await page.getByTestId(`cover-pick-archive-${v02.id}`).click();
  await page.getByTestId('cover-picker-apply').click();
  await expect(page.getByTestId('cover-picker-current')).toHaveCount(0);
  await page.locator('button.done').click();

  // The card switched in place to the layered, versioned cover of the folder - and the server agrees after a reload.
  await expect(card).toHaveAttribute('src', new RegExp(`^/api/v1/nodes/${series.id}/cover\\?v=`));
  const chosen = await card.getAttribute('src');
  await page.reload();
  await expect(card).toHaveAttribute('src', chosen!);
  const served = await page.request.get(chosen!);
  expect(served.status()).toBe(200);
  expect(served.headers()['cache-control']).toContain('private');
  await shot(page, 'covers-02-card-changed');

  // "This file's cover" pins the file cover again; Automatic removes the choice.
  await openPickerFor(page, 'Synthetic Series');
  await expect(page.getByTestId('cover-picker-current')).toContainText("Another item's cover");
  await page.getByTestId('cover-pick-File').click();
  await page.getByTestId('cover-picker-apply').click();
  await expect(card).toHaveAttribute('src', fileCover);
  await page.locator('button.done').click();

  await openPickerFor(page, 'Synthetic Series');
  await expect(page.getByTestId('cover-picker-current')).toContainText("Always this file's cover");
  await page.getByTestId('cover-pick-automatic').click();
  await page.getByTestId('cover-picker-apply').click();
  await expect.poll(async () => (await page.request.get(`/api/v1/nodes/${series.id}/cover-options`)).ok()
    && (await (await page.request.get(`/api/v1/nodes/${series.id}/cover-options`)).json()).current.mode).toBe('Automatic');
});

test('phone: the picker is full-screen with three covers per row', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const libraryId = await ensureLibrary(page);
  await page.goto(`/libraries/${libraryId}/browse`);
  await openPickerFor(page, 'Synthetic Series');
  await expect(page.locator('.cover-picker-fullscreen')).toBeVisible();
  await shot(page, 'covers-03-picker-phone');
  await page.getByTestId('cover-picker-cancel').click();
  await expect(page.getByTestId('cover-picker-current')).toHaveCount(0);
});
