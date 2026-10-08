import { test, expect, Page } from '@playwright/test';

import { login, readerPrefs, routeFakeItem, withoutWebGpu } from './reader-fixture';

/**
 * Reader 1.36.0: the double-page spread with the Upscaling overlay, and (below) mouse-wheel page turns.
 *
 * Spread overlap (owner, 1.35.1): opened in Double page, a spread sometimes showed "three partial pages" until the
 * reader paged away and back. The pairing and the layout are plain CSS and reflow by themselves when the second page
 * arrives; the Upscaling overlay (Crisp is the default) is a canvas positioned over its OWN page from that page's
 * offset, so when the second page loads and the centred row shifts the first page sideways, the first page's canvas
 * must follow. The tests make the second page (or the view) arrive late on purpose and compare every overlay with the
 * rect its page actually paints.
 *
 * Same rig as `reader-rendering.spec.ts`: the SPA and sign-in are real, the item is a synthetic one answered by
 * `page.route` (`reader-fixture.ts`), WebGL2 is SwiftShader with WebGPU pinned off.
 */
const PAGE_W = 300;
const PAGE_H = 420;

test.describe.configure({ mode: 'serial' });
test.use({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1 });

interface Rect { x: number; y: number; width: number; height: number }
interface Placement { loaded: boolean; painted: Rect; canvas: Rect | null }

/** Every live page image: the rect it paints (object-fit contain) and its overlay canvas's rect when shown, in viewport px. */
async function overlayPlacement(page: Page): Promise<Placement[]> {
  return page.evaluate(() => {
    const row = document.querySelector('.spread-row:not(.outgoing)');
    if (!row) return [];
    return Array.from(row.querySelectorAll<HTMLImageElement>('img[alt="Page"]')).map((img) => {
      const r = img.getBoundingClientRect();
      const nw = img.naturalWidth;
      const nh = img.naturalHeight;
      const s = nw > 0 && nh > 0 ? Math.min(r.width / nw, r.height / nh) : 0;
      const pw = nw * s;
      const ph = nh * s;
      const painted = { x: r.left + (r.width - pw) / 2, y: r.top + (r.height - ph) / 2, width: pw, height: ph };
      const next = img.nextElementSibling;
      const canvas = next instanceof HTMLCanvasElement && getComputedStyle(next).display !== 'none'
        ? next.getBoundingClientRect() : null;
      return {
        loaded: img.complete && nw > 0,
        painted,
        canvas: canvas && { x: canvas.left, y: canvas.top, width: canvas.width, height: canvas.height },
      };
    });
  });
}

/** The worst distance (px) between a shown overlay and the page it covers. */
function misplacement(placements: Placement[]): number {
  let worst = 0;
  for (const p of placements) {
    if (!p.canvas) continue;
    worst = Math.max(worst,
      Math.abs(p.canvas.x - p.painted.x), Math.abs(p.canvas.y - p.painted.y),
      Math.abs(p.canvas.width - p.painted.width), Math.abs(p.canvas.height - p.painted.height));
  }
  return worst;
}

async function expectOverlaysOnTheirPages(page: Page): Promise<void> {
  // Both pages loaded and both overlays shown (SwiftShader: allow the first render some time)...
  await expect.poll(async () => (await overlayPlacement(page)).filter((p) => p.loaded && p.canvas).length,
    { timeout: 30_000 }).toBe(2);
  // ...and each overlay on its own page (the first page moved when the second one arrived).
  await expect.poll(async () => misplacement(await overlayPlacement(page)), { timeout: 5_000 }).toBeLessThanOrEqual(2);
  const placements = await overlayPlacement(page);
  // The two pages sit side by side, not on top of each other.
  const [a, b] = [...placements].sort((x, y) => x.painted.x - y.painted.x);
  expect(a.painted.x + a.painted.width).toBeLessThanOrEqual(b.painted.x + 2);
}

test.afterEach(async ({ page }) => {
  await page.evaluate(() => localStorage.clear()).catch(() => undefined);
});

test('double page: each Crisp overlay stays on its own page when the second page arrives late', async ({ page }) => {
  await withoutWebGpu(page);
  await readerPrefs(page, { upscaler: 'sharp' });
  await routeFakeItem(page, {
    id: 'e2espreadlate', pageCount: 4, pageWidth: PAGE_W, pageHeight: PAGE_H,
    readerMode: 'DoubleSpread', spreadStarts: [], delayMs: { p1: 1500 },
  });
  await login(page);
  await page.goto('/reader/e2espreadlate');
  await expectOverlaysOnTheirPages(page);
});

