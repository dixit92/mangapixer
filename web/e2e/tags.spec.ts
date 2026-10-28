import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Tags that combine (1.49.0, lane T; owner decision 9 a), on the real tag index of the series library of
 * `e2e/fixtures/make-series-fixtures.mjs` (`Synthetic Series`: ComicInfo series "Synthetic Saga", genres Action / Fantasy, years 2020-2022).
 * The owner's path: a plain search -> the (i) of a result -> a genre chip ADDS its tag to the search (the text stays) -> the chip is
 * switched to "Not" (exclude) -> Refine excludes the other genre -> typed `-genre:` / `|` / `year:a..b`. No mocked routes.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic ComicInfo library available');
test.describe.configure({ mode: 'serial' });

interface Node { id: string; displayName: string; kind: string; hasSeriesInfo?: boolean }

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

/** The series library (registered + scanned once, shared with info-panel.spec.ts / series-info.spec.ts). */
async function ensureLibrary(page: Page): Promise<void> {
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
    return (root.items as Node[]).find((n) => n.displayName === 'Synthetic Series')?.hasSeriesInfo === true;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(true);
  // The tag index builds in the background: wait until the typed genre finds the work.
  await expect.poll(async () => {
    const res = await (await page.request.get('/api/v1/search?q=' + encodeURIComponent('genre:fantasy'))).json();
    return (res.works ?? []).some((w: { title: string }) => w.title === 'Synthetic Saga');
  }, { timeout: 120_000, intervals: [2000, 5000] }).toBe(true);
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/tags-${name}.png`, fullPage: true });
}

const work = (page: Page) => page.getByTestId('work-card').filter({ hasText: 'Synthetic Saga' });

test('an Info chip opened over a search ADDS its tag (the text stays); a chip and Refine exclude a genre', async ({ page }) => {
  test.setTimeout(240_000);
  await login(page);
  await ensureLibrary(page);

  await page.goto('/search?q=synthetic');
  const card = page.locator('.result-card').filter({ hasText: 'Synthetic Series' }).first();
  await expect(card).toBeVisible();
  await card.getByTestId('info-toggle').click();
  const overlay = page.getByTestId('series-overlay');
  const fantasy = overlay.locator('[data-testid="info-bucket"][data-bucket="Genres"]').getByTestId('info-chip').filter({ hasText: 'Fantasy' });
  await expect(fantasy).toBeVisible();
  await fantasy.click();

  // Added, not replaced: the text is still in the URL and the field, and the genre is a chip next to it.
  await expect(overlay).toHaveCount(0);
  await expect(page).toHaveURL(/[?&]q=synthetic(&|$)/);
  await expect(page).toHaveURL(/[?&]f=genre(:|%3A)fv_/);
  await expect(page.getByTestId('search-input')).toHaveValue('synthetic');
  const chip = page.getByTestId('search-filter-chip');
  await expect(chip).toHaveCount(1);
  await expect(chip).toContainText('Genres: Fantasy');
  await expect(work(page)).toBeVisible();
  await expect(page.getByTestId('search-refine')).toBeVisible();
  await shot(page, '01-added');

  // The chip's toggle excludes the genre: the only work carries it, so nothing is left.
  await chip.getByTestId('search-filter-exclude').click();
  await expect(chip).toHaveAttribute('data-excluded', 'true');
  await expect(chip).toContainText('Not Genres: Fantasy');
  await expect(page).toHaveURL(/[?&]f=-genre(:|%3A)fv_/);
  await expect(page.locator('.no-results')).toHaveText('No works with these tags.');

  // Back to "must have"; then Refine's minus excludes the work's other genre.
  await chip.getByTestId('search-filter-exclude').click();
  await expect(work(page)).toBeVisible();
  const action = page.getByTestId('search-refine').locator('.refine-chip').filter({ hasText: 'Action' });
  await expect(action.locator('.count')).toHaveText('1');
  await action.getByTestId('refine-not').click();
  await expect(page.getByTestId('search-filter-chip')).toHaveCount(2);
  await expect(page.getByTestId('search-filter-chip').nth(1)).toContainText('Not Genres: Action');
  await expect(page.locator('.no-results')).toHaveText('No works with these tags.');
  await expect(page.getByTestId('search-input')).toHaveValue('synthetic');
  await expectFitsScreen(page, 'tag search with an exclusion');
});

test('typed syntax: -genre, any-of with |, a year range; the "?" lists it', async ({ page }) => {
  test.setTimeout(240_000);
  await login(page);
  await ensureLibrary(page);

  await page.goto('/search?q=' + encodeURIComponent('synthetic -genre:action'));
  await expect(page.locator('.no-results')).toHaveText('No works with these tags.');
  await page.goto('/search?q=' + encodeURIComponent('genre:horror|fantasy'));
  await expect(work(page)).toBeVisible();
  await page.goto('/search?q=' + encodeURIComponent('year:2015..2025'));
  await expect(work(page)).toBeVisible();
  await page.goto('/search?q=' + encodeURIComponent('year:..1999'));
  await expect(page.locator('.no-results')).toHaveText('No works with these tags.');

  await page.getByTestId('search-syntax-toggle').click();
  const help = page.getByTestId('search-syntax');
  await expect(help).toBeVisible();
  await expect(help.getByTestId('search-syntax-example').filter({ hasText: '-genre:horror' })).toBeVisible();
  await expectFitsScreen(page, 'search syntax help');
  await shot(page, '02-syntax');
});

test('phone: the Refine list and the syntax help fit, and a Refine chip is tappable', async ({ page }) => {
  test.setTimeout(240_000);
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  await ensureLibrary(page);

  await page.goto('/search?q=' + encodeURIComponent('genre:fantasy'));
  await expect(work(page)).toBeVisible();
  await expect(page.getByTestId('search-refine')).toBeVisible();
  await page.getByTestId('search-syntax-toggle').click();
  await expect(page.getByTestId('search-syntax')).toBeVisible();
  await expectFitsScreen(page, 'refine + syntax on a phone');
  const add = page.getByTestId('refine-chip').first();
  const box = await add.boundingBox();
  expect(Math.round(box!.height)).toBeGreaterThanOrEqual(30);
  await shot(page, '03-phone');
});
