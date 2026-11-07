import { test, expect, Page, APIRequestContext, Locator } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Mobile fixes for the app shell, browse and the shared controls (1.51.0, from the mobile UI sweep of 1.50.1, findings M1-M21).
 * Phones are checked as phones: a touch screen (`hasTouch`, `isMobile`) at 360 / 390 px and held sideways at 844 x 390. Real data
 * from the synthetic libraries (`e2e/fixtures/make-series-fixtures.mjs`): `Volume Stacks` (the series folder with volumes) and
 * `Jump Rail` (a 65-chapter folder: the jump picker and the phone scrub strip). A long library name and the tag search are
 * contract-shaped answers served in the browser; nothing is changed on the server except the viewer's own view preferences, which
 * every test restores.
 *
 * - M2 / M3 / M4: the phone selection bar is ONE short row and every icon button in it has an accessible name; the stack page's Select too.
 * - M1 / M5 / M14 / M21: sideways, the expanded sidebar stops at its width (a long name ends in an ellipsis), the browse bar wraps as on
 *   a phone and its menus open on screen; the collapsed rail's links have names and the expand button is a 40 px target; the jump
 *   control is the picker (40 px chips).
 * - M7 / M8: the scrub strip leaves the cards' stars free, and a TOUCH drag (not only a mouse drag) shows the bubble and lands.
 * - M9 - M13, M20: touch targets of 40 px - the brand link, the size sliders, Home's library headings, the breadcrumb, the list
 *   checkboxes, the tag filter chips' buttons and Refine's or / not; the filter chips wrap and cut a long name inside the chip.
 * - A desktop with a mouse keeps the old sizes and labels.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const VOLUMES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-volumes` : undefined;
const JUMP_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-jump` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LONG_NAME = 'A Synthetic Shelf With A Rather Long Library Name That Never Fits A Sidebar';
const UNBROKEN = 'Averyveryverylongsyntheticpennamewithoutanyspacesforthelayoutcheck';
const TOUCH = { hasTouch: true, isMobile: true, deviceScaleFactor: 2 } as const;

test.skip(!FIXTURE_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic libraries available');
// Not serial: each check stands alone (one worker runs them in order; each test restores the view preferences it changed).
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
  const { token } = await (await request.get('/api/v1/auth/csrf')).json();
  return { 'X-MangaPixer-Csrf': token };
}

/** A synthetic library (registered by whichever spec runs first) and one of its folders once it holds `rows` items: [libraryId, folderId]. */
async function ensureLibrary(page: Page, name: string, root: string, folderName: string, rows: number, query = ''): Promise<[string, string]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === name);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: name, rootPath: root } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folder = '';
  await expect.poll(async () => {
    const top = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = ((top.items as Node[]).find((n) => n.displayName === folderName)?.id) ?? '';
    if (!folder) return 0;
    const inside = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?parentId=${folder}&pageSize=100${query}`)).json();
    return (inside.items as Node[]).length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(rows);
  return [lib!.id, folder];
}
const volumeLibrary = (page: Page) => ensureLibrary(page, 'Volume Stacks', VOLUMES_ROOT!, 'Stacked Saga', 7, '&group=flat');
const jumpLibrary = (page: Page) => ensureLibrary(page, 'Jump Rail', JUMP_ROOT!, 'Jump Saga', 65);

/** Writes the viewer's browse preferences and returns the previous ones (the PUT overwrites every field). */
async function setPreferences(page: Page, change: Record<string, unknown>): Promise<Record<string, unknown>> {
  const headers = await csrf(page.request);
  const current = await (await page.request.get('/api/v1/reading/library-preferences')).json();
  const res = await page.request.put('/api/v1/reading/library-preferences', { headers, data: { ...current, ...change } });
  expect(res.ok(), await res.text()).toBeTruthy();
  return current;
}

async function settle(page: Page): Promise<void> {
  await page.waitForLoadState('networkidle', { timeout: 5000 }).catch(() => { /* a page that keeps polling */ });
  await page.waitForTimeout(400);
}

async function shot(page: Page, name: string): Promise<void> {
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/mobile-${name}.png` });
}

async function box(locator: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  await locator.scrollIntoViewIfNeeded();
  const b = await locator.boundingBox();
  expect(b, 'element has a box').not.toBeNull();
  return b!;
}

/** Fails with the element's size when it is smaller than a 40 x 40 touch target (`what` names it). */
async function expectTarget(locator: Locator, what: string, min = { width: 40, height: 40 }): Promise<void> {
  const b = await box(locator);
  expect(Math.round(b.width), `${what}: width`).toBeGreaterThanOrEqual(min.width);
  expect(Math.round(b.height), `${what}: height`).toBeGreaterThanOrEqual(min.height);
}