test('double page: the overlays follow a switch from single page after the first page was shown', async ({ page }) => {
  await withoutWebGpu(page);
  await readerPrefs(page, { upscaler: 'sharp' });
  // The effective view answers late (the first page is already shown in single page), the second page later still.
  await routeFakeItem(page, {
    id: 'e2espreadswitch', pageCount: 4, pageWidth: PAGE_W, pageHeight: PAGE_H,
    readerMode: 'DoubleSpread', spreadStarts: [], modeDelayMs: 1500, delayMs: { p1: 3000 },
  });
  await login(page);
  await page.goto('/reader/e2espreadswitch');
  await expectOverlaysOnTheirPages(page);
});

// --- Mouse-wheel page turns (1.36.0) -------------------------------------------------------------------------------
// In a fixed view (the page fits the screen) the wheel turns the page, one page per gesture; a page taller than the
// screen (fit width) scrolls natively and never turns. Smooth (no overlay) keeps these independent of WebGL.

/** The toolbar's page counter, e.g. "2 / 4". */
const pageInfo = (page: Page) => page.locator('.reader-toolbar .page-info');

async function openFakeReader(page: Page, id: string): Promise<void> {
  await readerPrefs(page, { upscaler: 'smooth', animation: 'none' });
  await routeFakeItem(page, { id, pageCount: 4, pageWidth: PAGE_W, pageHeight: PAGE_H, readerMode: 'PagedLtr' });
  await login(page);
  await page.goto(`/reader/${id}`);
  await expect(pageInfo(page)).toHaveText('1 / 4');
  await expect(page.locator('.spread-row:not(.outgoing) img[alt="Page"]')).toHaveCount(1);
  // The pointer rests over the page, as a reader's mouse would.
  await page.mouse.move(640, 450);
}

test('wheel: in fit screen one wheel step turns one page, down = next and up = previous', async ({ page }) => {
  await openFakeReader(page, 'e2ewheelfit');
  await page.mouse.wheel(0, 120);
  await expect(pageInfo(page)).toHaveText('2 / 4');
  await page.waitForTimeout(400); // past the quiet gap: the next step is a new gesture
  await page.mouse.wheel(0, 120);
  await expect(pageInfo(page)).toHaveText('3 / 4');
  await page.waitForTimeout(400);
  await page.mouse.wheel(0, -120);
  await expect(pageInfo(page)).toHaveText('2 / 4');
  // Nothing scrolled: the page still fits the screen.
  expect(await page.locator('.reader-viewport').evaluate((el) => el.scrollTop)).toBe(0);
});

test('wheel: a trackpad flick (a long inertial stream of wheel events) turns exactly one page', async ({ page }) => {
  await openFakeReader(page, 'e2ewheelflick');
  // 60 events a frame apart with decaying deltas, like a trackpad's momentum phase (synthetic: the browser's own input
  // pipeline cannot replay momentum, but the reader sees the same event shape and timing).
  await page.locator('.reader-viewport').evaluate(async (el) => {
    for (let i = 0; i < 60; i++) {
      el.dispatchEvent(new WheelEvent('wheel', { deltaY: Math.max(1, 80 - i), bubbles: true, cancelable: true }));
      await new Promise((resolve) => setTimeout(resolve, 16));
    }
  });
  await expect(pageInfo(page)).toHaveText('2 / 4');
  await page.waitForTimeout(500);
  await expect(pageInfo(page)).toHaveText('2 / 4');
});

test('wheel: in fit width a tall page scrolls natively and never turns', async ({ page }) => {
  await openFakeReader(page, 'e2ewheelwidth');
  await page.getByRole('button', { name: 'Image fit' }).click();
  await page.getByRole('menuitemradio', { name: /Fit width/ }).click();
  await expect(page.getByRole('menuitemradio', { name: /Fit width/ })).toHaveCount(0);
  const viewport = page.locator('.reader-viewport');
  // The page is now taller than the screen.
  await expect.poll(() => viewport.evaluate((el) => el.scrollHeight - el.clientHeight)).toBeGreaterThan(200);
  await page.mouse.move(640, 450);
  await page.mouse.wheel(0, 300);
  await expect.poll(() => viewport.evaluate((el) => el.scrollTop)).toBeGreaterThan(0);
  await page.waitForTimeout(400);
  await page.mouse.wheel(0, 300);
  await page.waitForTimeout(400);
  await expect(pageInfo(page)).toHaveText('1 / 4');
});
