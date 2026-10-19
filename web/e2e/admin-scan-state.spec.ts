import { test, expect, Page } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * Administration > Libraries with SEVERAL libraries scanning (1.45.0, owner design 2026-10-19; the owner's live 1.44.0 screenshot had
 * 3 of 6 scanning). The libraries list is answered here: the real libraries (padded with synthetic ones to three) start scanning one
 * after another - one more at each of the page's 2.5 s polls - so their rows begin animating at different times, as on the owner's
 * box. Nothing is scanned for real; the scan runs and the Cancel calls are answered here too.
 * - the tint and ONE bar cover the whole library block: the bar runs along the block's foot, below its Auto-scan and Declared lines
 *   (it was drawn between the title row and those lines, reading as a divider of the wrong row);
 * - every bar and every ring run in one phase (they started whenever their row was inserted);
 * - the ring is centred on the letter / icon glyph (it was an arc above-left of it);
 * - a real gap between "Scanning…" and the dot before Cancel scan ("Scanning…·");
 * - the "Scan all libraries" tooltip opens above the button (below, it covered the first library's name);
 * - "3 libraries scanning · Cancel all" over the list, and Cancel all cancels every run.
 * Optional: E2E_SCREENSHOT_DIR saves a screenshot per width.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];

interface Lib { id: string; name: string; isScanning: boolean; [k: string]: unknown }

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

function synthetic(i: number): Lib {
  return {
    id: `zzk3syn${i}`, name: `Synthetic Shelf ${i}`, isScanning: false, itemCount: 12 * (i + 1), lastScanCompleted: null,
    defaultReaderMode: null, icon: null, scanSchedule: 'off', nextScheduledScanAt: null, scanHour: null, scanWeekday: null,
    lastScanStartedBy: null,
  };
}

/** Answers the libraries list (one more library scanning every 2.6 s from the first answer), scan runs and cancels. */
async function routeScans(page: Page): Promise<{ cancelled: string[] }> {
  let t0 = 0;
  const cancelled: string[] = [];
  await page.route(/\/api\/v1\/libraries$/, async (route) => {
    const res = await route.fetch();
    const libs: Lib[] = [...(await res.json() as Lib[])];
    for (let i = libs.length; i < 3; i++) libs.push(synthetic(i));
    if (t0 === 0) t0 = Date.now();
    const scanning = Math.min(3, 1 + Math.floor((Date.now() - t0) / 2600));
    await route.fulfill({ response: res, json: libs.map((l, i) => ({ ...l, isScanning: i < scanning })) });
  });
  await page.route(/\/api\/v1\/admin\/libraries\/zzk3syn\d$/, (route) =>
    route.fulfill({ json: synthetic(Number(route.request().url().slice(-1))) }));
  await page.route(/\/api\/v1\/admin\/libraries\/[^/]+\/scans$/, (route) => {
    const id = route.request().url().split('/').at(-2);
    return route.fulfill({ json: [{ id: `run-${id}`, libraryId: id, status: 'running', startedAt: '2026-10-19T00:00:00Z' }] });
  });
  await page.route(/\/api\/v1\/admin\/scans\/[^/]+\/cancel$/, (route) => {
    cancelled.push(route.request().url().split('/').at(-2)!);
    return route.fulfill({ status: 204 });
  });
  return { cancelled };
}

