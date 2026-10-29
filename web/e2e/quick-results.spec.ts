import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Quick results while typing (1.49.0, lane O; owner decision 7), on the owner's path: type in the header search box -> grouped results ->
 * Enter opens an item; a typed field suggests values and completes the token; on a phone the Search page shows the same groups under its
 * field (the header has no box there). The jump-rail library (`e2e/fixtures/make-series-fixtures.mjs`, `<E2E_SERIES_FIXTURE_ROOT>-jump`:
 * `Jump Saga/Jump Saga - Ch. 001 .. 064.cbz` + a one-shot) holds the folder "Jump Saga" and its 65 archives, registered as "Jump Rail".
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const JUMP_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-jump` : undefined;
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

/** The jump library (registered + scanned once, shared with jump-rail.spec.ts); returns [library id, the Jump Saga folder's id]. */
async function ensureLibrary(page: Page): Promise<[string, string]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: JUMP_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folder = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = ((root.items as Node[]).find((n) => n.displayName === 'Jump Saga')?.id) ?? '';
    if (!folder) return 0;
    const inside = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?parentId=${folder}&pageSize=100`)).json();
    return (inside.items as Node[]).filter((n) => n.kind === 'Archive').length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(65);
  return [lib!.id, folder];
}

test('header box: type -> grouped results -> Down + Enter opens the folder; Enter alone opens the Search page', async ({ page }) => {
  test.setTimeout(180_000);
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/libraries');

  const box = page.getByTestId('header-search-input');
  await expect(box).toHaveAttribute('role', 'combobox');
  await expect(box).toHaveAttribute('aria-expanded', 'false');

  // One request after the typing pauses, never one per keystroke.
  let asked = 0;
  page.on('request', (r) => { if (r.url().includes('/api/v1/search/suggest')) asked++; });
  const answered = page.waitForResponse((r) => r.url().includes('/api/v1/search/suggest') && r.ok());
  await box.click();
  await box.pressSequentially('jump sa', { delay: 20 });
  await answered;

  const panel = page.getByTestId('header-search-panel');
  await expect(panel).toBeVisible();
  await expect(box).toHaveAttribute('aria-expanded', 'true');
  expect(asked).toBeLessThanOrEqual(2); // debounced: not one request per keystroke (7 characters)
  // Groups: the folder first, then five archives (named after the series, found by their own names).
  await expect(panel.locator('[data-group="folders"] [role="option"]')).toHaveText([/Jump Saga/]);
  await expect(panel.locator('[data-group="archives"] [role="option"]')).toHaveCount(5);
  // 1.50.0: the folder an archive sits in is its context line now (lane W's line, filled by the server since lane S).
  await expect(panel.locator('[data-group="archives"] [data-testid="context-line"]').first()).toHaveText('Jump Saga');

  // Down chooses the first result (the folder); aria-activedescendant names it.
  await box.press('ArrowDown');
  const active = panel.locator('[role="option"][aria-selected="true"]');
  await expect(active).toHaveCount(1);
  await expect(active).toContainText('Jump Saga');
  await expect(box).toHaveAttribute('aria-activedescendant', (await active.getAttribute('id')) ?? 'none');
  await box.press('Enter');
  await expect(page).toHaveURL(new RegExp(`/libraries/${libraryId}/browse/${folderId}$`));
  await expect(box).toHaveValue('');

  // Enter with nothing chosen opens the full Search page with the text.
  await box.click();
  await box.pressSequentially('jump saga', { delay: 10 });
  await box.press('Enter');
  await expect(page).toHaveURL(/\/search\?q=jump(%20|\+)saga$/);
  await expect(page.locator('.result-count')).toHaveText('1 folder, 65 archives');
});

test('header box: a typed field suggests its values and a pick completes the token; Escape closes the list', async ({ page }) => {
  test.setTimeout(180_000);
  await login(page);
  await ensureLibrary(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/libraries');

  const box = page.getByTestId('header-search-input');
  const panel = page.getByTestId('header-search-panel');
  await box.click();
  await box.pressSequentially('jump library:jum', { delay: 20 });
  await expect(panel).toBeVisible();
  await expect(panel.locator('.qr-heading')).toHaveText('Libraries');
  await expect(panel.locator('[role="option"]')).toHaveText([/Jump Rail/]);

  await box.press('ArrowDown');
  await box.press('Enter');
  await expect(box).toHaveValue('jump library:"Jump Rail" ');
  // Choosing a value completes the token; it does not leave the page.
  await expect(page).toHaveURL(/\/libraries$/);

  await box.press('Escape');
  await expect(panel).toBeHidden();
  await expect(box).toHaveValue('jump library:"Jump Rail" ');
});

test('phone: no header box; the Search page shows the grouped quick results under its field', async ({ page }) => {
  test.setTimeout(180_000);
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/search');
  await expect(page.getByTestId('header-search-input')).toHaveCount(0);

  const answered = page.waitForResponse((r) => r.url().includes('/api/v1/search/suggest') && r.ok());
  await page.getByTestId('search-input').click();
  await page.getByTestId('search-input').pressSequentially('jump sa', { delay: 20 });
  await answered;

  const quick = page.getByTestId('search-quick');
  await expect(quick).toBeVisible();
  await expect(quick.locator('[data-group="folders"] [role="option"]')).toHaveText([/Jump Saga/]);
  await expect(quick.locator('[data-group="archives"] [role="option"]')).toHaveCount(5);
  await expectFitsScreen(page, 'Search page with quick results at 390');

  await quick.locator('[data-group="folders"] [role="option"]').click();
  await expect(page).toHaveURL(new RegExp(`/libraries/${libraryId}/browse/${folderId}$`));
});
