import { test, expect, Page } from '@playwright/test';

import { expectFitsScreen } from './layout';
import { login, readerPrefs, routeFakeItem } from './reader-fixture';

/**
 * Reader jump back (1.54.0, owner request 2026-11-17: an iPad swipe-up to leave the app began on the page slider and jumped the
 * reader - and saved the jumped-to page). On the real reader, with TOUCH at iPad and phone sizes:
 * - a touch the browser / OS takes (a real `touchStart` + `touchCancel` through CDP: Chromium sends pointerdown + pointercancel,
 *   as iPadOS does when it takes the swipe) jumps nowhere and saves nothing;
 * - a tap jumps when the finger lifts; the bar's back button and the chip lead back; Back flips to Return; Backspace;
 * - leaving the app within 2 s of a jump saves the page jumped FROM; the chip asks on return; a reload offers Return.
 * Same rig as `reader-top-bar.spec.ts` (`reader-fixture.ts`; the item's progress answer is always page 1).
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ID = 'e2ejumpback';
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const PAGES = 200;

async function openReader(page: Page): Promise<number[]> {
  await readerPrefs(page, { upscaler: 'smooth', animation: 'none' });
  await routeFakeItem(page, { id: ID, pageCount: PAGES, pageWidth: 300, pageHeight: 420, readerMode: 'PagedLtr' });
  const saves: number[] = [];
  await page.route(`**/api/v1/reading/progress/${ID}`, (r) => {
    if (r.request().method() === 'PUT') {
      saves.push((r.request().postDataJSON() as { pageIndex: number }).pageIndex);
      return r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ revision: 1, alreadyApplied: false }) });
    }
    return r.fallback();
  });
  await login(page);
  await page.goto(`/reader/${ID}`);
  await expect(page.locator('.reader-toolbar .page-info')).toHaveText(`1 / ${PAGES}`);
  return saves;
}

/** The viewport point of the slider at a 0-based page (the thumb travels the track inset by its 11 px radius). */
async function sliderPoint(page: Page, index: number): Promise<{ x: number; y: number }> {
  const box = (await page.locator('.reader-nav .scrub').boundingBox())!;
  return { x: box.x + 11 + (index / (PAGES - 1)) * (box.width - 22), y: box.y + box.height / 2 };
}

async function setVisibility(page: Page, state: 'hidden' | 'visible'): Promise<void> {
  await page.evaluate((s) => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => s });
    document.dispatchEvent(new Event('visibilitychange'));
  }, state);
}

async function shot(page: Page, name: string): Promise<void> {
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/jump-back-${name}.png` });
}

for (const vp of [{ name: 'ipad', width: 1180, height: 820 }, { name: 'phone', width: 390, height: 844 }]) {
  test.describe(`reader jump back - ${vp.name} ${vp.width} x ${vp.height}, touch`, () => {
    test.use({ viewport: { width: vp.width, height: vp.height }, hasTouch: true, isMobile: true });

    test('a cancelled touch does not jump; a tap jumps on lift; Back / Return in the bar and the chip; Backspace', async ({ page }) => {
      const saves = await openReader(page);
      const info = page.locator('.reader-toolbar .page-info');

      // The iPad case: the finger lands on the slider, then the OS takes the touch.
      const at150 = await sliderPoint(page, 149);
      const cdp = await page.context().newCDPSession(page);
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: at150.x, y: at150.y }] });
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
      await page.waitForTimeout(300);
      await expect(info).toHaveText(`1 / ${PAGES}`);
      expect(saves).toEqual([]);
      await expect(page.locator('app-jump-back-button')).toHaveCount(0);

      // A tap: the jump lands on lift, is saved, and offers the way back.
      await page.touchscreen.tap(at150.x, at150.y);
      await expect(info).toHaveText(`150 / ${PAGES}`);
      await expect.poll(() => saves.at(-1)).toBe(149);
      const chip = page.locator('app-jump-back-chip');
      await expect(chip.getByRole('status')).toContainText(vp.name === 'phone' ? 'From p. 1' : 'Jumped from page 1', { useInnerText: true });
      await expect(chip).toBeInViewport({ ratio: 1 });
      await expect(page.locator('app-jump-back-button button')).toHaveAttribute('aria-label', 'Back to page 1');
      await expect(page.locator('.scrub-tick')).toHaveCount(1);
      await expectFitsScreen(page, `${vp.name} after a jump`);
      await shot(page, `${vp.name}-after-jump`);

      // The chip's button: back to page 1 (saved), and the offer flips to Return.
      await chip.locator('button.go').tap();
      await expect(info).toHaveText(`1 / ${PAGES}`);
      await expect.poll(() => saves.at(-1)).toBe(0);
      await expect(page.locator('app-jump-back-button button')).toHaveAttribute('aria-label', 'Return to page 150');
      await expect(chip.getByRole('status')).toContainText(vp.name === 'phone' ? 'Back on p. 1' : 'Back on page 1', { useInnerText: true });
      await shot(page, `${vp.name}-after-back`);

      // The bar's button returns; Backspace goes back again.
      await page.locator('app-jump-back-button button').tap();
      await expect(info).toHaveText(`150 / ${PAGES}`);
      await page.keyboard.press('Backspace');
      await expect(info).toHaveText(`1 / ${PAGES}`);

      // The chip's x: the chip goes, the bar's button stays.
      await chip.locator('button.x').tap();
      await expect(chip).toHaveCount(0);
      await expect(page.locator('app-jump-back-button button')).toHaveAttribute('aria-label', 'Return to page 150');
    });

    test('leaving within 2 s of a jump keeps the saved place; the chip asks on return; a reload offers Return', async ({ page }) => {
      const saves = await openReader(page);
      const info = page.locator('.reader-toolbar .page-info');
      const at150 = await sliderPoint(page, 149);
      await page.touchscreen.tap(at150.x, at150.y);
      await expect(info).toHaveText(`150 / ${PAGES}`);
      await expect.poll(() => saves.at(-1)).toBe(149);

      await setVisibility(page, 'hidden');
      await expect.poll(() => saves.at(-1)).toBe(0);
      await setVisibility(page, 'visible');
      const chip = page.locator('app-jump-back-chip');
      await expect(chip.getByRole('status')).toContainText(vp.name === 'phone' ? 'From p. 1' : 'You jumped just before leaving', { useInnerText: true });
      await expect(info).toHaveText(`150 / ${PAGES}`);
      await shot(page, `${vp.name}-left-and-back`);

      // A suspended Home Screen app may be reloaded: it resumes on the saved page 1, and offers Return to 150.
      await page.reload();
      await expect(info).toHaveText(`1 / ${PAGES}`);
      await expect(page.locator('app-jump-back-button button')).toHaveAttribute('aria-label', 'Return to page 150');
    });
  });
}