/** Every button in `scope` that is drawn has an accessible name. */
async function expectNamedButtons(scope: Locator, what: string): Promise<number> {
  const buttons = scope.locator('button:visible');
  const n = await buttons.count();
  for (let i = 0; i < n; i++) await expect(buttons.nth(i), `${what}: button ${i + 1} of ${n}`).toHaveAccessibleName(/\S/);
  return n;
}

test.describe('phone, touch', () => {
  test.use({ ...TOUCH, viewport: { width: 390, height: 844 } });

  test('M2 / M3 / M4 / M12 / M13: one-row selection bar with named buttons, stack Select, breadcrumb and row checkbox targets', async ({ page }) => {
    test.setTimeout(240_000);
    await login(page);
    const [libraryId, folderId] = await volumeLibrary(page);
    const browse = `/libraries/${libraryId}/browse`;
    const saved = await setPreferences(page, { viewMode: 'card', seriesViewMode: 'Folders' });
    try {
      for (const width of [360, 390]) {
        await page.setViewportSize({ width, height: width === 360 ? 780 : 844 });
        // Select mode at the library root, one folder selected (an admin: the bar holds all nine actions).
        await page.goto(browse);
        await page.locator('button.select-toggle').tap();
        await page.locator('.node-wrap .node-card').first().tap();
        await expect(page.getByTestId('selection-count')).toHaveText('1 selected');
        await settle(page);
        await shot(page, `${width}-select-one`);
        const bar = page.locator('.browse-bar.selecting');
        const height = (await bar.boundingBox())!.height;
        expect(height, `${width} px: the selection bar is one short row`).toBeLessThanOrEqual(80);
        const named = await expectNamedButtons(bar, `${width} px selection bar`);
        expect(named, 'the admin bar holds the actions, the Select menu and Done').toBeGreaterThanOrEqual(7);
        // Done keeps its border and its place: the whole of it is on screen beside the actions.
        const done = await box(page.getByTestId('selection-done'));
        expect(done.x + done.width).toBeLessThanOrEqual(width);
        await expectFitsScreen(page, `${width} px select mode`);

        // The series folder's breadcrumb links.
        await page.goto(`${browse}/${folderId}`);
        await expectTarget(page.locator('.breadcrumbs a').first(), `${width} px breadcrumb link`, { width: 1, height: 40 });

        // The volume stack page: its Select button is named, and its own selection bar is one row too.
        await page.goto(`${browse}/${folderId}/volume/1`);
        const select = page.getByTestId('stack-select');
        await expect(select).toHaveAccessibleName('Select');
        await select.tap();
        await page.getByTestId('stack-item').first().tap();
        await settle(page);
        const stackBar = page.locator('.bar.selecting');
        expect((await stackBar.boundingBox())!.height, `${width} px: the stack page's selection bar`).toBeLessThanOrEqual(80);
        await expectNamedButtons(stackBar, `${width} px stack selection bar`);
        await expectFitsScreen(page, `${width} px stack select mode`);
      }

      // List view: the row checkbox is a 40 x 40 target.
      await setPreferences(page, { viewMode: 'list', seriesViewMode: 'Folders' });
      await page.goto(`${browse}/${folderId}`);
      await expectTarget(page.locator('.node-wrap .row-select').first(), 'list row checkbox');
    } finally {
      await setPreferences(page, saved);
    }
  });

  test('M7 / M8 / M14: the scrub strip leaves the stars free and follows a touch drag; the picker chips are 40 px', async ({ page }) => {
    test.setTimeout(180_000);
    await login(page);
    const [libraryId, folderId] = await jumpLibrary(page);
    const saved = await setPreferences(page, { viewMode: 'card', sort: 'name', direction: 'asc' });
    try {
      await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
      const strip = page.locator('.scrub');
      await expect(strip).toBeVisible();
      await settle(page);
      const s = (await strip.boundingBox())!;

      // M7: no card reaches under the strip, and a tap at a right-hand card's star lands on the star.
      const rightmost = await page.locator('.nodes .node-wrap').evaluateAll((els) => Math.max(...els.map((e) => e.getBoundingClientRect().right)));
      expect(rightmost, 'the cards end before the strip').toBeLessThanOrEqual(s.x + 0.5);
      const covered = await page.evaluate(() => {
        const stars = Array.from(document.querySelectorAll<HTMLElement>('.nodes app-star-toggle button'));
        return stars.filter((b) => {
          const r = b.getBoundingClientRect();
          if (r.bottom <= 0 || r.top >= innerHeight) return false;
          const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
          return !hit || !b.contains(hit);
        }).length;
      });
      expect(covered, 'stars on screen that a tap does not reach').toBe(0);
      await shot(page, '390-scrub-gutter');

      // M8: a TOUCH drag (CDP touch events, as a finger) - the bubble follows, the page does not scroll, release lands on the range.
      const n = await page.locator('.scrub .mark').count();
      const sliceY = (i: number) => s.y + (s.height * (i + 0.5)) / n;
      const x = s.x + s.width - 10;
      const cdp = await page.context().newCDPSession(page);
      const touch = (type: 'touchStart' | 'touchMove' | 'touchEnd', y?: number) =>
        cdp.send('Input.dispatchTouchEvent', { type, touchPoints: y === undefined ? [] : [{ x, y, id: 1, radiusX: 4, radiusY: 4, force: 1 }] });
      const scrollBefore = await page.evaluate(() => window.scrollY);
      await touch('touchStart', sliceY(0));
      await expect(page.locator('.scrub .bubble-label')).toHaveText('0-9');
      for (let i = 1; i <= 8; i++) { await touch('touchMove', sliceY(0) + ((sliceY(4) - sliceY(0)) * i) / 8); await page.waitForTimeout(30); }
      await expect(page.locator('.scrub .bubble-label')).toHaveText('40-49');
      expect(await page.evaluate(() => window.scrollY), 'the drag does not scroll the page').toBe(scrollBefore);
      await shot(page, '390-scrub-touch-drag');
      await touch('touchEnd');
      await cdp.detach();
      await expect(page.locator('.scrub .bubble')).toHaveCount(0);
      await expect(page.locator('.jump-menu-trigger')).toContainText('40-49');
      await expect(page.locator('.node-wrap', { hasText: 'Jump Saga - Ch. 040.cbz' })).toBeInViewport();

      // M14: the phone picker's chips.
      await page.locator('.jump-menu-trigger').tap();
      const chips = page.locator('.jump-picker-menu .jump-chip');
      await expect(chips.first()).toBeVisible();
      for (let i = 0; i < await chips.count(); i++) await expectTarget(chips.nth(i), `picker chip ${i + 1}`);
    } finally {
      await setPreferences(page, saved);
    }
  });

  test('M9 / M10 / M11: the brand link, the size sliders and Home\'s library headings are 40 px targets', async ({ page }) => {
    test.setTimeout(120_000);
    await login(page);
    await volumeLibrary(page);
    await page.goto('/');
    await expect(page.locator('.home-toolbar')).toBeVisible();
    await expectTarget(page.locator('a.brand'), 'header brand link');
    await expectTarget(page.locator('.home-toolbar input[type=range]'), 'Home card size slider', { width: 1, height: 40 });
    const heading = page.locator('.recent-lib h4').first();
    await expect(heading).toBeVisible();
    await expectTarget(heading, 'Home library heading', { width: 1, height: 40 });
    await page.goto('/settings');
    await expectTarget(page.getByTestId('settings-card-size'), 'Settings card size slider', { width: 1, height: 40 });
    await expectTarget(page.getByTestId('settings-list-columns'), 'Settings list columns slider', { width: 1, height: 40 });
  });

  test('M20: the tag filter chips wrap, cut a long name inside the chip, and their buttons and Refine\'s or / not are 40 px', async ({ page }) => {
    test.setTimeout(120_000);
    await login(page);
    await page.route(/\/api\/v1\/search\?.*\bf=/, (route) => route.fulfill({ json: {
      query: '', items: [], totalCount: 0, nextCursor: null, hasMore: false, works: [],
      filters: [
        { bucket: 'Creators', source: 'Chip', id: 'fv_l1', display: UNBROKEN, matched: true, valueIds: ['fv_l1'] },
        { bucket: 'Genres', source: 'Chip', id: 'fv_g0', display: 'Action', matched: true, valueIds: ['fv_g0'] },
      ],
      refine: [{ bucket: 'Genres', more: 0, chips: [{ id: 'fv_g1', display: 'Drama', count: 2, roles: 'None', conflict: false, filter: 'genre:fv_g1' }] }],
    } }));
    for (const width of [360, 390]) {
      await page.setViewportSize({ width, height: 800 });
      await page.goto('/search?f=creator:fv_l1&f=genre:fv_g0');
      const chips = page.getByTestId('search-filter-chip');
      await expect(chips).toHaveCount(2);
      await settle(page);
      await shot(page, `${width}-search-tags`);
      // Both chips inside the field, the long one cut inside itself: its name shows more than a few letters.
      const field = (await page.locator('.search-field .mat-mdc-text-field-wrapper').boundingBox())!;
      for (let i = 0; i < 2; i++) {
        const c = (await chips.nth(i).boundingBox())!;
        expect(c.x, `${width} px chip ${i + 1} left`).toBeGreaterThanOrEqual(field.x - 0.5);
        expect(c.x + c.width, `${width} px chip ${i + 1} right`).toBeLessThanOrEqual(field.x + field.width + 0.5);
      }
      const label = chips.first().locator('.filter-label');
      const shown = await label.evaluate((el) => ({ width: el.getBoundingClientRect().width, cut: el.scrollWidth > el.clientWidth }));
      expect(shown.width, `${width} px: the long name is readable inside its chip`).toBeGreaterThan(80);
      expect(shown.cut, 'the long name is cut (ellipsis)').toBe(true);
      for (let i = 0; i < 2; i++) {
        const exclude = chips.nth(i).getByTestId('search-filter-exclude');
        await expectTarget(exclude, `${width} px chip ${i + 1} Exclude`);
        // The whole button takes the touch: its left edge is not cut off by the chip's label cell.
        const reached = await exclude.evaluate((el) => {
          const r = el.getBoundingClientRect();
          const hit = document.elementFromPoint(r.left + 3, r.top + r.height / 2);
          return !!hit && el.contains(hit);
        });
        expect(reached, `${width} px chip ${i + 1} Exclude: its left edge takes a tap`).toBe(true);
        await expectTarget(chips.nth(i).getByRole('button', { name: /^Remove filter/ }), `${width} px chip ${i + 1} Remove`);
      }
      await expectTarget(page.getByTestId('refine-or').first(), `${width} px Refine or`);
      await expectTarget(page.getByTestId('refine-not').first(), `${width} px Refine not`);
      await expectFitsScreen(page, `${width} px tag search`);
    }
  });
});

