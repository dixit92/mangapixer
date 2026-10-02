import { test, expect, Page, APIRequestContext } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * The per-folder cover preference (1.32.0) in a real browser against a real server: an admin selects one folder in the library,
 * opens "Folder covers..." from the selection bar, chooses File covers and saves; the server stores it (and the dialog shows it
 * again), then Inherit removes it. The dialog and the selection bar fit a phone (390 px) and a tablet (820 px) screen. Local data
 * only: no provider request.
 *
 * Needs the synthetic fixture library from `e2e/fixtures/make-series-fixtures.mjs` visible to the SERVER at
 * E2E_SERIES_FIXTURE_ROOT (as `covers.spec.ts`); without it the suite skips.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const LIBRARY_NAME = 'Series Fixtures';
const SIZES = [{ width: 390, height: 844 }, { width: 820, height: 1180 }];

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
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

/** The fixture library (registered by whichever spec runs first) and its "Synthetic Series" folder: [libraryId, folderId]. */
async function ensureLibrary(page: Page): Promise<[string, string]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: FIXTURE_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folder = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = ((root.items as Node[]).find((n) => n.displayName === 'Synthetic Series')?.id) ?? '';
    return folder;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).not.toBe('');
  return [lib!.id, folder];
}

async function preference(page: Page, folderId: string): Promise<{ preference?: string | null; effective: string }> {
  return await (await page.request.get(`/api/v1/admin/folders/${folderId}/cover-preference`)).json();
}

async function openDialog(page: Page, libraryId: string): Promise<void> {
  await page.goto(`/libraries/${libraryId}/browse`);
  await page.locator('button.select-toggle').click();
  await page.locator('.node-wrap', { hasText: 'Synthetic Series' }).locator('.node-card').click();
  await page.getByTestId('folder-cover-preference-action').click();
  await expect(page.getByTestId('folder-cover-choices')).toBeVisible();
}

test('an admin sets File covers on a folder from the selection bar, sees it again, and clears it', async ({ page }) => {
  test.setTimeout(180_000);
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  const headers = await csrf(page.request);
  await page.request.delete(`/api/v1/admin/folders/${folderId}/cover-preference`, { headers });

  await openDialog(page, libraryId);
  await expect(page.getByTestId('folder-cover-inherit')).toContainText("Inherit (Web covers when available - the library's setting)");
  await page.getByTestId('folder-cover-file').click();
  const saved = page.waitForResponse((r) => r.url().endsWith(`/admin/folders/${folderId}/cover-preference`) && r.request().method() === 'PUT');
  await page.getByTestId('folder-cover-save').click();
  expect((await saved).ok()).toBeTruthy();
  await expect(page.getByTestId('folder-cover-choices')).toHaveCount(0);
  expect(await preference(page, folderId)).toMatchObject({ preference: 'File', effective: 'File' });

  // Reopened, the dialog shows the stored value; Inherit removes it.
  await openDialog(page, libraryId);
  await expect(page.getByTestId('folder-cover-file').locator('input')).toBeChecked();
  await page.getByTestId('folder-cover-inherit').click();
  const cleared = page.waitForResponse((r) => r.url().endsWith(`/admin/folders/${folderId}/cover-preference`) && r.request().method() === 'DELETE');
  await page.getByTestId('folder-cover-save').click();
  expect((await cleared).ok()).toBeTruthy();
  expect((await preference(page, folderId)).preference ?? null).toBeNull();
});

test('the folder covers dialog and the selection bar fit a phone and a tablet screen', async ({ page }) => {
  test.setTimeout(180_000);
  await login(page);
  const [libraryId] = await ensureLibrary(page);
  for (const size of SIZES) {
    await page.setViewportSize(size);
    await openDialog(page, libraryId);
    await expectFitsScreen(page, `${size.width} px folder covers dialog`);
    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(page.getByTestId('folder-cover-choices')).toHaveCount(0);
    await expectFitsScreen(page, `${size.width} px selection bar with Folder covers`);
  }
});
