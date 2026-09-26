import { test, expect, Page } from '@playwright/test';
import { deflateSync } from 'node:zlib';

/**
 * Reader Upscaling on WebGL2 (1.25.0): Crisp (AMD FSR 1) and Enhance without
 * WebGPU, in headless Chromium, whose WebGL2 is SwiftShader (Playwright passes
 * --enable-unsafe-swiftshader) - usable for correctness and wiring, not speed.
 *
 * Runs against the default E2E instance (empty media, see Verify-E2E.ps1): the
 * SPA and sign-in are real; the reader's item endpoints for one made-up item id
 * are answered by `page.route` with a synthetic small page (a PNG built below:
 * line art, no real content), so the page is shown ~2x larger than its natural
 * size and the Upscaling overlay has work to do.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const ITEM = 'e2erenderingitem';
const PAGE_W = 300;
const PAGE_H = 420;

test.describe.configure({ mode: 'serial' });
test.use({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1 });

// --- a tiny PNG encoder (RGB, no dependencies) --------------------------------

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
function crc32(bytes: Buffer): number {
  let c = 0xffffffff;
  for (const b of bytes) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
function chunk(type: string, data: Buffer): Buffer {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

/** Synthetic line art: concentric rings, a diagonal stroke and a screentone patch on white. */
function linePage(): Buffer {
  const rows: Buffer[] = [];
  for (let y = 0; y < PAGE_H; y++) {
    const row = Buffer.alloc(1 + PAGE_W * 3, 255);
    row[0] = 0; // filter: none
    for (let x = 0; x < PAGE_W; x++) {
      const r = Math.hypot(x - PAGE_W / 2, y - PAGE_H / 2);
      const ring = r > 20 && r % 14 < 1.6;
      const diagonal = Math.abs(y - x * (PAGE_H / PAGE_W)) < 1.5;
      const tone = x > 220 && y < 80 && x % 4 === 0 && y % 4 === 0;
      if (ring || diagonal || tone) row.fill(0, 1 + x * 3, 4 + x * 3);
    }
    rows.push(row);
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(PAGE_W, 0);
  header.writeUInt32BE(PAGE_H, 4);
  header[8] = 8; // bit depth
  header[9] = 2; // colour type: RGB
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header), chunk('IDAT', deflateSync(Buffer.concat(rows))), chunk('IEND', Buffer.alloc(0)),
  ]);
}

// --- the fake item ------------------------------------------------------------

async function routeItem(page: Page): Promise<void> {
  const png = linePage();
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  const progress = {
    itemId: ITEM, pageIndex: 0, contentVersion: 1, updatedAt: '2026-01-01T00:00:00Z', state: 'Unread',
    revision: 0, isStale: false, openPageIndex: 0,
  };
  await page.route(`**/api/v1/items/${ITEM}/manifest`, (r) => r.fulfill(json({
    itemId: ITEM, contentVersion: 1, manifestVersion: 1, archiveFormat: 'Zip', pageCount: 2, isSolid: false,
    hasAnimatedPages: false, spreadStarts: null,
    pages: [0, 1].map((i) => ({
      entryKey: `p${i}`, pageIndex: i, mediaType: 'image/png', width: PAGE_W, height: PAGE_H, animationState: 'Static', byteSize: png.length,
    })),
  })));
  await page.route(`**/api/v1/items/${ITEM}/pages/**`, (r) => r.fulfill({ status: 200, contentType: 'image/png', body: png }));
  await page.route(`**/api/v1/reading/progress/${ITEM}`, (r) =>
    r.fulfill(json(r.request().method() === 'GET' ? progress : { revision: 1, alreadyApplied: false })));
  await page.route(`**/api/v1/reading/${ITEM}/effective-mode`, (r) => r.fulfill(json({ readerMode: 'PagedLtr' })));
  await page.route(`**/api/v1/reading/${ITEM}/bookmarks`, (r) => r.fulfill(json([])));
  await page.route(`**/api/v1/nodes/${ITEM}/neighbors`, (r) => r.fulfill(json({ previous: null, next: null })));
  await page.route(`**/api/v1/nodes/${ITEM}`, (r) => r.fulfill(json({
    id: ITEM, parentId: 'e2eparent', libraryId: 'e2elibrary', kind: 'Archive', displayName: 'Synthetic page',
    availability: 'Available', coverUrl: null, childFolderCount: null, childArchiveCount: null, pageCount: 2,
    readingState: 'Unread', lastReadPage: null, readerDefault: null, isRead: false, isFavorite: false,
  })));
}

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

