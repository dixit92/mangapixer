import { test, expect, Page, APIRequestContext } from '@playwright/test';

/**
 * 1.50.0 lane S (owner decision 4): words spread over folder levels, on the owner's path - type the series and a number on the Search
 * page and get the volume whose own name has no series title. The fixture library (`e2e/fixtures/make-series-fixtures.mjs`,
 * `<E2E_SERIES_FIXTURE_ROOT>-levels`): `Berserk/Vol 12.cbz`, `Vol 13.cbz`, `Berserk/Volumes/Vol 14.cbz`, `Vinland Saga/Ch. 0106.00
 * Vol. 006.cbz`. Also a `-word` naming a folder, and quick results (through their route - the box itself is lane U's this cycle).
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const LEVELS_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-levels` : undefined;
const LIBRARY_NAME = 'Folder Levels';

test.skip(!LEVELS_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic folder-levels library available');
test.beforeEach(({ page }) => { page.setDefaultTimeout(20_000); });

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

/** Registers + scans the folder-levels library once; waits until the four archives are in the catalog. */
async function ensureLibrary(page: Page): Promise<void> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: LEVELS_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  await expect.poll(async () => {
    const res = await page.request.get(`/api/v1/search?q=${encodeURIComponent('vol')}&libraryId=${lib!.id}&pageSize=50`);
    return res.ok() ? (await res.json()).archiveCount : 0;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(4);
}

test('1.50.0: "berserk 12" finds the title-less volume inside the Berserk folder', async ({ page }) => {
  test.setTimeout(180_000);
  await login(page);
  await ensureLibrary(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  const titles = page.locator('.results-grid .result-title');

  const answered = page.waitForResponse((r) => r.url().includes('/api/v1/search?') && r.ok());
  await page.goto('/search?q=berserk%2012');
  await answered;
  await expect(titles).toHaveCount(1);
  await expect(titles.first()).toContainText('Vol 12');

  // Through a kind folder in between, and a zero-padded MangaList chapter name from a short number.
  const viaKind = page.waitForResponse((r) => r.url().includes('/api/v1/search?') && r.ok());
  await page.goto('/search?q=berserk%2014');
  await viaKind;
  await expect(titles).toHaveCount(1);
  await expect(titles.first()).toContainText('Vol 14');

  const padded = page.waitForResponse((r) => r.url().includes('/api/v1/search?') && r.ok());
  await page.goto('/search?q=vinland%206');
  await padded;
  await expect(titles).toHaveCount(1);
  await expect(titles.first()).toContainText('Vol. 006');
});

test('1.50.0: a -word naming a folder leaves out what is inside it; quick results follow both rules', async ({ page }) => {
  test.setTimeout(180_000);
  await login(page);
  await ensureLibrary(page);

  const search = async (q: string): Promise<string[]> => {
    const res = await page.request.get(`/api/v1/search?q=${encodeURIComponent(q)}&pageSize=50`);
    expect(res.ok()).toBeTruthy();
    return ((await res.json()).items as { displayName: string }[]).map((n) => n.displayName);
  };
  expect(await search('berserk 14')).toEqual(['Vol 14.cbz']);
  expect(await search('berserk 14 -volumes')).toEqual([]);
  // 1.49.0 searched FOR "berserk" here; now it is left out, and only "-" words find nothing.
  expect(await search('-berserk')).toEqual([]);

  const suggest = await (await page.request.get(`/api/v1/search/suggest?q=${encodeURIComponent('berserk 12')}`)).json();
  expect((suggest.archives as { label: string }[]).map((a) => a.label)).toContain('Vol 12.cbz');

  // The server names the series folder of each archive it lists (lane W's context line) - through a kind folder too.
  const withContext = async (q: string): Promise<(string | null)[]> => {
    const res = await page.request.get(`/api/v1/search?q=${encodeURIComponent(q)}&pageSize=50`);
    return ((await res.json()).items as { contextName?: string | null }[]).map((n) => n.contextName ?? null);
  };
  expect(await withContext('berserk 12')).toEqual(['Berserk']);
  expect(await withContext('berserk 14')).toEqual(['Berserk']);
  const quickContext = (suggest.archives as { label: string; contextName?: string | null }[]).find((a) => a.label === 'Vol 12.cbz');
  expect(quickContext?.contextName).toBe('Berserk');
});
