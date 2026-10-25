import { test, expect, Page } from '@playwright/test';

import { expectFitsScreen } from './layout';
import { login, readerPrefs, routeFakeItem } from './reader-fixture';

/**
 * Vertical-mode tap zones (edges 1.41.0, owner 2026-10-11; reshaped 1.48.0, owner 2026-10-24). The left and right
 * edges (20 % of the width each, full height, corners included) step a screen; which one goes forward is the
 * per-device "Edge taps" setting (Right goes forward by default). The centre column has a short top band (15 %) = back
 * and a short bottom band (15 %) = forward; the rest of the centre shows / hides the controls.
 *
 * Same rig as `reader-top-bar.spec.ts`: the SPA and sign-in are real, the item is a synthetic tall-page archive answered
 * by `page.route` (`reader-fixture.ts`). Reduced motion makes each step an instant scroll, so `scrollTop` can be read
 * right after a tap.
 */
const ID = 'e2eedgezones';

async function openVertical(page: Page): Promise<void> {
  await readerPrefs(page, { upscaler: 'smooth', animation: 'none' });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await routeFakeItem(page, { id: ID, pageCount: 8, pageWidth: 300, pageHeight: 1200, readerMode: 'VerticalWebtoon' });
  await login(page);
  await page.goto(`/reader/${ID}`);
  await expect(page.locator('.reader-viewport.webtoon img.webtoon-page')).toHaveCount(8);
}

const strip = (page: Page) => page.locator('.reader-viewport.webtoon');
const scrollTop = (page: Page) => strip(page).evaluate((el) => Math.round(el.scrollTop));

/** One screen step at the default 90 %. */
const step = (page: Page) => strip(page).evaluate((el) => Math.round(el.clientHeight * 0.9));

/** Tap (click) the strip at a fraction of its box; asserts first that the strip is what a tap there hits. */
async function tapAt(page: Page, x: number, y: number): Promise<void> {
  const box = (await strip(page).boundingBox())!;
  const px = box.x + x * box.width;
  const py = box.y + y * box.height;
  expect(await strip(page).evaluate((el, [hx, hy]) => {
    const hit = document.elementFromPoint(hx, hy);
    return !!hit && el.contains(hit);
  }, [px, py]), `a tap at ${x} / ${y} lands on the strip`).toBe(true);
  await page.mouse.click(px, py);
}

/** Scroll the strip to `top` (as a reader would) and wait until the reader has seen it. */
async function scrollTo(page: Page, top: number): Promise<void> {
  await strip(page).evaluate((el, t) => { el.scrollTop = t; }, top);
  await expect.poll(() => scrollTop(page)).toBe(top);
}

test.afterEach(async ({ page }) => {
  await page.evaluate(() => localStorage.clear()).catch(() => undefined);
});

test('vertical mode: the left edge goes back by default, forward with "Left goes forward"; a tap near the top of an edge steps by the edge, the centre top band goes back', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await openVertical(page);
  const s = await step(page);
  await scrollTo(page, 3000);

  // Default "Right goes forward": the middle band's left edge steps BACK a screen, the right edge forward.
  await tapAt(page, 0.08, 0.5);
  await expect.poll(() => scrollTop(page)).toBe(3000 - s);
  await tapAt(page, 0.92, 0.5);
  await expect.poll(() => scrollTop(page)).toBe(3000);

  // Switch in the Display menu (vertical view: Edge taps follows Tap to scroll).
  await page.getByRole('button', { name: 'Display', exact: true }).click();
  const group = page.getByRole('group', { name: 'Edge taps', exact: true });
  await expect(group).toBeVisible();
  expect(await page.locator('.mat-mdc-menu-panel.reader-options-menu').evaluate((p) => p.scrollWidth - p.clientWidth)).toBe(0);
  for (const name of ['Edge taps: Right goes forward', 'Edge taps: Left goes forward']) {
    const item = page.getByRole('menuitemradio', { name, exact: true });
    await item.scrollIntoViewIfNeeded();
    expect(await item.evaluate((el) => {
      const r = el.getBoundingClientRect();
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      return !!hit && (hit === el || el.contains(hit));
    }), `${name} is what a tap there hits`).toBe(true);
  }
  await expect(page.getByRole('menuitemradio', { name: 'Edge taps: Right goes forward' })).toHaveAttribute('aria-checked', 'true');
  await expectFitsScreen(page, 'the Display menu in vertical mode');
  await page.getByRole('menuitemradio', { name: 'Edge taps: Left goes forward', exact: true }).click();
  await expect(page.getByRole('menuitemradio')).toHaveCount(0); // a pick closes the menu
  expect(await page.evaluate(() => localStorage.getItem('mangapixer-webtoon-forward-edge'))).toBe('left');

  // "Left goes forward": the same left-edge tap now steps FORWARD, the right edge back.
  await tapAt(page, 0.08, 0.5);
  await expect.poll(() => scrollTop(page)).toBe(3000 + s);
  await tapAt(page, 0.92, 0.5);
  await expect.poll(() => scrollTop(page)).toBe(3000);

  // 1.48.0: the edges are FULL height. A tap near the top of the left edge steps by the edge (forward now), where the
  // old top third said back; the same height in the centre column (the short top band) goes back, and the bottom band
  // forward.
  await tapAt(page, 0.03, 0.04);
  await expect.poll(() => scrollTop(page)).toBe(3000 + s);
  await scrollTo(page, 3000);
  await tapAt(page, 0.03, 0.96);
  await expect.poll(() => scrollTop(page)).toBe(3000 + s);
  await scrollTo(page, 3000);
  await tapAt(page, 0.5, 0.04);
  await expect.poll(() => scrollTop(page)).toBe(3000 - s);
  await scrollTo(page, 3000);
  await tapAt(page, 0.5, 0.96);
  await expect.poll(() => scrollTop(page)).toBe(3000 + s);
  await scrollTo(page, 3000);
  // Between the two bands the centre only shows / hides the controls (no scroll).
  await tapAt(page, 0.5, 0.5);
  await page.waitForTimeout(400);
  expect(await scrollTop(page)).toBe(3000);

  // The setting is per device: it survives a reload.
  await page.reload();
  await expect(page.locator('.reader-viewport.webtoon img.webtoon-page')).toHaveCount(8);
  await scrollTo(page, 3000);
  await tapAt(page, 0.08, 0.5);
  await expect.poll(() => scrollTop(page)).toBe(3000 + s);
});

test('phone: Reader options offers Edge taps in vertical mode, and it fits the screen', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openVertical(page);
  await page.getByRole('button', { name: 'Reader options' }).click();
  const sheet = page.locator('app-reader-options-sheet');
  const edges = sheet.getByRole('radiogroup', { name: 'Edge taps' });
  await edges.scrollIntoViewIfNeeded();
  await expect(edges.getByRole('radio', { name: /Right goes forward/ })).toHaveAttribute('aria-checked', 'true');
  const left = edges.getByRole('radio', { name: /Left goes forward/ });
  expect(await left.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!hit && (hit === el || el.contains(hit));
  }), 'Left goes forward is what a tap there hits').toBe(true);
  await expectFitsScreen(page, 'the Reader options sheet in vertical mode');
  await left.click();
  await expect(left).toHaveAttribute('aria-checked', 'true');
  expect(await page.evaluate(() => localStorage.getItem('mangapixer-webtoon-forward-edge'))).toBe('left');
});