/**
 * Headless Chromium may or may not expose WebGPU; these tests pin "no WebGPU". Its WebGL
 * runs on SwiftShader (software), which the app treats as "Graphics chip unavailable" for
 * Enhance - so the GPU kind is pinned too: `hardware` drops `failIfMajorPerformanceCaveat`
 * (the probe then sees a hardware context), `software` refuses such contexts.
 */
async function withoutWebGpu(page: Page, noFloatTargets = false, gpu: 'hardware' | 'software' = 'hardware'): Promise<void> {
  await page.addInitScript(([noFloat, kind]: [boolean, string]) => {
    Object.defineProperty(Navigator.prototype, 'gpu', { configurable: true, get: () => undefined });
    const getContext = HTMLCanvasElement.prototype.getContext as (this: HTMLCanvasElement, id: string, opts?: Record<string, unknown>) => unknown;
    HTMLCanvasElement.prototype.getContext = function (this: HTMLCanvasElement, id: string, opts?: Record<string, unknown>) {
      if (id === 'webgl2' && opts?.['failIfMajorPerformanceCaveat']) {
        if (kind === 'software') return null;
        const { failIfMajorPerformanceCaveat: _drop, ...rest } = opts;
        return getContext.call(this, id, rest);
      }
      return getContext.call(this, id, opts);
    } as typeof HTMLCanvasElement.prototype.getContext;
    if (noFloat) {
      const proto = WebGL2RenderingContext.prototype as unknown as { getExtension(name: string): unknown };
      const original = proto.getExtension;
      proto.getExtension = function (this: WebGL2RenderingContext, name: string) {
        return /^EXT_color_buffer_(half_)?float$/.test(name) ? null : original.call(this, name);
      };
    }
  }, [noFloatTargets, gpu] as [boolean, string]);
}

/** The paged page image (the overlay canvas is laid over it). */
function pageImage(page: Page) {
  return page.locator('.spread-row:not(.outgoing) img[alt="Page"]').first();
}

async function openReader(page: Page): Promise<void> {
  await routeItem(page);
  // The first reader open auto-shows the help overlay (onboarding); not under test here.
  await page.addInitScript(() => localStorage.setItem('mangapixer-reader-help-seen', '1'));
  await login(page);
  await page.goto(`/reader/${ITEM}`);
  await expect(pageImage(page)).toBeVisible();
}

async function openRenderingMenu(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Upscaling', exact: true }).click();
  await expect(page.getByRole('menuitemradio', { name: /^Upscaling: Smooth/ })).toBeVisible();
}

/** The overlay canvas the Upscaling directive lays over the page, once shown. */
function overlay(page: Page) {
  return page.locator('.spread-row:not(.outgoing) canvas[aria-hidden="true"]').first();
}

/** Dark and light pixels on the overlay: a real picture, not a blank or black box. */
async function overlayPixels(page: Page): Promise<{ width: number; height: number; dark: number; light: number }> {
  return overlay(page).evaluate((el) => {
    const canvas = el as HTMLCanvasElement;
    const copy = document.createElement('canvas');
    copy.width = canvas.width;
    copy.height = canvas.height;
    const ctx = copy.getContext('2d')!;
    ctx.drawImage(canvas, 0, 0);
    const data = ctx.getImageData(0, 0, copy.width, copy.height).data;
    let dark = 0;
    let light = 0;
    for (let i = 0; i < data.length; i += 4) {
      const l = (data[i] + data[i + 1] + data[i + 2]) / 3;
      if (l < 64) dark++;
      else if (l > 192) light++;
    }
    return { width: canvas.width, height: canvas.height, dark, light };
  });
}

test.afterEach(async ({ page }) => {
  await page.evaluate(() => localStorage.removeItem('mangapixer-reader-upscaler')).catch(() => undefined);
});

