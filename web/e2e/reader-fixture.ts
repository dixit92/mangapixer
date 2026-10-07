import { expect, Page } from '@playwright/test';
import { deflateSync } from 'node:zlib';

/**
 * Shared rig for reader specs that run on a made-up item (1.36.0): a synthetic line-art PNG, `page.route` answers for
 * the reader's item endpoints, sign-in, and the "no WebGPU, hardware WebGL2" pin. Same approach as
 * `reader-rendering.spec.ts` (which keeps its own copy): the SPA and sign-in are real, the item is not, so no media
 * is needed and the page is small enough to be painted larger than its natural size (the Upscaling overlay has work).
 */
export const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
export const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';

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

/** Synthetic line art: concentric rings and a diagonal stroke on white. */
export function syntheticPage(width: number, height: number): Buffer {
  const rows: Buffer[] = [];
  for (let y = 0; y < height; y++) {
    const row = Buffer.alloc(1 + width * 3, 255);
    row[0] = 0; // filter: none
    for (let x = 0; x < width; x++) {
      const r = Math.hypot(x - width / 2, y - height / 2);
      const ring = r > 20 && r % 14 < 1.6;
      const diagonal = Math.abs(y - x * (height / width)) < 1.5;
      if (ring || diagonal) row.fill(0, 1 + x * 3, 4 + x * 3);
    }
    rows.push(row);
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8; // bit depth
  header[9] = 2; // colour type: RGB
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header), chunk('IDAT', deflateSync(Buffer.concat(rows))), chunk('IEND', Buffer.alloc(0)),
  ]);
}

// --- the fake item ------------------------------------------------------------

export interface FakeItem {
  id: string;
  pageCount: number;
  pageWidth: number;
  pageHeight: number;
  /** What `effective-mode` answers, e.g. `PagedLtr`, `PagedRtl`, `DoubleSpread`. */
  readerMode: string;
  /** The archive's saved pairing (`[]` = no shifts: pages 0 and 1 pair); null = the device fallback. */
  spreadStarts?: number[] | null;
  /** Extra latency per page entry key (e.g. `{ p1: 1500 }` makes the second page arrive late). */
  delayMs?: Record<string, number>;
  /** Extra latency of the `effective-mode` answer (the view arrives after the first page is shown). */
  modeDelayMs?: number;
}

export async function routeFakeItem(page: Page, item: FakeItem): Promise<void> {
  const png = syntheticPage(item.pageWidth, item.pageHeight);
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  const id = item.id;
  const progress = {
    itemId: id, pageIndex: 0, contentVersion: 1, updatedAt: '2026-01-01T00:00:00Z', state: 'Unread',
    revision: 0, isStale: false, openPageIndex: 0,
  };
  const indices = Array.from({ length: item.pageCount }, (_, i) => i);
  await page.route(`**/api/v1/items/${id}/manifest`, (r) => r.fulfill(json({
    itemId: id, contentVersion: 1, manifestVersion: 1, archiveFormat: 'Zip', pageCount: item.pageCount, isSolid: false,
    hasAnimatedPages: false, spreadStarts: item.spreadStarts ?? null,
    pages: indices.map((i) => ({
      entryKey: `p${i}`, pageIndex: i, mediaType: 'image/png', width: item.pageWidth, height: item.pageHeight,
      animationState: 'Static', byteSize: png.length,
    })),
  })));
  await page.route(`**/api/v1/items/${id}/pages/**`, async (r) => {
    const key = decodeURIComponent(new URL(r.request().url()).pathname.split('/').pop() ?? '');
    const delay = item.delayMs?.[key] ?? 0;
    if (delay > 0) await new Promise((resolve) => setTimeout(resolve, delay));
    await r.fulfill({ status: 200, contentType: 'image/png', body: png }).catch(() => undefined);
  });
  await page.route(`**/api/v1/reading/progress/${id}`, (r) =>
    r.fulfill(json(r.request().method() === 'GET' ? progress : { revision: 1, alreadyApplied: false })));
  await page.route(`**/api/v1/reading/${id}/effective-mode`, async (r) => {
    if (item.modeDelayMs) await new Promise((resolve) => setTimeout(resolve, item.modeDelayMs));
    await r.fulfill(json({ readerMode: item.readerMode })).catch(() => undefined);
  });
  await page.route(`**/api/v1/reading/${id}/bookmarks`, (r) => r.fulfill(json([])));
  await page.route(`**/api/v1/nodes/${id}/neighbors`, (r) => r.fulfill(json({ previous: null, next: null })));
  await page.route(`**/api/v1/nodes/${id}`, (r) => r.fulfill(json({
    id, parentId: 'e2eparent', libraryId: 'e2elibrary', kind: 'Archive', displayName: 'Synthetic page',
    availability: 'Available', coverUrl: null, childFolderCount: null, childArchiveCount: null, pageCount: item.pageCount,
    readingState: 'Unread', lastReadPage: null, readerDefault: null, isRead: false, isFavorite: false,
  })));
}

export async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

/**
 * Pin "no WebGPU" and a hardware-looking WebGL2 (headless Chromium's WebGL is SwiftShader, which the app would
 * otherwise treat as a blocklisted chip): Crisp then runs on WebGL2 - see `reader-rendering.spec.ts`.
 */
export async function withoutWebGpu(page: Page): Promise<void> {
  await page.addInitScript(() => {
    Object.defineProperty(Navigator.prototype, 'gpu', { configurable: true, get: () => undefined });
    const getContext = HTMLCanvasElement.prototype.getContext as (this: HTMLCanvasElement, id: string, opts?: Record<string, unknown>) => unknown;
    HTMLCanvasElement.prototype.getContext = function (this: HTMLCanvasElement, id: string, opts?: Record<string, unknown>) {
      if (id === 'webgl2' && opts?.['failIfMajorPerformanceCaveat']) {
        const { failIfMajorPerformanceCaveat: _drop, ...rest } = opts;
        return getContext.call(this, id, rest);
      }
      return getContext.call(this, id, opts);
    } as typeof HTMLCanvasElement.prototype.getContext;
  });
}

/** Reader preferences for one test: Upscaling choice, page-turn animation, help already seen. */
export async function readerPrefs(page: Page, prefs: { upscaler?: 'smooth' | 'sharp' | 'enhance'; animation?: string }): Promise<void> {
  await page.addInitScript(([upscaler, animation]: [string | null, string | null]) => {
    localStorage.setItem('mangapixer-reader-help-seen', '1');
    if (upscaler) localStorage.setItem('mangapixer-reader-upscaler', upscaler);
    if (animation) localStorage.setItem('mangapixer-reader-page-animation', animation);
  }, [prefs.upscaler ?? null, prefs.animation ?? null] as [string | null, string | null]);
}
