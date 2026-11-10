import { test, expect, Page, APIRequestContext } from '@playwright/test';

/**
 * 1.53.0 pre-cut fixes (lane F), from the visual change guide's findings on the cut candidate, in a real browser on the synthetic
 * ComicInfo library ("Series Fixtures", the shared-ratings spec's library):
 * - finding 2: the selection bar is one band again at 820 - 1440 px - "N selected" left, the actions (wrapping only when they do not
 *   fit), Done right - never the count or Done on a line of their own (V6's general wrap rule had reached the selecting bar);
 * - finding 3: a library page shows the first-answer spinner (V2's, after 300 ms) also while it waits for its view settings, not only
 *   for the folder request;
 * - finding 4: a trail that cannot fit ends with "..." (drawn, not a hard cut) and always shows the folder you are in, on a touch tablet
 *   (820 px) and at 1280 px with "Rate" in the bar - with a very long library name and ancestor served in the browser.
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots (dark).
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Series Fixtures';
const PLAIN = 'Plain Folder';
/** The guide's 61-letter library name. */
const LONG_LIBRARY = 'Jump Rail - A Synthetic Shelf With A Rather Long Library Name';
const LONG_ANCESTOR = 'An Ancestor Folder Whose Name Is Also Far Too Long For One Bar';

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic ComicInfo library available');

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

/** Registers + scans the fixture library once (whichever spec runs first); returns the library id and the plain folder's id. */
async function ensureFixture(page: Page): Promise<{ libraryId: string; plain: string }> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: FIXTURE_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let plain = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50&sort=name`)).json();
    const items = root.items as Node[];
    plain = items.find((n) => n.displayName === PLAIN)?.id ?? '';
    return !!plain && items.filter((n) => n.kind === 'Folder').length >= 3;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(true);
  return { libraryId: lib!.id, plain };
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${SHOTS}/f-${name}-${page.viewportSize()?.width ?? 0}-dark.png` });
}

test.describe('the selection bar is one band on a tablet and a desktop (guide finding 2)', () => {
  for (const viewport of [{ width: 820, height: 1180 }, { width: 1024, height: 768 }, { width: 1280, height: 900 }, { width: 1440, height: 900 }]) {
    test(`${viewport.width} px: "N selected" left, the actions, Done right - with "Rate..." in the bar`, async ({ page }) => {
      test.setTimeout(150_000);
      await page.setViewportSize(viewport);
      await login(page);
      const { libraryId } = await ensureFixture(page);
      await page.goto(`/libraries/${libraryId}/browse`);
      await expect(page.locator('.node-wrap').nth(2)).toBeVisible();
      await page.locator('button.select-toggle').click();
      for (const n of [0, 1, 2]) await page.locator('.node-wrap').nth(n).locator('.node-card').click();
      await expect(page.getByTestId('selection-count')).toHaveText('3 selected');
      await expect(page.getByTestId('selection-rate')).toBeVisible();
      const bar = await page.locator('.browse-bar.selecting').evaluate((el) => {
        const box = (s: string) => el.querySelector(s)!.getBoundingClientRect();
        const count = box('[data-testid="selection-count"]');
        const actions = box('app-selection-bar .actions');
        const done = box('[data-testid="selection-done"]');
        const beside = (a: DOMRect) => a.top < actions.bottom && a.bottom > actions.top;
        return {
          countLeft: count.right <= actions.left + 1 && beside(count),
          doneRight: done.left >= actions.right - 1 && beside(done),
          height: Math.round(el.getBoundingClientRect().height),
        };
      });
      expect(bar.countLeft, `"3 selected" sits left of the actions, on their band (bar ${bar.height} px)`).toBe(true);
      expect(bar.doneRight, `Done sits right of the actions, on their band (bar ${bar.height} px)`).toBe(true);
      await shot(page, 'selection-bar');
    });
  }
});

test('a library page shows the spinner while it waits for its view settings (guide finding 3)', async ({ page }) => {
  test.setTimeout(150_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  await login(page);
  const { libraryId } = await ensureFixture(page);
  let release: () => void = () => undefined;
  const held = new Promise<void>((resolve) => { release = resolve; });
  await page.route('**/api/v1/reading/library-preferences', async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    await held;
    return route.continue();
  });
  const browses: string[] = [];
  page.on('request', (r) => { if (r.url().includes(`/libraries/${libraryId}/browse`)) browses.push(r.url()); });
  await page.goto(`/libraries/${libraryId}/browse`);
  await expect(page.locator('.browse-bar')).toBeVisible();
  // Held view settings: no folder request yet, but the spinner (after its 300 ms) - not a bar over nothing.
  await expect(page.getByTestId('browse-loading')).toBeVisible({ timeout: 3_000 });
  expect(browses, 'the folder request waits for the view settings').toEqual([]);
  await expect(page.getByText('This folder is empty.')).toHaveCount(0);
  await shot(page, 'loading-view-settings');
  release();
  await expect(page.locator('.node-wrap').first()).toBeVisible();
  await expect(page.getByTestId('browse-loading')).toHaveCount(0);
});

