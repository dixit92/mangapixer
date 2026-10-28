import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen, layoutProblems } from './layout';

/**
 * Search paging + read status (1.45.0 phase K4), on the owner's path: search -> filter Unread -> Load more. 1.49.0 (lane S): the count
 * says "N folders, M archives"; reversed words find the series and a typo shows close matches. The jump-rail library
 * (`e2e/fixtures/make-series-fixtures.mjs`, `<E2E_SERIES_FIXTURE_ROOT>-jump`: `Jump Saga/Jump Saga - Ch. 001 .. 064.cbz` + a one-shot) gives
 * 66 hits for "Jump Saga" (the folder and its 65 archives) - more than one 50-result page. Five chapters are marked read first (and unmarked
 * at the end): the cards show "Read" marks; Unread then keeps 60 archives (the folder is partly read, so it is Reading), the first page
 * holds 50 of them and Load more brings the other 10, none of them marked read.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const JUMP_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-jump` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Jump Rail';

test.skip(!JUMP_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic jump-rail library available');
test.beforeEach(({ page }) => { page.setDefaultTimeout(20_000); });

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

/** The jump library (registered + scanned once, shared with jump-rail.spec.ts); returns the folder's archives in name order. */
async function ensureLibrary(page: Page): Promise<Node[]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: JUMP_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let archives: Node[] = [];
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    const folder = (root.items as Node[]).find((n) => n.displayName === 'Jump Saga')?.id;
    if (!folder) return 0;
    const inside = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?parentId=${folder}&pageSize=100&sort=name&direction=asc`)).json();
    archives = (inside.items as Node[]).filter((n) => n.kind === 'Archive');
    return archives.length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(65);
  return archives;
}

async function setRead(page: Page, ids: string[], read: boolean): Promise<void> {
  const headers = await csrf(page.request);
  for (const id of ids) {
    const res = read
      ? await page.request.put(`/api/v1/reading/${id}/read`, { headers })
      : await page.request.delete(`/api/v1/reading/${id}/read`, { headers });
    expect(res.ok(), `${read ? 'mark' : 'unmark'} ${id}: ${res.status()}`).toBeTruthy();
  }
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

test('search -> filter Unread -> Load more: every page is unread, the count and the URL follow the filter', async ({ page }) => {
  test.setTimeout(180_000);
  await login(page);
  const archives = await ensureLibrary(page);
  const read = archives.slice(0, 5).map((a) => a.id);
  await setRead(page, read, true);
  try {
    await page.setViewportSize({ width: 1280, height: 900 });
    const cards = page.locator('.results-grid .result-card');

    // Unfiltered: 66 hits, the first page of 50, the read chapters carry the browse "Read" mark.
    const first = page.waitForResponse((r) => r.url().includes('/api/v1/search?') && r.ok());
    await page.goto('/search?q=Jump%20Saga');
    await first;
    await expect(page.locator('.result-count')).toHaveText('1 folder, 65 archives');
    await expect(cards).toHaveCount(50);
    await expect(page.locator('.results-grid .badge.read')).not.toHaveCount(0);
    await shot(page, 'search-read-1280-all');

    // Filter Unread: sent to the server, written to the URL, count and page follow.
    const filtered = page.waitForResponse((r) => r.url().includes('readState=unread') && r.ok());
    await page.locator('.read-filter').getByRole('option', { name: 'Unread' }).click();
    await filtered;
    await expect(page).toHaveURL(/[?&]read=unread/);
    await expect(page).toHaveURL(/[?&]q=Jump(%20|\+)Saga/);
    await expect(page.locator('.result-count')).toHaveText('60 archives');
    await expect(cards).toHaveCount(50);
    await expect(page.locator('.results-grid .badge')).toHaveCount(0);

    // Load more: the next keyset page with the same filter - the last 10, none read, then no more button.
    const more = page.waitForResponse((r) => r.url().includes('cursor=') && r.url().includes('readState=unread') && r.ok());
    await page.getByRole('button', { name: 'Load more' }).click();
    await more;
    await expect(cards).toHaveCount(60);
    await expect(page.locator('.results-grid .badge')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Load more' })).toHaveCount(0);
    const titles = await page.locator('.results-grid .result-title').allTextContents();
    expect(new Set(titles).size).toBe(60);
    for (const r of archives.slice(0, 5)) expect(titles).not.toContain(r.displayName);
    await shot(page, 'search-read-1280-unread-all-loaded');

    // A reload opens the same filtered search (the URL is the state).
    await page.reload();
    await expect(page.locator('.result-count')).toHaveText('60 archives');
    await expect(page.locator('.read-filter').getByRole('option', { name: 'Unread' })).toHaveAttribute('aria-selected', 'true');

    // The chip row fits a phone.
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.locator('.read-filter')).toBeVisible();
    await expectFitsScreen(page);
    await shot(page, 'search-read-390-unread');
  } finally {
    await setRead(page, read, false);
  }
});

test('1.49.0: reversed words find the series, a typo shows close matches with a note, a 2-letter search explains itself', async ({ page }) => {
  test.setTimeout(180_000);
  await login(page);
  await ensureLibrary(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  const cards = page.locator('.results-grid .result-card');

  // The words in the other order: one literal phrase before 1.49.0 found nothing.
  let answer = page.waitForResponse((r) => r.url().includes('/api/v1/search?') && r.ok());
  await page.goto('/search?q=saga%20jump');
  await answer;
  await expect(page.locator('.result-count')).toHaveText('1 folder, 65 archives');
  await expect(cards.first().locator('.result-title')).toHaveText('Jump Saga');
  await expect(page.getByTestId('search-close-hint')).toHaveCount(0);

  // A swapped letter: close matches only, said so.
  answer = page.waitForResponse((r) => r.url().includes('/api/v1/search?') && new URL(r.url()).searchParams.get('q') === 'jump sgaa' && r.ok());
  await page.getByTestId('search-input').fill('jump sgaa');
  await answer;
  await expect(page.getByTestId('search-close-hint')).toHaveText('No exact matches. Showing close matches for "jump sgaa".');
  await expect(cards.first().locator('.result-title')).toHaveText('Jump Saga');

  // Two letters: names with a word starting with them, and a note.
  answer = page.waitForResponse((r) => r.url().includes('/api/v1/search?') && new URL(r.url()).searchParams.get('q') === 'ju' && r.ok());
  await page.getByTestId('search-input').fill('ju');
  await answer;
  await expect(page.getByTestId('search-short-hint')).toContainText('Names with a word starting with "ju"');
  // Among the "ju" names (other fixture folders start with "Jump" too - the jump-rail doujin folder sorts first), the series is listed.
  await expect(cards.locator('.result-title').filter({ hasText: /^Jump Saga$/ })).toHaveCount(1);

  await page.setViewportSize({ width: 390, height: 844 });
  // The shell switches to its phone layout a moment after a resize (BreakpointObserver); measure once it has settled.
  await expect.poll(async () => (await layoutProblems(page)).length, { timeout: 5_000 }).toBe(0);
  await expectFitsScreen(page, 'the Search page after a 2-letter search (phone)');
  await shot(page, 'search-words-390-short');
});
