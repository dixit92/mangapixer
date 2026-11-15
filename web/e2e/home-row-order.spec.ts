import { test, expect, Page, Locator } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Home row order (1.54.0, owner decision 3): Settings' "Home" card lists the five Home rows with Move up / Move down; Home renders its
 * rows in that order (DOM order = screen order, so a screen reader and Tab follow it too); the order is saved for the user (it survives a
 * reload) and Reset brings back the default. At 1280 px and at 390 px with touch. Leaves the admin on the default order.
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots (dark and light).
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const DEFAULT = ['continue', 'recommended', 'favorites', 'recent', 'libraries'];
const LABELS = ['Continue reading', 'Recommended for you', 'Favorites', 'New chapters', 'Libraries'];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];

test.describe.configure({ mode: 'serial' });

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

/** One screenshot per theme: the page as it is (dark, the default), then reloaded in light with `reopen`, then back to dark. */
async function shots(page: Page, name: string, reopen: () => Promise<void>): Promise<void> {
  if (!SHOTS) return;
  const width = page.viewportSize()?.width ?? 0;
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/h-${name}-${width}-dark.png` });
  await page.route('**/api/v1/reading/preferences', async (route) => {
    if (route.request().method() !== 'GET') return route.continue();
    const response = await route.fetch();
    await route.fulfill({ response, json: { ...(await response.json()), theme: 'light' } });
  });
  await page.reload();
  await reopen();
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/h-${name}-${width}-light.png` });
  await page.unroute('**/api/v1/reading/preferences');
  await page.reload();
}

async function homeOrder(page: Page): Promise<string[]> {
  return page.locator('[data-home-row]').evaluateAll((els) => els.map((e) => e.getAttribute('data-home-row') ?? ''));
}

async function cardOrder(card: Locator): Promise<string[]> {
  return card.getByTestId('home-row-item').evaluateAll((els) => els.map((e) => e.getAttribute('data-row') ?? ''));
}

async function openCard(page: Page): Promise<Locator> {
  await page.goto('/settings');
  const card = page.getByTestId('home-rows-card');
  await card.scrollIntoViewIfNeeded();
  await expect(card.getByTestId('home-row-item')).toHaveCount(5);
  return card;
}

/** Moves a row by clicking its Move up button and waits for the save. */
async function moveUp(page: Page, card: Locator, label: string): Promise<void> {
  const saved = page.waitForResponse((r) => r.url().endsWith('/api/v1/reading/library-preferences') && r.request().method() === 'PUT');
  await card.getByRole('button', { name: `Move ${label} up` }).click();
  expect((await saved).ok()).toBe(true);
}

for (const vp of [{ name: '1280 px', width: 1280, height: 900, touch: false }, { name: '390 px', width: 390, height: 844, touch: true }]) {
  test.describe(vp.name, () => {
    test.use({ viewport: { width: vp.width, height: vp.height }, hasTouch: vp.touch, isMobile: vp.touch });

    test('reorder the Home rows in Settings; Home follows; the order is saved; Reset restores it', async ({ page }) => {
      await login(page);
      const card = await openCard(page);
      expect(await cardOrder(card)).toEqual(DEFAULT);
      for (const [i, label] of LABELS.entries()) {
        await expect(card.getByTestId('home-row-item').nth(i)).toContainText(label);
      }
      // The ends cannot move further.
      await expect(card.getByRole('button', { name: 'Move Continue reading up' })).toBeDisabled();
      await expect(card.getByRole('button', { name: 'Move Libraries down' })).toBeDisabled();
      if (vp.touch) {
        const box = (await card.getByRole('button', { name: 'Move Libraries up' }).boundingBox())!;
        expect(box.width).toBeGreaterThanOrEqual(40);
        expect(box.height).toBeGreaterThanOrEqual(40);
        await expectFitsScreen(page, 'Settings with the Home card');
      }

      // Libraries to the top: four moves; focus stays on the moved row's button (until it reaches the top and that button disables).
      await moveUp(page, card, 'Libraries');
      await expect(card.getByRole('button', { name: 'Move Libraries up' })).toBeFocused();
      await moveUp(page, card, 'Libraries');
      await moveUp(page, card, 'Libraries');
      await moveUp(page, card, 'Libraries');
      const moved = ['libraries', 'continue', 'recommended', 'favorites', 'recent'];
      expect(await cardOrder(card)).toEqual(moved);
      await expect(card.getByRole('button', { name: 'Move Libraries down' })).toBeFocused();
      await shots(page, 'settings-home-card', async () => { await page.getByTestId('home-rows-card').scrollIntoViewIfNeeded(); });

      // Home renders in that order: in the DOM and on screen.
      await page.goto('/');
      await expect(page.locator('[data-home-row]')).toHaveCount(5);
      expect(await homeOrder(page)).toEqual(moved);
      const libraries = page.getByRole('heading', { name: 'Libraries', exact: true });
      const recent = page.getByRole('heading', { name: 'New chapters', exact: true });
      await expect(libraries).toBeVisible();
      if (await recent.count()) {
        expect((await libraries.boundingBox())!.y).toBeLessThan((await recent.boundingBox())!.y);
      }
      if (vp.touch) await expectFitsScreen(page, 'Home with Libraries first');
      await shots(page, 'home-libraries-first', async () => { await expect(page.locator('[data-home-row]')).toHaveCount(5); });

      // Saved for the user: still there after a reload, in Settings too.
      await page.reload();
      await expect(page.locator('[data-home-row]')).toHaveCount(5);
      expect(await homeOrder(page)).toEqual(moved);
      const again = await openCard(page);
      expect(await cardOrder(again)).toEqual(moved);

      // Reset.
      const saved = page.waitForResponse((r) => r.url().endsWith('/api/v1/reading/library-preferences') && r.request().method() === 'PUT');
      await again.getByRole('button', { name: 'Reset to default order' }).click();
      expect((await saved).ok()).toBe(true);
      expect(await cardOrder(again)).toEqual(DEFAULT);
      await page.goto('/');
      await expect(page.locator('[data-home-row]')).toHaveCount(5);
      expect(await homeOrder(page)).toEqual(DEFAULT);
    });
  });
}
