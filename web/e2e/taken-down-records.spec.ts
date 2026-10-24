import { test, expect, Page } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * 1.47.0 (owner decision 10a): "MangaDex records taken down" on the Runs tab of Metadata Manager - a count and a list of the records
 * the latest-upload sweep stopped finding, with since-when, from stored rows only. A real server has none (the sweep needs the
 * network), so the answer is a contract-shaped `taken-down` response served in the browser (the server side is covered by
 * `TakenDownRecordsHttpTests`). The page asks MangaPixer only. Checked at 1280 and 390 px; with no record missing nothing is drawn.
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

const serve = (page: Page, body: unknown) => page.route('**/api/v1/admin/metadata/taken-down', (r) => r.fulfill({
  contentType: 'application/json', body: JSON.stringify(body),
}));

const answer = {
  total: 3,
  limit: 200,
  items: [
    {
      recordId: 'tdr1', title: 'A Synthetic Series With A Rather Long Title That Has To Wrap On A Phone Screen', since: '2026-10-04T06:00:00Z',
      lastSeen: '2026-10-03T06:00:00Z', sweeps: 7,
      series: [
        { nodeId: 'tdn1', libraryId: 'tdl1', displayName: 'Synthetic Shelf One' },
        { nodeId: 'tdn2', libraryId: 'tdl1', displayName: 'Synthetic Shelf Twin With A Longer Folder Name' },
      ],
      moreSeries: 1,
    },
    { recordId: 'tdr2', title: 'Another Takedown', since: '2026-10-12T06:00:00Z', sweeps: 1, series: [{ nodeId: 'tdn3', libraryId: 'tdl2', displayName: 'Shelf Three' }] },
  ],
};

for (const [label, width, height] of [['desktop', 1280, 900], ['phone', 390, 844]] as const) {
  test(`the Runs tab lists the MangaDex records taken down, from stored data only (${label})`, async ({ page, baseURL }) => {
    await page.setViewportSize({ width, height });
    const origin = new URL(baseURL!).origin;
    const foreign: string[] = [];
    page.on('request', (r) => { if (!r.url().startsWith('data:') && new URL(r.url()).origin !== origin) foreign.push(r.url()); });
    await serve(page, answer);
    await login(page);
    await page.goto('/admin/metadata?tab=runs');

    const card = page.getByTestId('taken-down');
    await expect(card).toBeVisible();
    await expect(page.getByTestId('taken-down-count')).toHaveText('3');
    const rows = page.getByTestId('taken-down-row');
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toContainText('Synthetic Shelf One');
    await expect(rows.nth(0)).toContainText('and 1 more');
    await expect(rows.nth(0).getByTestId('taken-down-since')).toContainText('7 checks in a row');
    await expect(card).toContainText('Showing the 2 oldest of 3.');
    // A series name opens its folder in the library.
    await expect(rows.nth(0).getByRole('link', { name: 'Synthetic Shelf One' })).toHaveAttribute('href', '/libraries/tdl1/browse/tdn1');
    await expect(page.getByTestId('metadata-runs')).toBeVisible();
    await expectFitsScreen(page, 'the Runs tab with taken-down records');
    if (SHOTS) {
      await page.waitForTimeout(500);
      await page.screenshot({ path: `${SHOTS}/taken-down-${label}.png`, fullPage: true });
    }
    expect(foreign).toEqual([]);
  });
}

test('nothing is drawn when no record is missing', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await serve(page, { total: 0, limit: 200, items: [] });
  await login(page);
  await page.goto('/admin/metadata?tab=runs');
  await expect(page.getByTestId('metadata-runs')).toBeVisible();
  await expect(page.getByTestId('taken-down')).toHaveCount(0);
});
