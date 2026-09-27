import { test, expect, Page } from '@playwright/test';
import { deflateSync } from 'node:zlib';

/**
 * Vertical (webtoon) resume (1.27.0): an archive whose saved position is mid-way opens
 * in the Vertical view SHOWING that page, and loading it never overwrites the stored
 * position. Before 1.27.0 the reader scrolled the strip before it was rendered, so the
 * view stayed on page 1 while the bar said "18 / 30", and the first scroll saved ~page 1.
 *
 * Runs against the default E2E instance (empty media, see Verify-E2E.ps1): the SPA and
 * sign-in are real; the reader's item endpoints for one made-up item id are answered by
 * `page.route` with a synthetic 30-page item (plain grey pages, no real content), and
 * every progress save is recorded instead of stored.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const ITEM = 'e2everticalresume';
const PAGES = 30;
const SAVED = 17; // zero-based: the bar shows 18 / 30
const PAGE_W = 200;
const PAGE_H = 400;

test.describe.configure({ mode: 'serial' });
test.use({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1 });

// --- a tiny solid-colour PNG (RGB, no dependencies) ----------------------------

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
function greyPage(): Buffer {
  const row = Buffer.alloc(1 + PAGE_W * 3, 160);
  row[0] = 0; // filter: none
  const header = Buffer.alloc(13);
  header.writeUInt32BE(PAGE_W, 0);
  header.writeUInt32BE(PAGE_H, 4);
  header[8] = 8; // bit depth
  header[9] = 2; // colour type: RGB
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(Buffer.concat(Array.from({ length: PAGE_H }, () => row)))),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// --- the fake item ------------------------------------------------------------

interface ItemOptions {
  /** The server-resolved reading mode. */
  mode: 'VerticalWebtoon' | 'PagedLtr';
  /** Answer the reading-mode request this long after it is made (it can lose the race with the progress). */
  modeDelayMs?: number;
  /** A READ archive (read-mark) re-read to a mid page, or an unread one in progress. */
  read: boolean;
}

/** Routes the item and returns the page index of every progress save the reader sends. */
async function routeItem(page: Page, opts: ItemOptions): Promise<number[]> {
  const png = greyPage();
  const saves: number[] = [];
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  const progress = {
    itemId: ITEM, pageIndex: SAVED, contentVersion: 1, updatedAt: '2026-01-01T00:00:00Z',
    state: opts.read ? 'Completed' : 'InProgress', revision: 4, isStale: false, openPageIndex: SAVED,
  };
  await page.route(`**/api/v1/items/${ITEM}/manifest`, (r) => r.fulfill(json({
    itemId: ITEM, contentVersion: 1, manifestVersion: 1, archiveFormat: 'Zip', pageCount: PAGES, isSolid: false,
    hasAnimatedPages: false, spreadStarts: null,
    pages: Array.from({ length: PAGES }, (_, i) => ({
      entryKey: `p${i}`, pageIndex: i, mediaType: 'image/png', width: PAGE_W, height: PAGE_H, animationState: 'Static', byteSize: png.length,
    })),
  })));
  await page.route(`**/api/v1/items/${ITEM}/pages/**`, (r) => r.fulfill({ status: 200, contentType: 'image/png', body: png }));
  await page.route(`**/api/v1/reading/progress/${ITEM}`, (r) => {
    if (r.request().method() === 'GET') return r.fulfill(json(progress));
    saves.push((r.request().postDataJSON() as { pageIndex: number }).pageIndex);
    return r.fulfill(json({ revision: progress.revision + saves.length, alreadyApplied: false }));
  });
  await page.route(`**/api/v1/reading/${ITEM}/effective-mode`, async (r) => {
    if (opts.modeDelayMs) await new Promise((resolve) => setTimeout(resolve, opts.modeDelayMs));
    await r.fulfill(json({ readerMode: opts.mode })).catch(() => undefined);
  });
  await page.route(`**/api/v1/reading/${ITEM}/bookmarks`, (r) => r.fulfill(json([])));
  await page.route(`**/api/v1/nodes/${ITEM}/neighbors`, (r) => r.fulfill(json({ previous: null, next: null })));
  await page.route(`**/api/v1/nodes/${ITEM}`, (r) => r.fulfill(json({
    id: ITEM, parentId: 'e2eparent', libraryId: 'e2elibrary', kind: 'Archive', displayName: 'Synthetic strip',
    availability: 'Available', coverUrl: null, childFolderCount: null, childArchiveCount: null, pageCount: PAGES,
    readingState: opts.read ? 'Completed' : 'InProgress', lastReadPage: SAVED, readerDefault: null,
    isRead: opts.read, isFavorite: false,
  })));
  return saves;
}

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

