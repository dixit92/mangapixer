import { test, expect, Page, APIRequestContext } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * The smart jump rail (1.41.0) in a real folder: `Jump Saga` holds `Jump Saga - Ch. 001 .. 064.cbz` and a one-shot
 * (`e2e/fixtures/make-series-fixtures.mjs`, `<E2E_SERIES_FIXTURE_ROOT>-jump`). The common prefix is ignored, so the rail lists round
 * number ranges (0, 10 ... 60) plus an `Other` chip for the one-shot; a chip lands on the first chapter of its range - in place when
 * that card is loaded, through the range's cursor when it is not - in ascending and in descending name order. Checked at 1280 px
 * (the rail) and 390 px (the phone picker, which shows the ranges).
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const JUMP_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-jump` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Jump Rail';
const ASC = ['0', '10', '20', '30', '40', '50', '60', 'Other'];

test.skip(!JUMP_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic jump-rail library available');
test.describe.configure({ mode: 'serial' });
// A step that cannot happen fails on its own line instead of running into the test timeout.
test.beforeEach(({ page }) => { page.setDefaultTimeout(20_000); });

interface Node { id: string; displayName: string }

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

/** The jump library (registered + scanned once); returns [libraryId, folderId]. */
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
    return inside.totalCount as number;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(65);
  return [lib!.id, folder];
}

/** Writes the viewer's name-sort direction (the PUT overwrites every field, so the current ones are echoed); returns the previous one. */
async function setDirection(page: Page, direction: 'asc' | 'desc'): Promise<string> {
  const headers = await csrf(page.request);
  const current = await (await page.request.get('/api/v1/reading/library-preferences')).json();
  const res = await page.request.put('/api/v1/reading/library-preferences', { headers, data: { ...current, sort: 'name', direction } });
  expect(res.ok(), await res.text()).toBeTruthy();
  return current.direction ?? 'asc';
}

async function shot(page: Page, name: string): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: false });
}

/**
 * Whether the card titled `title` sits in the first row just under the sticky stack (the visible rail, else the top bar) - where a
 * jump scrolls the bucket's first card to.
 */
async function landsUnderRail(page: Page, title: string): Promise<boolean> {
  return page.evaluate((wanted) => {
    const rail = document.querySelector<HTMLElement>('nav.jump-rail');
    const stack = rail && rail.getBoundingClientRect().height > 0 ? rail : document.querySelector<HTMLElement>('.browse-bar');
    const edge = stack ? stack.getBoundingClientRect().bottom : 0;
    const card = Array.from(document.querySelectorAll<HTMLElement>('.node-wrap'))
      .find((c) => c.querySelector('.node-title')?.textContent?.trim() === wanted);
    if (!card) return false;
    const offset = card.getBoundingClientRect().top - edge;
    return offset >= -2 && offset <= 40;
  }, title);
}

/**
 * Whether a jump landed on `title`: scrolled in place to just under the rail (the card was loaded), or - near the end of the list,
 * where the page cannot scroll that far - scrolled to the bottom with the card on screen, or the list was reloaded from the range's
 * cursor and starts with it (the card was not loaded; that depends on how far infinite scroll has gone, so all three count).
 */
async function landed(page: Page, title: string): Promise<boolean> {
  if (await landsUnderRail(page, title)) return true;
  const atBottom = await page.evaluate((wanted) => {
    const card = Array.from(document.querySelectorAll<HTMLElement>('.node-wrap'))
      .find((c) => c.querySelector('.node-title')?.textContent?.trim() === wanted);
    if (!card) return false;
    const r = card.getBoundingClientRect();
    // Near the end of the list the page cannot scroll the card up to the rail: it counts as landed when it is fully on screen and
    // what is left to scroll could not bring it within the landing band under the rail (the list's footer can still grow a few px
    // after the jump, so "exactly at the bottom" is too strict - seen: 12 px).
    const rail = document.querySelector<HTMLElement>('nav.jump-rail');
    const stack = rail && rail.getBoundingClientRect().height > 0 ? rail : document.querySelector<HTMLElement>('.browse-bar');
    const edge = stack ? stack.getBoundingClientRect().bottom : 0;
    const sc = document.scrollingElement ?? document.documentElement;
    const remaining = sc.scrollHeight - sc.clientHeight - sc.scrollTop;
    return r.top >= edge && r.bottom <= window.innerHeight && r.top - edge - remaining > 40;
  }, title);
  if (atBottom) return true;
  return (await page.locator('.node-wrap').first().locator('.node-title').textContent())?.trim() === title;
}

async function chips(page: Page): Promise<string[]> {
  return (await page.locator('nav.jump-rail .jump-chip').allTextContents()).map((t) => t.trim());
}

test('desktop: number ranges behind a common prefix land on their first chapter, ascending and descending', async ({ page }) => {
  test.setTimeout(180_000); // the first test also waits for the library scan
  await page.setViewportSize({ width: 1280, height: 900 });
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  const previous = await setDirection(page, 'asc');
  try {
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    const rail = page.locator('nav.jump-rail');
    await expect(rail).toBeVisible();
    await expect(rail).toHaveAttribute('aria-label', 'Jump to number');
    await expect.poll(() => chips(page)).toEqual(ASC);
    await expect(page.locator('.node-wrap').first().locator('.node-title')).toHaveText('Jump Saga - Ch. 001.cbz');

    // 30 is in the first loaded page: scrolls in place to Ch. 030.
    await rail.getByRole('button', { name: '30', exact: true }).click();
    await expect.poll(() => landsUnderRail(page, 'Jump Saga - Ch. 030.cbz')).toBe(true);
    await expect(rail.locator('.jump-chip.active')).toHaveText('30');

    // The rail stays on top of the cards it overlays: a tap on a chip hits the chip, not a card.
    const box = (await rail.getByRole('button', { name: '60', exact: true }).boundingBox())!;
    const hit = await page.evaluate(([x, y]) => document.elementFromPoint(x, y)?.closest('.jump-chip')?.textContent?.trim() ?? null,
      [box.x + box.width / 2, box.y + box.height / 2]);
    expect(hit).toBe('60');

    // 60 may be past what is loaded (50 cards a page): then the range cursor reloads the list from Ch. 060.
    await rail.getByRole('button', { name: '60', exact: true }).click();
    await expect.poll(() => landed(page, 'Jump Saga - Ch. 060.cbz')).toBe(true);
    // 1.47.0: near the end of the list the page cannot scroll the range's first card up to the top row - the clicked chip stays
    // highlighted (the scroll-spy used to move it to the range the top row belongs to).
    await expect(rail.locator('.jump-chip.active')).toHaveText('60');
    await rail.getByRole('button', { name: 'Other', exact: true }).click();
    await expect(page.locator('.node-wrap', { hasText: 'Jump Saga - Oneshot.cbz' })).toBeInViewport();
    await expect(rail.locator('.jump-chip.active')).toHaveText('Other');
    await page.waitForTimeout(600); // a late scroll event must not move it either
    await expect(rail.locator('.jump-chip.active')).toHaveText('Other');
    await expectFitsScreen(page, 'the jump rail (desktop)');
    await shot(page, 'jump-rail-desktop');

    // Descending: the rail is reversed and each chip lands on its range's first chapter in that order.
    await setDirection(page, 'desc');
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    await expect.poll(() => chips(page)).toEqual([...ASC].reverse());
    await expect(page.locator('.node-wrap').first().locator('.node-title')).toHaveText('Jump Saga - Oneshot.cbz');
    await rail.getByRole('button', { name: '50', exact: true }).click();
    await expect.poll(() => landsUnderRail(page, 'Jump Saga - Ch. 059.cbz')).toBe(true);
    await rail.getByRole('button', { name: '0', exact: true }).click();
    await expect.poll(() => landed(page, 'Jump Saga - Ch. 009.cbz')).toBe(true);
    await shot(page, 'jump-rail-desktop-desc');
  } finally {
    await setDirection(page, previous === 'desc' ? 'desc' : 'asc');
  }
});

test('phone: the picker lists the ranges and a tap lands on the range', async ({ page }) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  const previous = await setDirection(page, 'asc');
  try {
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    await expect(page.locator('nav.jump-rail')).toBeHidden();
    const trigger = page.locator('.jump-menu-trigger');
    await expect(trigger).toBeVisible();
    await expect(trigger).toContainText('0-9');
    await expectFitsScreen(page, 'the folder with the jump picker (phone)');

    await trigger.click();
    const picker = page.locator('.jump-picker-menu .jump-chip');
    await expect.poll(async () => (await picker.allTextContents()).map((t) => t.trim()))
      .toEqual(['0-9', '10-19', '20-29', '30-39', '40-49', '50-59', '60-69', 'Other']);
    await shot(page, 'jump-rail-phone-menu');
    await page.locator('.jump-picker-menu').getByRole('button', { name: '40-49', exact: true }).click();
    await expect(page.locator('.jump-picker-menu')).toHaveCount(0); // the picker closes on a pick
    await expect.poll(() => landsUnderRail(page, 'Jump Saga - Ch. 040.cbz')).toBe(true);
    await expect(trigger).toContainText('40-49');
    await expectFitsScreen(page, 'the folder after a jump (phone)');
    await shot(page, 'jump-rail-phone');
  } finally {
    await setDirection(page, previous === 'desc' ? 'desc' : 'asc');
  }
});

/**
 * The phone scrub strip (1.49.0, lane P): on a LONG level (this one has 65 rows) a thin strip sits on the right edge at 390 px; a
 * touch-drag shows a bubble with the chip under the finger without scrolling the page, and letting go jumps there - the same jump as
 * a tap on the picker's chip. A short level (a faked 30-row jump index) shows the picker only; tablet widths show no strip.
 */
test('phone: the scrub strip on a long level - drag shows a bubble, release lands on the range; a short level has none', async ({ page }) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  const previous = await setDirection(page, 'asc');
  try {
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    const strip = page.locator('.scrub');
    await expect(strip).toBeVisible();
    await expect(strip).toHaveAttribute('aria-hidden', 'true');
    await expect(page.locator('.jump-menu-trigger')).toBeVisible(); // the accessible path stays
    await expect(page.locator('.scrub .mark')).toHaveCount(ASC.length);
    await expectFitsScreen(page, 'the folder with the scrub strip (phone)');

    // The strip is a 44 px wide hit area on the right edge, on top of the cards.
    const box = (await strip.boundingBox())!;
    expect(box.width).toBeGreaterThanOrEqual(44);
    expect(box.x + box.width).toBeLessThanOrEqual(390.5);
    const touchAction = await strip.evaluate((el) => getComputedStyle(el).touchAction);
    expect(touchAction).toBe('none');
    const hit = await page.evaluate(([x, y]) => document.elementFromPoint(x, y)?.closest('.scrub') !== null, [box.x + box.width - 10, box.y + box.height / 2]);
    expect(hit).toBe(true);

    // Drag to the slice of chip 40 (index 4 of 8): the bubble names it, the page does not scroll, nothing is jumped to yet.
    const x = box.x + box.width - 10;
    const sliceY = (i: number) => box.y + (box.height * (i + 0.5)) / ASC.length;
    const scrollBefore = await page.evaluate(() => window.scrollY);
    await page.mouse.move(x, sliceY(0));
    await page.mouse.down();
    await expect(page.locator('.scrub .bubble-label')).toHaveText('0-9');
    await page.mouse.move(x, sliceY(2), { steps: 4 });
    await page.mouse.move(x, sliceY(4), { steps: 4 });
    await expect(page.locator('.scrub .bubble-label')).toHaveText('40-49');
    await shot(page, 'jump-scrub-phone-drag');
    expect(await page.evaluate(() => window.scrollY)).toBe(scrollBefore);
    expect(await landsUnderRail(page, 'Jump Saga - Ch. 040.cbz')).toBe(false);

    await page.mouse.up();
    await expect(page.locator('.scrub .bubble')).toHaveCount(0);
    await expect.poll(() => landsUnderRail(page, 'Jump Saga - Ch. 040.cbz')).toBe(true);
    await expect(page.locator('.jump-menu-trigger')).toContainText('40-49');
    await expectFitsScreen(page, 'the folder after a scrub jump (phone)');

    // The last slice is "Other" (the one-shot); a release below the strip still lands on the last chip.
    await page.mouse.move(x, sliceY(7));
    await page.mouse.down();
    await expect(page.locator('.scrub .bubble-label')).toHaveText('Other');
    await page.mouse.move(x, box.y + box.height + 40, { steps: 3 });
    await page.mouse.up();
    await expect(page.locator('.node-wrap', { hasText: 'Jump Saga - Oneshot.cbz' })).toBeInViewport();

    // A short level (30 rows by a faked jump index) has the picker only.
    await page.route('**/jump-index*', async (route) => {
      await route.fulfill({
        json: {
          libraryId, parentId: folderId, direction: 'asc', mode: 'letters',
          buckets: [
            { label: 'A', count: 10, firstCursor: null, firstNodeId: null },
            { label: 'B', count: 10, firstCursor: 'x', firstNodeId: null },
            { label: 'C', count: 10, firstCursor: 'y', firstNodeId: null },
          ],
        },
      });
    });
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    await expect(page.locator('.jump-menu-trigger')).toBeVisible();
    await expect(page.locator('.scrub')).toHaveCount(0);
    await page.unroute('**/jump-index*');

    // Tablet width: the rail shows and the strip does not.
    await page.setViewportSize({ width: 820, height: 1180 });
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    await expect(page.locator('nav.jump-rail')).toBeVisible();
    await expect(page.locator('.scrub')).toBeHidden();
  } finally {
    await setDirection(page, previous === 'desc' ? 'desc' : 'asc');
  }
});
