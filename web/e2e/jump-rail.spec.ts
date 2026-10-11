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
 * Whether a jump landed on `title`: scrolled in place to just under the rail (the card was loaded), or the list was reloaded from the
 * range's cursor and starts with it (it was not - which depends on how far infinite scroll has loaded, so both count).
 */
async function landed(page: Page, title: string): Promise<boolean> {
  if (await landsUnderRail(page, title)) return true;
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
    await rail.locator('.jump-chip', { hasText: /^30$/ }).click();
    await expect.poll(() => landsUnderRail(page, 'Jump Saga - Ch. 030.cbz')).toBe(true);
    await expect(rail.locator('.jump-chip.active')).toHaveText('30');

    // The rail stays on top of the cards it overlays: a tap on a chip hits the chip, not a card.
    const box = (await rail.locator('.jump-chip', { hasText: /^60$/ }).boundingBox())!;
    const hit = await page.evaluate(([x, y]) => document.elementFromPoint(x, y)?.closest('.jump-chip')?.textContent?.trim() ?? null,
      [box.x + box.width / 2, box.y + box.height / 2]);
    expect(hit).toBe('60');

    // 60 may be past what is loaded (50 cards a page): then the range cursor reloads the list from Ch. 060.
    await rail.locator('.jump-chip', { hasText: /^60$/ }).click();
    await expect.poll(() => landed(page, 'Jump Saga - Ch. 060.cbz')).toBe(true);
    await rail.locator('.jump-chip', { hasText: 'Other' }).click();
    await expect(page.locator('.node-wrap', { hasText: 'Jump Saga - Oneshot.cbz' })).toBeInViewport();
    await expectFitsScreen(page, 'the jump rail (desktop)');
    await shot(page, 'jump-rail-desktop');

    // Descending: the rail is reversed and each chip lands on its range's first chapter in that order.
    await setDirection(page, 'desc');
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    await expect.poll(() => chips(page)).toEqual([...ASC].reverse());
    await expect(page.locator('.node-wrap').first().locator('.node-title')).toHaveText('Jump Saga - Oneshot.cbz');
    await rail.locator('.jump-chip', { hasText: /^50$/ }).click();
    await expect.poll(() => landsUnderRail(page, 'Jump Saga - Ch. 059.cbz')).toBe(true);
    await rail.locator('.jump-chip', { hasText: /^0$/ }).click();
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
    await picker.filter({ hasText: '40-49' }).click();
    await expect(page.locator('.jump-picker-menu')).toHaveCount(0); // the picker closes on a pick
    await expect.poll(() => landsUnderRail(page, 'Jump Saga - Ch. 040.cbz')).toBe(true);
    await expect(trigger).toContainText('40-49');
    await expectFitsScreen(page, 'the folder after a jump (phone)');
    await shot(page, 'jump-rail-phone');
  } finally {
    await setDirection(page, previous === 'desc' ? 'desc' : 'asc');
  }
});