/** Serves the fixture library under the guide's very long name, and a long ancestor in the plain folder's trail. */
async function longTrail(page: Page, libraryId: string, plain: string): Promise<void> {
  await page.route('**/api/v1/libraries', async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    const response = await route.fetch();
    const libs = (await response.json()) as { id: string; name: string }[];
    await route.fulfill({ response, json: libs.map((l) => (l.id === libraryId ? { ...l, name: LONG_LIBRARY } : l)) });
  });
  await page.route(`**/api/v1/nodes/${plain}/breadcrumbs`, (route) => route.fulfill({
    status: 200, contentType: 'application/json',
    json: { nodeId: plain, trail: [{ id: plain, displayName: LONG_ANCESTOR }] },
  }));
}

/** What the trail shows: the folder you are in whole and inside the bar, and every cut drawn with an ellipsis. */
async function trailState(page: Page) {
  return page.locator('.browse-bar .breadcrumbs').evaluate((trail) => {
    const box = trail.getBoundingClientRect();
    const inside = (r: DOMRect) => r.width > 0 && r.left >= box.left - 1 && r.right <= box.right + 1;
    const current = trail.querySelector('.current')!;
    const currentSep = trail.querySelector('.crumb-current .sep');
    const cuts: string[] = [];
    let hardCuts = 0;
    for (const el of [trail, ...Array.from(trail.querySelectorAll<HTMLElement>('*'))] as HTMLElement[]) {
      const style = getComputedStyle(el);
      if (style.display === 'inline' || el.scrollWidth <= el.clientWidth + 1) continue;
      const right = el.getBoundingClientRect().right;
      // A cut is drawn as "..." only on text: an inline-block (an atomic box) that crosses the edge is clipped without one.
      const atomic = Array.from(el.children).some((c) => getComputedStyle(c).display === 'inline-block'
        && c.getBoundingClientRect().right > right + 1);
      if (style.textOverflow !== 'ellipsis' || style.overflow !== 'hidden' || atomic) hardCuts++;
      cuts.push(el.className || el.tagName);
    }
    return {
      current: current.textContent?.trim(),
      currentShown: inside(current.getBoundingClientRect()) && (current as HTMLElement).scrollWidth <= (current as HTMLElement).clientWidth + 1,
      sepShown: !!currentSep && inside(currentSep.getBoundingClientRect()),
      cuts, hardCuts,
    };
  });
}

for (const [viewport, touch] of [[{ width: 820, height: 1180 }, true], [{ width: 1280, height: 900 }, false]] as const) {
  test.describe(`${viewport.width} px${touch ? ', touch' : ''}: a long trail (guide finding 4)`, () => {
    test.use({ viewport, hasTouch: touch });

    test('ends with "..." and always shows the folder you are in, with "Rate" in the bar', async ({ page }) => {
      test.setTimeout(150_000);
      await login(page);
      const { libraryId, plain } = await ensureFixture(page);
      await longTrail(page, libraryId, plain);
      await page.goto(`/libraries/${libraryId}/browse/${plain}`);
      await expect(page.getByTestId('folder-rate-button')).toBeVisible();
      await expect(page.locator('.browse-bar .breadcrumbs a', { hasText: 'Jump Rail' })).toBeVisible();
      await expect(page.locator('.browse-bar .breadcrumbs .current')).toHaveText(PLAIN);
      await page.waitForTimeout(400);
      const state = await trailState(page);
      expect(state.currentShown, `the folder you are in (${state.current}) is whole and inside the trail`).toBe(true);
      expect(state.sepShown, 'the "/" in front of it shows too').toBe(true);
      expect(state.cuts.length, 'the long names cannot all fit: something is cut').toBeGreaterThan(0);
      expect(state.hardCuts, `every cut ends with "..." (cut: ${state.cuts.join(', ')})`).toBe(0);
      await shot(page, `trail-long-name${touch ? '-touch' : ''}`);

      // The library root (no folder of its own): the long name alone ends with "..." too.
      await page.goto(`/libraries/${libraryId}/browse`);
      await expect(page.locator('.node-wrap').first()).toBeVisible();
      const root = await page.locator('.browse-bar .breadcrumbs').evaluate((trail) => {
        const cut = Array.from(trail.querySelectorAll<HTMLElement>('a')).concat([trail as HTMLElement])
          .filter((el) => getComputedStyle(el).display !== 'inline' && el.scrollWidth > el.clientWidth + 1);
        return cut.map((el) => getComputedStyle(el).textOverflow);
      });
      expect(root.every((t) => t === 'ellipsis'), `a cut library name ends with "..." (${root.join(', ')})`).toBe(true);
    });
  });
}
