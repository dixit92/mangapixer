import { test, expect, Request } from '@playwright/test';
import { login, readerPrefs, routeFakeItem } from './reader-fixture';

/**
 * Reader prefetch (1.42.0, "current page before its own prefetch"): opening an item asks for the page on screen first
 * (unmarked), the prefetches start only once it is there, at most two run at a time, every prefetch carries
 * `X-MangaPixer-Prefetch: 1` and the exact URL of the page's own <img>, and the page on screen is never prefetched.
 * The item is synthetic (page.route answers it), with slow pages so the prefetches are still running when counted.
 */
const ID = 'e2eprefetch1';

test('opens the current page first, then at most two marked prefetches at a time', async ({ page }) => {
  await readerPrefs(page, { upscaler: 'smooth', animation: 'none' });
  const slow: Record<string, number> = {};
  for (let i = 1; i < 12; i++) slow[`p${i}`] = 800;
  await routeFakeItem(page, { id: ID, pageCount: 12, pageWidth: 300, pageHeight: 420, readerMode: 'PagedLtr', delayMs: slow });

  const log: { key: string; prefetch: boolean; start: number; end?: number; url: string }[] = [];
  const byRequest = new Map<Request, (typeof log)[number]>();
  page.on('request', (r) => {
    const m = /\/api\/v1\/items\/[^/]+\/pages\/([^/?]+)/.exec(r.url());
    if (!m || r.url().endsWith('/thumbnail')) return;
    const entry = { key: decodeURIComponent(m[1]), prefetch: r.headers()['x-mangapixer-prefetch'] === '1', start: Date.now(), url: r.url() };
    log.push(entry);
    byRequest.set(r, entry);
  });
  const finish = (r: Request) => { const e = byRequest.get(r); if (e) e.end = Date.now(); };
  page.on('requestfinished', finish);
  page.on('requestfailed', finish);

  await login(page);
  await page.goto(`/reader/${ID}`);
  await expect(page.locator('.reader-toolbar .page-info')).toHaveText('1 / 12');
  await expect.poll(() => log.filter((e) => e.prefetch).length, { timeout: 15000 }).toBeGreaterThanOrEqual(6);
  await expect.poll(() => log.filter((e) => e.prefetch && e.end === undefined).length, { timeout: 15000 }).toBe(0);

  const first = log[0];
  expect(first.key).toBe('p0');
  expect(first.prefetch).toBe(false);
  const prefetches = log.filter((e) => e.prefetch);
  // Never the page on screen, and none before it had arrived.
  expect(prefetches.map((e) => e.key)).not.toContain('p0');
  for (const p of prefetches) expect(p.start).toBeGreaterThanOrEqual(first.end!);
  // Nearest first.
  expect(prefetches.slice(0, 2).map((e) => e.key)).toEqual(['p1', 'p2']);
  // At most two marked prefetches in flight at any moment.
  for (const p of prefetches) {
    const overlapping = prefetches.filter((q) => q.start <= p.start && (q.end ?? Infinity) > p.start);
    expect(overlapping.length, `prefetches in flight when ${p.key} started`).toBeLessThanOrEqual(2);
  }
  // The prefetch URL is the <img>'s own URL (byte-identical), so the browser cache can serve the turn.
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('.reader-toolbar .page-info')).toHaveText('2 / 12');
  const img = await page.locator('.spread-row:not(.outgoing) img').first().getAttribute('src');
  expect(new URL(img!, page.url()).href).toBe(prefetches.find((e) => e.key === 'p1')!.url);
});