async function openReader(page: Page, opts: ItemOptions): Promise<number[]> {
  const saves = await routeItem(page, opts);
  // The first reader open auto-shows the help overlay (onboarding); not under test here.
  await page.addInitScript(() => localStorage.setItem('mangapixer-reader-help-seen', '1'));
  await login(page);
  await page.goto(`/reader/${ITEM}`);
  await expect(page.locator('.page-info')).toContainText(`/ ${PAGES}`);
  return saves;
}

/** The zero-based page of the strip crossing the middle of the Vertical scroller, or -1. */
function pageOnScreen(page: Page): Promise<number> {
  return page.evaluate(() => {
    const el = document.querySelector<HTMLElement>('.reader-viewport.webtoon');
    if (!el) return -1;
    const middle = el.scrollTop + el.clientHeight / 2;
    const imgs = Array.from(el.querySelectorAll<HTMLElement>('.webtoon-page'));
    return imgs.findIndex((img) => middle >= img.offsetTop && middle < img.offsetTop + img.offsetHeight);
  });
}

/** Opened at the saved page, bar and strip agree, and nothing overwrote the position. */
async function expectResumedAtSavedPage(page: Page, saves: number[]): Promise<void> {
  await expect.poll(() => pageOnScreen(page), { timeout: 10_000 }).toBe(SAVED);
  await expect(page.locator('.page-info')).toContainText(`${SAVED + 1} / ${PAGES}`);
  // Longer than the reader's 600 ms scroll-save debounce: the load must not save a page.
  await page.waitForTimeout(1500);
  expect(await pageOnScreen(page)).toBe(SAVED);
  expect(saves.filter((p) => p !== SAVED)).toEqual([]);

  // A small scroll by the reader stays on the page (these pages are taller than the
  // screen), so it saves nothing below the saved page either.
  await page.mouse.move(640, 450);
  await page.mouse.wheel(0, 120);
  await page.waitForTimeout(1200);
  expect(saves.filter((p) => p < SAVED)).toEqual([]);
}

test('a read archive re-read to a mid page reopens there in the Vertical view', async ({ page }) => {
  const saves = await openReader(page, { mode: 'VerticalWebtoon', read: true });
  await expectResumedAtSavedPage(page, saves);
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/vertical-resume-desktop.png` });
});

test('an unread archive in progress resumes mid-way in Vertical when the mode answers late', async ({ page }) => {
  // The reading-mode answer arrives after the saved position: the reader shows the page
  // in the paged view first, then switches to Vertical and must keep the page.
  const saves = await openReader(page, { mode: 'VerticalWebtoon', modeDelayMs: 1500, read: false });
  await expectResumedAtSavedPage(page, saves);
});

test('switching Single page to Vertical keeps the current page', async ({ page }) => {
  const saves = await openReader(page, { mode: 'PagedLtr', read: false });
  await expect(page.locator('.page-info')).toContainText(`${SAVED + 1} / ${PAGES}`);
  await page.getByRole('button', { name: 'Reading mode' }).click();
  await page.getByRole('menuitemradio', { name: /Vertical/ }).click();
  await expectResumedAtSavedPage(page, saves);
});