test.describe('small phone, touch', () => {
  test.use({ ...TOUCH, viewport: { width: 360, height: 780 } });

  test('N1 (gate sweep): beside the scrub strip the default card size still gets two columns at 360 px', async ({ page }) => {
    // 1.51.0: M7's 44 px gutter left a 300 px grid at 360 px - one 300 x 471 card per screen for a long level, three times the scrolling.
    test.setTimeout(120_000);
    await login(page);
    const [libraryId, folderId] = await jumpLibrary(page);
    const saved = await setPreferences(page, { viewMode: 'card', sort: 'name', direction: 'asc' });
    try {
      await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
      const strip = page.locator('.scrub');
      await expect(strip).toBeVisible();
      await settle(page);
      const s = (await strip.boundingBox())!;
      const cards = await page.locator('.nodes .node-wrap').evaluateAll((els) =>
        els.slice(0, 6).map((e) => { const r = e.getBoundingClientRect(); return { left: Math.round(r.left), top: Math.round(r.top), right: r.right }; }));
      const firstRow = cards.filter((c) => c.top === cards[0].top);
      expect(firstRow.length, 'cards in the first row').toBeGreaterThanOrEqual(2);
      expect(Math.max(...cards.map((c) => c.right)), 'the cards still end before the strip').toBeLessThanOrEqual(s.x + 0.5);
      await shot(page, '360-scrub-gutter-two-columns');
    } finally {
      await setPreferences(page, saved);
    }
  });
});

