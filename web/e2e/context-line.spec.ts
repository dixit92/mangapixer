import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Archive context line (1.50.0, lane W): a title-less archive found outside its folder says which folder it belongs to. The context
 * library (`e2e/fixtures/make-series-fixtures.mjs`, `<E2E_SERIES_FIXTURE_ROOT>-context`, registered as "Context Line") holds three archives
 * whose names carry no series title but one distinctive word, "Quillfeather":
 *   Context Saga/Vol. 012 Quillfeather.cbz                -> context line "Context Saga"
 *   Context Saga/Volumes/Vol. 013 Quillfeather Two.cbz    -> "Context Saga" (the plain "Volumes" kind folder is skipped)
 *   Quillfeather Oneshot.cbz (library root)               -> no line
 * Membership is asserted, never position. The search page's cards carry `contextName` once lane S calls the helper in the search
 * (merge order W -> ... -> S): until then this spec fills a MISSING `contextName` on the search answer from the fixture's known layout
 * (the annotation `context-from` says which side supplied it) - the card, the muted line, the tooltip and the phone fit are what is proved here;
 * the server's own answer is proved by the HTTP tests (Continue reading) and, after the merge, by this spec with no injection. Home's
 * Continue reading card is checked with a mocked entry (a long folder name) for the phone fit.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const CONTEXT_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-context` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Context Line';
const SAGA_VOLUME = 'Vol. 012 Quillfeather.cbz';
const NESTED_VOLUME = 'Vol. 013 Quillfeather Two.cbz';
const ROOT_ARCHIVE = 'Quillfeather Oneshot.cbz';
const KNOWN_CONTEXT: Record<string, string> = { [SAGA_VOLUME]: 'Context Saga', [NESTED_VOLUME]: 'Context Saga' };

test.skip(!CONTEXT_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic context library available');
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

/** Registers + scans the context library once, then waits until the search finds all three archives. */
async function ensureLibrary(page: Page): Promise<void> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  if (!libs.some((l) => l.name === LIBRARY_NAME)) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: CONTEXT_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    const lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib.id}/scan`, { headers });
  }
  await expect.poll(async () => {
    const res = await page.request.get('/api/v1/search?q=Quillfeather&pageSize=50');
    const body = await res.json();
    return (body.items as { kind: string }[]).filter((n) => n.kind === 'Archive').length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(3);
}

/** Fills a missing `contextName` on archive results from the known fixture layout; returns how many it had to supply. */
async function fillMissingContext(page: Page): Promise<{ injected: () => number }> {
  let injected = 0;
  await page.route(/\/api\/v1\/search\?/, async (route) => {
    const response = await route.fetch();
    const body = await response.json();
    for (const item of (body.items ?? []) as { kind: string; displayName: string; contextName?: string | null }[]) {
      if (item.kind === 'Archive' && !item.contextName && KNOWN_CONTEXT[item.displayName]) {
        item.contextName = KNOWN_CONTEXT[item.displayName];
        injected++;
      }
    }
    await route.fulfill({ response, json: body });
  });
  return { injected: () => injected };
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

for (const size of [{ width: 390, height: 844 }, { width: 1280, height: 900 }]) {
  test(`search results: title-less archives name their folder, a root archive has no line (${size.width} px)`, async ({ page }) => {
    test.setTimeout(180_000);
    await login(page);
    await ensureLibrary(page);
    const filled = await fillMissingContext(page);
    await page.setViewportSize(size);

    const first = page.waitForResponse((r) => r.url().includes('/api/v1/search?') && r.ok());
    await page.goto('/search?q=Quillfeather');
    await first;

    const cards = page.locator('.results-grid .result-card');
    await expect(cards).toHaveCount(3);
    const card = (title: string) => cards.filter({ has: page.locator('.result-title', { hasText: new RegExp(`^\\s*${title.replace(/\./g, '\\.')}\\s*$`) }) });

    // Membership: each archive's own card, with its folder under the title (the nested one skips the plain "Volumes" folder).
    for (const title of [SAGA_VOLUME, NESTED_VOLUME]) {
      const line = card(title).getByTestId('context-line');
      await expect(line).toHaveText('Context Saga');
      await expect(line).toHaveAttribute('title', 'Context Saga');
    }
    // The root archive has no folder: no line.
    await expect(card(ROOT_ARCHIVE)).toHaveCount(1);
    await expect(card(ROOT_ARCHIVE).getByTestId('context-line')).toHaveCount(0);
    await expect(page.getByTestId('context-line')).toHaveCount(2);

    test.info().annotations.push({ type: 'context-from', description: filled.injected() > 0 ? `spec (filled ${filled.injected()})` : 'server' });
    await shot(page, `context-line-search-${size.width}`);
    if (size.width <= 500) await expectFitsScreen(page, 'search results with context lines');
  });
}

test('Continue reading card: a long folder name stays on one line and fits a phone screen', async ({ page }) => {
  test.setTimeout(120_000);
  await login(page);
  const longName = 'An Extraordinarily Long Series Folder Name That Cannot Possibly Fit On One Line Of A Phone Card';
  await page.route(/\/api\/v1\/reading\/continue(\?|$)/, (route) => route.fulfill({
    json: [{
      itemId: 'ctxe2e1', libraryId: 'ctxe2elib', libraryName: 'Context Line', displayName: 'Vol. 012', pageIndex: 2, contentVersion: 1,
      updatedAt: '2026-10-28T00:00:00Z', contextName: longName,
    }, {
      itemId: 'ctxe2e2', libraryId: 'ctxe2elib', libraryName: 'Context Line', displayName: 'Loose Oneshot', pageIndex: 0, contentVersion: 1,
      updatedAt: '2026-10-28T00:00:00Z', contextName: null,
    }],
  }));
  for (const size of [{ width: 390, height: 844 }, { width: 1280, height: 900 }]) {
    await page.setViewportSize(size);
    await page.goto('/');
    const cards = page.locator('.cont-wrap');
    await expect(cards).toHaveCount(2);
    const line = cards.nth(0).getByTestId('context-line');
    await expect(line).toHaveText(longName);
    await expect(line).toHaveAttribute('title', longName);
    await expect(cards.nth(1).getByTestId('context-line')).toHaveCount(0);
    // One line: the same height as a single text line, and ellipsized (its text is wider than the box).
    const clipped = await line.evaluate((el) => ({ h: el.getBoundingClientRect().height, truncated: el.scrollWidth > el.clientWidth }));
    expect(clipped.h).toBeLessThan(24);
    expect(clipped.truncated).toBe(true);
    await shot(page, `context-line-home-${size.width}`);
    if (size.width <= 500) await expectFitsScreen(page, 'Home with a Continue reading context line');
  }
});
