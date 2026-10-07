import { test, expect, Page } from '@playwright/test';

/**
 * Library sidebar height (1.36.0): on a desktop or tablet the sidebar column reaches the bottom of the window however far
 * a long page is scrolled. Before, the sticky panel was viewport-minus-toolbar tall, so once the toolbar scrolled away it
 * ended 64 px above the window's bottom edge and the page background showed below it (owner report, 1.35.1).
 * The page is made long on purpose (a tall spacer in the content column) so the check does not depend on library size.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
/** `LibrarySidebarComponent.CollapsedKey`: '1' collapsed (also the default when unset), '0' expanded. */
const COLLAPSED_KEY = 'mangapixer-nav-collapsed';

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

/**
 * Whether what is PAINTED at the point `fromBottom` px above the window's bottom edge, `x` px from its left edge, is the sidebar's
 * surface: the nearest element at that point with a visible background must be (inside) the sidebar. A box that merely covers the
 * point is not enough - before 1.36.0 the sidebar's host already stretched to the bottom, transparent, with the page showing through.
 */
async function sidebarAt(page: Page, x: number, fromBottom: number): Promise<boolean> {
  return page.evaluate(([px, dy]) => {
    let el: Element | null = document.elementFromPoint(px, window.innerHeight - dy);
    while (el) {
      const bg = getComputedStyle(el).backgroundColor;
      const transparent = bg === 'transparent' || /rgba\([^)]*,\s*0\)$/.test(bg);
      if (!transparent) return !!el.closest('app-library-sidebar');
      el = el.parentElement;
    }
    return false;
  }, [x, fromBottom] as const);
}

test.describe('Library sidebar height', () => {
  test.beforeEach(async ({ page }) => { await login(page); });

  for (const [width, height] of [[1280, 900], [820, 1180]] as const) {
    test(`${width} px: the sidebar reaches the bottom of the window on a long, scrolled page`, async ({ page }) => {
      await page.setViewportSize({ width, height });
      await page.addInitScript((key) => { try { localStorage.setItem(key, '0'); } catch { /* ignore */ } }, COLLAPSED_KEY);
      await page.goto('/');
      await expect(page.locator('app-library-sidebar nav.sidebar:not(.collapsed)')).toBeVisible();
      const sidebar = page.locator('app-library-sidebar');
      await expect(sidebar).toBeVisible();
      await page.evaluate(() => {
        const spacer = document.createElement('div');
        spacer.setAttribute('data-testid', 'e2e-spacer');
        spacer.style.height = '4000px';
        document.querySelector('main.content')!.appendChild(spacer);
      });

      for (const where of ['top', 'middle', 'bottom'] as const) {
        await page.evaluate((w) => {
          const max = document.documentElement.scrollHeight - window.innerHeight;
          window.scrollTo(0, w === 'top' ? 0 : w === 'middle' ? Math.round(max / 2) : max);
        }, where);
        await page.waitForTimeout(100);
        expect(await sidebarAt(page, 8, 4), `bottom-left corner at the ${where} of the page`).toBe(true);
        expect(await sidebarAt(page, 8, 70), `70 px above the bottom at the ${where} of the page`).toBe(true);
      }

      // The panel itself (the nav list) stays pinned to the top while scrolling.
      const top = await page.locator('app-library-sidebar nav.sidebar').evaluate((n) => n.getBoundingClientRect().top);
      expect(top).toBeLessThanOrEqual(1);
    });
  }

  test('a collapsed sidebar also reaches the bottom', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.addInitScript((key) => { try { localStorage.setItem(key, '1'); } catch { /* ignore */ } }, COLLAPSED_KEY);
    await page.goto('/');
    await expect(page.locator('app-library-sidebar nav.sidebar.collapsed')).toBeVisible();
    await page.evaluate(() => {
      const spacer = document.createElement('div');
      spacer.style.height = '4000px';
      document.querySelector('main.content')!.appendChild(spacer);
      window.scrollTo(0, document.documentElement.scrollHeight);
    });
    await page.waitForTimeout(100);
    expect(await sidebarAt(page, 8, 4)).toBe(true);
  });
});