test.describe('phone held sideways, touch', () => {
  test.use({ ...TOUCH, viewport: { width: 844, height: 390 } });

  test('M1 / M5 / M14 / M21: a long library name, the sidebar expanded - the page fits, menus open on screen; the collapsed rail is named', async ({ page }) => {
    test.setTimeout(180_000);
    await login(page);
    const [libraryId, folderId] = await volumeLibrary(page);
    const [jumpId, sagaId] = await jumpLibrary(page);
    // Contract-shaped: the Volume Stacks library carries a long name (the sidebar and the breadcrumb read it from the list).
    await page.route(/\/api\/v1\/libraries$/, async (route) => {
      const res = await route.fetch();
      const libs = (await res.json()) as { id: string; name: string }[];
      await route.fulfill({ response: res, json: libs.map((l) => (l.id === libraryId ? { ...l, name: LONG_NAME } : l)) });
    });
    const saved = await setPreferences(page, { viewMode: 'card', seriesViewMode: 'Folders', sort: 'name', direction: 'asc' });
    try {
      // Collapsed (a fresh device): the rail's links carry their names; "Expand sidebar" is a 40 px target.
      await page.goto('/');
      const nav = page.locator('app-library-sidebar nav.sidebar');
      await expect(nav).toHaveClass(/collapsed/);
      await expect(nav.locator(`a[href="/libraries/${libraryId}/browse"]`)).toHaveAccessibleName(LONG_NAME);
      await expect(nav.locator('a[href="/"]')).toHaveAccessibleName('Home');
      const expand = page.getByRole('button', { name: 'Expand sidebar' });
      await expectTarget(expand, 'Expand sidebar');

      // Expanded: the column stops at the nav width and the long name ends in an ellipsis.
      await expand.tap();
      await expect(nav).not.toHaveClass(/collapsed/);
      await page.goto(`/libraries/${libraryId}/browse`);
      await settle(page);
      const side = (await page.locator('app-library-sidebar').boundingBox())!;
      expect(side.width, 'the expanded sidebar keeps its width').toBeLessThanOrEqual(225);
      const label = nav.locator(`a[href="/libraries/${libraryId}/browse"] .nav-label`);
      await expect(label).toHaveAttribute('title', LONG_NAME);
      expect(await label.evaluate((el) => el.scrollWidth > el.clientWidth), 'the long name is cut').toBe(true);
      await shot(page, 'land-lib-root-expanded');
      await expectFitsScreen(page, 'sideways, sidebar expanded: library root');

      // The View menu opens on screen and its last option can be reached.
      await page.locator('.view-toggle').tap();
      const panel = page.locator('.view-options-menu').first();
      await expect(panel).toBeVisible();
      const p = (await panel.boundingBox())!;
      expect(p.x).toBeGreaterThanOrEqual(0);
      expect(p.x + p.width).toBeLessThanOrEqual(844.5);
      await page.keyboard.press('Escape');

      // Select mode: Done stays on screen; the bar is one row.
      await page.locator('button.select-toggle').tap();
      await page.locator('.node-wrap .node-card').first().tap();
      await settle(page);
      const done = (await page.getByTestId('selection-done').boundingBox())!;
      expect(done.x + done.width).toBeLessThanOrEqual(844);
      expect((await page.locator('.browse-bar.selecting').boundingBox())!.height).toBeLessThanOrEqual(80);
      await expectNamedButtons(page.locator('.browse-bar.selecting'), 'sideways selection bar');
      await expectFitsScreen(page, 'sideways, sidebar expanded: select mode');

      // The series folder and its volume stack (the view switch).
      await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
      await settle(page);
      await expectFitsScreen(page, 'sideways, sidebar expanded: series folder');
      await page.goto(`/libraries/${libraryId}/browse/${folderId}/volume/1`);
      await settle(page);
      await expectFitsScreen(page, 'sideways, sidebar expanded: volume stack');

      // The long folder: the jump control is the picker (40 px chips), not the 20 px tall rail.
      await page.goto(`/libraries/${jumpId}/browse/${sagaId}`);
      await settle(page);
      await expect(page.locator('nav.jump-rail')).toBeHidden();
      await expect(page.locator('.jump-menu-trigger')).toBeVisible();
      await shot(page, 'land-jump-saga');
      await expectFitsScreen(page, 'sideways, sidebar expanded: long folder');
    } finally {
      await setPreferences(page, saved);
    }
  });
});

test.describe('desktop, mouse', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test('a desktop keeps the old sizes and labels (fine pointer)', async ({ page }) => {
    test.setTimeout(120_000);
    await login(page);
    const [libraryId, folderId] = await volumeLibrary(page);
    const saved = await setPreferences(page, { viewMode: 'list', seriesViewMode: 'Folders' });
    try {
      await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
      const check = (await box(page.locator('.node-wrap .row-select').first()));
      expect([Math.round(check.width), Math.round(check.height)]).toEqual([28, 28]);
      await expect(page.locator('nav.jump-rail, .jump-menu-trigger:visible')).toHaveCount(0); // a 7-row folder has no rail
      await page.locator('button.select-toggle').click();
      await page.locator('.node-wrap .row-select').first().click();
      // The labels are drawn beside the icons on a desktop.
      await expect(page.getByTestId('selection-mark-read').locator('.lbl')).toBeVisible();
      const slider = page.locator('.browse-bar .size-control-inline input[type=range]');
      if (await slider.count()) expect(Math.round((await slider.boundingBox())!.height)).toBeLessThan(40);
      await expectFitsScreen(page, 'desktop select mode');
    } finally {
      await setPreferences(page, saved);
    }
  });
});
