import { test, expect, Locator, Page } from '@playwright/test';

import { expectFitsScreen } from './layout';
import { login, readerPrefs, routeFakeItem } from './reader-fixture';

/**
 * Reader top bar 1.41.0 (owner decisions 5-7): the grouped bar (Series info, Bookmark menu, Display menu, Quality
 * menu), the phone sheet with the same sections, and a double tap / double click in the centre for fullscreen.
 *
 * Same rig as `reader-spread-wheel.spec.ts`: the SPA and sign-in are real, the item is a synthetic one answered by
 * `page.route` (`reader-fixture.ts`), plus a `series-info` answer so the Series info control has something to show.
 * Headless Chromium may refuse the real Fullscreen API, so `fakeFullscreen` stands in for the browser's half of it
 * (it sets `document.fullscreenElement` and fires `fullscreenchange`, as a browser does); what is asserted is what
 * the reader shows.
 */
const ID = 'e2etopbar';
const SERIES = 'e2etopbarseries';

async function fakeFullscreen(page: Page): Promise<void> {
  await page.addInitScript(() => {
    let element: Element | null = null;
    Object.defineProperty(Document.prototype, 'fullscreenElement', { configurable: true, get: () => element });
    Element.prototype.requestFullscreen = function (this: Element) {
      element = this;
      document.dispatchEvent(new Event('fullscreenchange'));
      return Promise.resolve();
    };
    Document.prototype.exitFullscreen = function () {
      element = null;
      document.dispatchEvent(new Event('fullscreenchange'));
      return Promise.resolve();
    };
  });
}

/** Opens the synthetic archive; returns the page indexes of every progress save the reader sends. */
async function openReader(page: Page, opts: { series?: boolean; mode?: string } = {}): Promise<number[]> {
  await readerPrefs(page, { upscaler: 'smooth', animation: 'none' });
  await fakeFullscreen(page);
  await routeFakeItem(page, { id: ID, pageCount: 6, pageWidth: 300, pageHeight: 420, readerMode: opts.mode ?? 'PagedLtr' });
  const saves: number[] = [];
  await page.route(`**/api/v1/reading/progress/${ID}`, (r) => {
    if (r.request().method() === 'PUT') {
      saves.push((r.request().postDataJSON() as { pageIndex: number }).pageIndex);
      return r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ revision: 1, alreadyApplied: false }) });
    }
    return r.fallback();
  });
  await page.route(`**/api/v1/nodes/${ID}/series-info*`, (r) => r.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify(opts.series === false
      ? { nodeId: ID, nodeKind: 'Archive', anchorNodeId: ID, anchorKind: 'Archive', anchorDisplayName: 'x', libraryId: 'e2elibrary', state: 'None', precedence: 'WebFirst', precedenceSource: 'Default' }
      : { nodeId: ID, nodeKind: 'Archive', anchorNodeId: SERIES, anchorKind: 'Folder', anchorDisplayName: 'Synthetic Saga', libraryId: 'e2elibrary', state: 'Web', title: 'Synthetic Saga', precedence: 'WebFirst', precedenceSource: 'Default' }),
  }));
  await login(page);
  await page.goto(`/reader/${ID}`);
  await expect(page.locator('.reader-toolbar .page-info')).toHaveText('1 / 6');
  return saves;
}

const barLabels = (page: Page) => page.locator('.reader-toolbar button').evaluateAll(
  (buttons) => buttons.map((b) => b.getAttribute('aria-label')));

/** What a tap at the centre of `selector`'s box lands on: the element itself (or inside it)? */
async function tapHits(page: Page, target: string | Locator): Promise<boolean> {
  // A Locator (e.g. by role) addresses the VISIBLE element; a CSS selector's first match can be a menu panel that is
  // still fading out after Escape (seen under load on the shared daemon).
  const locator = typeof target === 'string' ? page.locator(target).first() : target;
  return locator.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!hit && (hit === el || el.contains(hit));
  });
}

