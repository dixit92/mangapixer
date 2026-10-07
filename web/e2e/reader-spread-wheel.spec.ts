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
