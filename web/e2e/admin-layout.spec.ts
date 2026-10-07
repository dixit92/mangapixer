import { test, expect, Page, APIRequestContext } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * Administration layout (1.32.0): the cards sit in a responsive grid like Metadata Manager - more than one column on a desktop,
 * one column below 900 px, nothing scrolling sideways at any width - and automatic trash cleaning is switched on and timed in
 * the Scheduled jobs card only (the Trash card shows its status and links there). Phone and tablet widths are also covered by
 * the sweep in phone-layout.spec.ts; this spec adds the desktop widths and the column count.
 * Optional: E2E_SCREENSHOT_DIR saves one screenshot per width.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const VOLUMES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-volumes` : undefined;
const LIBRARY_NAME = 'Volume Stacks';

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

/** The distinct left edges of the grid's columns (the two `.col`s share one edge when the grid is one column). */
async function columnEdges(page: Page): Promise<number[]> {
  return page.getByTestId('admin-grid').locator(':scope > .col').evaluateAll((cols) =>
    [...new Set(cols.map((c) => Math.round(c.getBoundingClientRect().left)))]);
}

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get('/api/v1/auth/csrf');
  const { token } = await res.json();
  return { 'X-MangaPixer-Csrf': token };
}

/** The synthetic volume library (registered by whichever spec runs first); its id. */
async function ensureLibrary(page: Page): Promise<string> {
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  const found = libs.find((l) => l.name === LIBRARY_NAME);
  if (found) return found.id;
  const created = await page.request.post('/api/v1/admin/libraries', {
    headers: await csrf(page.request), data: { displayName: LIBRARY_NAME, rootPath: VOLUMES_ROOT },
  });
  expect(created.ok(), await created.text()).toBeTruthy();
  return (await created.json()).id;
}

async function openAdmin(page: Page, width: number, height: number): Promise<void> {
  await page.setViewportSize({ width, height });
  await page.goto('/admin');
  await expect(page.getByTestId('trash-card')).toBeVisible();
  await expect(page.getByTestId('scheduled-jobs')).toBeVisible();
  await expect(page.getByTestId('job-trash')).toBeVisible();
  await page.waitForTimeout(500);
}

test.describe('Administration layout', () => {
  test.beforeEach(async ({ page }) => { await login(page); });

  for (const [width, height, columns] of [[1920, 1080, 2], [1280, 900, 2], [900, 1000, 2], [820, 1180, 1], [390, 844, 1]] as const) {
    test(`${width} px: ${columns === 1 ? 'one column' : 'more than one column'}, fits the screen`, async ({ page }) => {
      await openAdmin(page, width, height);
      const edges = await columnEdges(page);
      expect(edges.length, `column left edges at ${width} px: ${edges.join(', ')}`).toBe(columns);
      await expectFitsScreen(page, `Administration at ${width} px`);
      if (SHOTS) await page.screenshot({ path: `${SHOTS}/admin-${width}.png`, fullPage: true });
    });
  }

  test('automatic trash cleaning is switched in Scheduled jobs; the Trash card only shows its status', async ({ page }) => {
    await openAdmin(page, 1280, 900);
    const trash = page.getByTestId('trash-card');
    await expect(trash.getByTestId('trash-auto')).toHaveCount(0);
    await expect(trash.getByTestId('trash-hour')).toHaveCount(0);
    await expect(trash.getByTestId('trash-auto-status')).toContainText('Automatic cleaning: Off');

    const row = page.getByTestId('job-trash');
    await expect(row.getByTestId('job-trash-auto')).toBeVisible();
    await expect(row.getByTestId('job-hour-trash')).toBeVisible();

    // The status line's link moves to the switch.
    await trash.getByTestId('trash-auto-link').click();
    await expect(row.getByTestId('job-trash-auto').locator('button')).toBeFocused();

    // Turning it on asks first; cancelling leaves it off.
    await row.getByTestId('job-trash-auto').locator('button').click();
    const confirm = row.getByTestId('job-trash-confirm');
    await expect(confirm).toContainText('Turn automatic cleaning on?');
    await row.getByTestId('job-trash-confirm-no').click();
    await expect(confirm).toHaveCount(0);
    await expect(trash.getByTestId('trash-auto-status')).toContainText('Automatic cleaning: Off');

    // On, then off again (leaves the instance as it was): the Trash card follows without a reload.
    await row.getByTestId('job-trash-auto').locator('button').click();
    const saved = page.waitForResponse((r) => r.url().endsWith('/api/v1/admin/trash/settings') && r.request().method() === 'PUT');
    await row.getByTestId('job-trash-confirm-yes').click();
    expect((await saved).ok()).toBeTruthy();
    await expect(trash.getByTestId('trash-auto-status')).toContainText('Automatic cleaning: daily at');
    const off = page.waitForResponse((r) => r.url().endsWith('/api/v1/admin/trash/settings') && r.request().method() === 'PUT');
    await row.getByTestId('job-trash-auto').locator('button').click();
    expect((await off).ok()).toBeTruthy();
    await expect(trash.getByTestId('trash-auto-status')).toContainText('Automatic cleaning: Off');
  });

  test('a library scan schedule is changed in Scheduled jobs; the Libraries card shows it with a link (1.33.0)', async ({ page }) => {
    test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic volume library available');
    const libraryId = await ensureLibrary(page);
    await openAdmin(page, 1280, 900);
    const summary = page.getByTestId('scan-summary').first();
    await expect(summary).toContainText('Auto-scan:');
    // Every schedule control on the page is inside Scheduled jobs (one per library row), none on the Libraries card.
    const controls = await page.getByLabel('Automatic scan schedule').count();
    await expect(page.getByTestId('scheduled-jobs').getByLabel('Automatic scan schedule')).toHaveCount(controls);

    const row = page.getByTestId('job-library-scan-' + libraryId);
    await expect(row.getByLabel('Automatic scan schedule')).toBeVisible();
    await page.getByTestId('scan-schedule-link').first().click();
    await expect(page.locator('[data-testid^="job-library-scan-"] select:focus')).toHaveCount(1);
  });
});