test('Crisp renders the enlarged page through FSR 1 on WebGL2, and the menu names the engine', async ({ page }) => {
  await withoutWebGpu(page);
  await openReader(page);
  await openRenderingMenu(page);
  const sharp = page.getByRole('menuitemradio', { name: /^Upscaling: Crisp/ });
  await expect(sharp).toBeEnabled();
  await sharp.click();

  await expect(overlay(page)).toBeVisible({ timeout: 30_000 });
  const img = await pageImage(page).boundingBox();
  const pixels = await overlayPixels(page);
  // Backing store = the painted page in device pixels (DPR 1), about 1.9x the source.
  expect(pixels.width).toBeGreaterThan(PAGE_W * 1.5);
  expect(Math.abs(pixels.height - Math.round(img!.height))).toBeLessThanOrEqual(2);
  expect(pixels.dark).toBeGreaterThan(1000);
  expect(pixels.light).toBeGreaterThan(pixels.width * pixels.height * 0.5);

  await openRenderingMenu(page);
  await expect(page.getByRole('menuitemradio', { name: 'Upscaling: Crisp - AMD FSR 1' })).toHaveAttribute('aria-checked', 'true');
  // The option line names the engine; the desktop status line only summarises the device (owner, 2026-09-26).
  // Secure origin (127.0.0.1 in CI): "WebGPU unavailable"; plain http on a LAN address: "WebGPU needs HTTPS".
  await expect(page.locator('.reader-options-menu .hint-tap')).toHaveText(/^GPU: WebGPU (unavailable|needs HTTPS), WebGL2 ready$/);
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/rendering-sharp-menu.png` });
});

test('without WebGPU, Enhance runs on WebGL2 and says why WebGPU is not used', async ({ page }) => {
  await withoutWebGpu(page);
  await openReader(page);
  await openRenderingMenu(page);
  await page.getByRole('menuitemradio', { name: /^Upscaling: Enhance/ }).click();

  await expect(overlay(page)).toBeVisible({ timeout: 60_000 });
  const pixels = await overlayPixels(page);
  expect(pixels.dark).toBeGreaterThan(1000);
  expect(pixels.light).toBeGreaterThan(pixels.width * pixels.height * 0.5);

  await openRenderingMenu(page);
  // 127.0.0.1 is a secure context, so the reason is the browser, not HTTPS.
  await expect(page.getByRole('menuitemradio', { name: 'Upscaling: Enhance - Anime4K (WebGL2)' }))
    .toHaveAttribute('aria-checked', 'true');
  // Max quality is WebGPU-only.
  await expect(page.getByRole('menuitemradio', { name: 'Enhance quality: Max quality' })).toBeDisabled();
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/rendering-enhance-webgl2-menu.png` });
});

test('without WebGPU or float render targets, Enhance is disabled with its reason and a saved Enhance is announced', async ({ page }) => {
  await withoutWebGpu(page, true);
  await page.addInitScript(() => localStorage.setItem('mangapixer-reader-upscaler', 'enhance'));
  await openReader(page);
  await expect(page.getByText("Enhance isn't available here - showing Smooth.")).toBeVisible();
  await openRenderingMenu(page);
  const enhance = page.getByRole('menuitemradio', {
    // Secure origin (CI): the chip is the limit; plain http: HTTPS (WebGPU) is the fix, so that is the reason shown.
    name: /^Upscaling: Enhance - (Graphics chip lacks float render targets|Needs a secure connection \(HTTPS\))$/,
  });
  await expect(enhance).toBeDisabled();
  await expect(page.getByRole('menuitemradio', { name: /^Upscaling: Smooth/ })).toHaveAttribute('aria-checked', 'true');
  // Crisp needs no float targets.
  await expect(page.getByRole('menuitemradio', { name: /^Upscaling: Crisp/ })).toBeEnabled();
  await expect(overlay(page)).toHaveCount(0);
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/rendering-enhance-disabled.png` });
});

test('software WebGL (blocklisted GPU): Enhance is disabled as "Graphics chip unavailable", Crisp still runs', async ({ page }) => {
  await withoutWebGpu(page, false, 'software');
  await openReader(page);
  await openRenderingMenu(page);
  await expect(page.getByRole('menuitemradio', { name: 'Upscaling: Enhance - Graphics chip unavailable' })).toBeDisabled();
  const crisp = page.getByRole('menuitemradio', { name: /^Upscaling: Crisp/ });
  await expect(crisp).toBeEnabled();
  await crisp.click();
  await expect(overlay(page)).toBeVisible({ timeout: 30_000 });
});