test.afterEach(async ({ page }) => {
  await page.evaluate(() => localStorage.clear()).catch(() => undefined);
});

test.describe('desktop and tablet bar', () => {
  for (const viewport of [{ width: 1280, height: 900 }, { width: 820, height: 1180 }]) {
    test(`${viewport.width} px: grouped bar, Display and Quality menus over the page`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await openReader(page);
      await expect(page.getByRole('button', { name: 'Series info', exact: true })).toBeVisible();
      expect(await barLabels(page)).toEqual([
        'Back to folder', 'No previous archive', 'No next archive', 'Series info', 'Bookmark menu', 'Add to favorites',
        'Display', 'Quality', 'Reading help', 'Enter fullscreen',
      ]);
      await expectFitsScreen(page, 'the reader bar');

      await page.getByRole('button', { name: 'Display', exact: true }).click();
      for (const group of ['Layout', 'Image fit', 'Reading direction', 'Page transition']) {
        await expect(page.getByRole('group', { name: group, exact: true })).toBeVisible();
      }
      await expect(page.getByRole('menuitemradio', { name: 'Layout: Single page' })).toHaveAttribute('aria-checked', 'true');
      // Every item a tap could hit sits on top of the page, inside the screen.
      for (const name of ['Layout: Single page', 'Image fit: Fit width', 'Reading direction: Right to left', 'Page transition: None']) {
        const item = page.getByRole('menuitemradio', { name, exact: true });
        await item.scrollIntoViewIfNeeded();
        expect(await item.evaluate((el) => {
          const r = el.getBoundingClientRect();
          const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
          return !!hit && (hit === el || el.contains(hit));
        }), `${name} is what a tap there hits`).toBe(true);
      }
      await expectFitsScreen(page, 'the Display menu');
      await page.getByRole('menuitemradio', { name: 'Image fit: Fit width', exact: true }).click();
      await expect(page.getByRole('menuitemradio')).toHaveCount(0); // a pick closes the menu

      await page.getByRole('button', { name: 'Quality', exact: true }).click();
      for (const group of ['Upscaling', 'Page quality', 'Downscale filter']) {
        await expect(page.getByRole('group', { name: group, exact: true })).toBeVisible();
      }
      await expectFitsScreen(page, 'the Quality menu');
      await page.keyboard.press('Escape');
      await expect(page.getByRole('menuitemradio')).toHaveCount(0);
      await expect(page).toHaveURL(new RegExp(`/reader/${ID}$`)); // Escape closed the menu, not the reader

      await page.getByRole('button', { name: 'Bookmark menu', exact: true }).click();
      const bookmarkItem = page.getByRole('menuitem', { name: 'Bookmark this page' });
      await expect(bookmarkItem).toBeVisible();
      expect(await tapHits(page, bookmarkItem)).toBe(true);
      await page.keyboard.press('Escape');
    });
  }

  test('Series info opens the series page and the reading position is saved on the way out', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    const saves = await openReader(page);
    await page.locator('.edge.next').click();
    await expect(page.locator('.reader-toolbar .page-info')).toHaveText('2 / 6');
    await page.getByRole('button', { name: 'Series info', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/series/${SERIES}$`));
    await expect.poll(() => saves.at(-1)).toBe(1);
  });

  test('no Series info when the archive has no series information', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openReader(page, { series: false });
    await expect(page.getByRole('button', { name: 'Bookmark menu', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Series info' })).toHaveCount(0);
  });

  test('webtoon: the Display menu has the Page width slider, reachable with Tab', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openReader(page, { mode: 'VerticalWebtoon' });
    await page.getByRole('button', { name: 'Display', exact: true }).click();
    await expect(page.getByRole('group', { name: 'Page width', exact: true })).toBeVisible();
    await expect(page.getByRole('group', { name: 'Image fit', exact: true })).toHaveCount(0);
    // No sideways scroll in the panel: the slider's (invisible) range input must fit inside it too (owner review, 1.41.0).
    expect(await page.locator('.mat-mdc-menu-panel.reader-options-menu').evaluate((p) => p.scrollWidth - p.clientWidth)).toBe(0);
    const slider = page.getByRole('slider', { name: 'Webtoon page width' });
    await page.keyboard.press('Tab');
    await expect(slider).toBeFocused();
    const before = Number(await slider.inputValue());
    await page.keyboard.press('ArrowRight');
    await expect.poll(async () => Number(await slider.inputValue())).toBe(before + 5);
    await expect(slider).toBeFocused(); // the arrow stayed with the slider; the menu is still open
    const width = await page.locator('img.webtoon-page').first().evaluate((img) => img.style.width);
    expect(width).toBe(`${before + 5}%`);
  });
});

test.describe('double click / double tap in the centre', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test('a double click toggles fullscreen, without turning the page, selecting text or hiding the controls', async ({ page }) => {
    await openReader(page);
    const centre = page.locator('.tap-toggle');
    expect(await centre.evaluate((el) => getComputedStyle(el).touchAction)).toBe('manipulation'); // no browser double-tap zoom
    await centre.dblclick();
    await expect(page.getByRole('button', { name: 'Exit fullscreen' })).toBeVisible();
    await expect(page.locator('.reader-top.immersive')).toHaveCount(1);
    await page.waitForTimeout(500); // past the double-tap window: the two clicks were not also a single tap
    await expect(page.locator('.reader-top.chrome-hidden')).toHaveCount(0);
    await expect(page.locator('.reader-toolbar .page-info')).toHaveText('1 / 6');
    expect(await page.evaluate(() => window.getSelection()?.toString() ?? '')).toBe('');

    // A single click in the centre hides the controls (after the window), as before.
    await centre.click();
    await expect(page.locator('.reader-top.chrome-hidden')).toHaveCount(1);
    // And a second double click leaves fullscreen.
    await centre.dblclick();
    await expect(page.getByRole('button', { name: 'Enter fullscreen' })).toBeVisible();
    await expect(page.locator('.reader-top.immersive')).toHaveCount(0);
    await expect(page.locator('.reader-toolbar .page-info')).toHaveText('1 / 6');
  });

  test('the side zones still turn the page at once on a double click (no fullscreen there)', async ({ page }) => {
    await openReader(page);
    await page.locator('.edge.next').dblclick();
    await expect(page.locator('.reader-toolbar .page-info')).toHaveText('3 / 6');
    await expect(page.getByRole('button', { name: 'Enter fullscreen' })).toBeVisible();
  });

  test('webtoon: a double click in the middle of the screen toggles fullscreen', async ({ page }) => {
    await openReader(page, { mode: 'VerticalWebtoon' });
    const strip = page.locator('.reader-viewport.webtoon');
    const box = (await strip.boundingBox())!;
    await page.mouse.dblclick(box.x + box.width / 2, box.y + box.height / 2);
    await expect(page.getByRole('button', { name: 'Exit fullscreen' })).toBeVisible();
  });
});

test('phone: the sheet is grouped like the bar and offers Series info', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openReader(page);
  expect(await barLabels(page)).toEqual(['Back to folder', 'No next archive', 'Enter fullscreen', 'Reader options']);
  await page.getByRole('button', { name: 'Reader options' }).click();
  const sheet = page.locator('app-reader-options-sheet');
  await expect(sheet.getByRole('heading', { name: 'Display', exact: true })).toBeVisible();
  await expect(sheet.getByRole('heading', { name: 'Quality', exact: true })).toBeVisible();
  const series = sheet.getByRole('button', { name: 'Series info', exact: true });
  await expect(series).toBeVisible();
  expect(await tapHits(page, 'app-reader-options-sheet button[aria-label="Series info"]')).toBe(true);
  await expectFitsScreen(page, 'the Reader options sheet');
  await series.click();
  await expect(page).toHaveURL(new RegExp(`/series/${SERIES}$`));
});