/** Geometry and animation state of each scanning library, measured in one frame, independent of how the DOM nests them. */
async function measure(page: Page) {
  return page.evaluate(() => {
    const items = [...document.querySelectorAll('mat-card mat-list-item')].filter((i) => i.querySelector('.lib-line'));
    const after = (from: Element, selector: string): Element | null =>
      [...document.querySelectorAll(selector)].find((e) => from.compareDocumentPosition(e) & Node.DOCUMENT_POSITION_FOLLOWING) ?? null;
    const before = (a: Element, b: Element | null) => !b || !!(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);
    const rect = (e: Element | null) => (e ? e.getBoundingClientRect() : null);
    const phase = (e: Element | null) => {
      const a = e ? e.getAnimations({ subtree: true }) : [];
      return a.length ? (a[0].effect!.getComputedTiming().progress ?? null) : null;
    };
    return items.map((item, n) => {
      const next = items[n + 1] ?? null;
      const scanning = !!item.querySelector('.scan-label');
      const schedule = after(item, 'app-library-scan-schedule');
      const declared = after(item, 'app-library-declared-facts');
      const bars = [...document.querySelectorAll('[data-testid="lib-scan-bar"]')]
        .filter((b) => (item.contains(b) || before(item, b)) && before(b, next));
      // The ring: its own element (1.45.0), else the list's icon slot it was drawn around (a ::after 5 px outside it).
      const ringEl = item.querySelector('[data-testid="lib-scan-ring"]');
      const slot = item.querySelector('.lib-icon')!;
      const r = rect(ringEl) ?? (() => { const s = slot.getBoundingClientRect(); return new DOMRect(s.x - 5, s.y - 5, s.width + 10, s.height + 10); })();
      const glyph = rect(item.querySelector('app-library-icon > *'))!;
      // The gap between "Scanning…" and the dot after it.
      let gap: number | null = null;
      const label = item.querySelector('.scan-label');
      if (label) {
        const walker = document.createTreeWalker(item.querySelector('.lib-line')!, NodeFilter.SHOW_TEXT);
        for (let t = walker.nextNode(); t; t = walker.nextNode()) {
          const at = t.textContent!.indexOf('·');
          if (at < 0 || !(label.compareDocumentPosition(t) & Node.DOCUMENT_POSITION_FOLLOWING) || label.contains(t)) continue;
          const range = document.createRange();
          range.setStart(t, at);
          range.setEnd(t, at + 1);
          gap = range.getBoundingClientRect().left - label.getBoundingClientRect().right;
          break;
        }
      }
      return {
        scanning, bars: bars.length,
        barTop: rect(bars[0] ?? null)?.top ?? null, barBottom: rect(bars[0] ?? null)?.bottom ?? null,
        detailsBottom: Math.max(rect(schedule)?.bottom ?? 0, rect(declared)?.bottom ?? 0),
        barPhase: phase(bars[0] ?? null), ringPhase: phase(slot),
        ringOff: Math.hypot((r.left + r.width / 2) - (glyph.left + glyph.width / 2), (r.top + r.height / 2) - (glyph.top + glyph.height / 2)),
        gap,
      };
    });
  });
}

test.describe('Administration > Libraries, several libraries scanning (1.45.0)', () => {
  test('the whole block scans with one bar at its foot, every bar and ring in one phase, the ring centred, a summary line', async ({ page }) => {
    test.setTimeout(120_000);
    const { cancelled } = await routeScans(page);
    await login(page);
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/admin');
    // Three scanning, the third one started two polls after the first.
    await expect(page.locator('mat-card mat-list-item .scan-label')).toHaveCount(3, { timeout: 30_000 });
    await page.waitForTimeout(1200);
    const rows = (await measure(page)).filter((r) => r.scanning);
    expect(rows).toHaveLength(3);

    for (const [i, row] of rows.entries()) {
      expect.soft(row.bars, `library ${i}: one bar`).toBe(1);
      // The bar runs along the block's foot - below the Auto-scan and Declared lines, not between them and the title row.
      expect.soft(row.barTop!, `library ${i}: bar below its detail lines`).toBeGreaterThanOrEqual(row.detailsBottom - 1);
      expect.soft(row.ringOff, `library ${i}: ring centred on the glyph`).toBeLessThan(1.5);
      expect.soft(row.gap ?? 0, `library ${i}: a gap between "Scanning…" and the dot`).toBeGreaterThanOrEqual(3);
    }
    // One phase: every bar (and every ring) is at the same point of its animation, though they started 2.6 s apart.
    const spread = (xs: (number | null)[]) => Math.max(...xs.map((x) => x ?? -1)) - Math.min(...xs.map((x) => x ?? -1));
    expect.soft(spread(rows.map((r) => r.barPhase)), 'bars in one phase').toBeLessThan(0.02);
    expect.soft(spread(rows.map((r) => r.ringPhase)), 'rings in one phase').toBeLessThan(0.02);

    // The "Scan all libraries" tooltip opens above the button.
    const scanAll = page.getByRole('button', { name: 'Scan all libraries' });
    await scanAll.hover();
    const tip = page.locator('.mat-mdc-tooltip');
    await expect(tip).toBeVisible();
    const [tipBox, buttonBox] = [(await tip.boundingBox())!, (await scanAll.boundingBox())!];
    expect.soft(tipBox.y + tipBox.height, 'tooltip above the button').toBeLessThanOrEqual(buttonBox.y + 1);
    await page.mouse.move(0, 0);

    // The summary line over the list; Cancel all cancels every run once.
    const summary = page.getByTestId('lib-scan-summary');
    await expect(summary).toHaveText(/^\s*3 libraries scanning\s*·\s*Cancel all\s*$/);
    await expectFitsScreen(page, 'Administration, three libraries scanning, 1280 px');
    if (SHOTS) await page.screenshot({ path: `${SHOTS}/admin-scanning-1280.png`, fullPage: false });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(400);
    await expectFitsScreen(page, 'Administration, three libraries scanning, 390 px');
    if (SHOTS) await page.screenshot({ path: `${SHOTS}/admin-scanning-390.png`, fullPage: false });
    await page.getByTestId('lib-cancel-all').click();
    await expect.poll(() => cancelled.length).toBe(3);
    expect(new Set(cancelled).size).toBe(3);
  });
});
