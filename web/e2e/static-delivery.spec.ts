import { test, expect, Response } from '@playwright/test';

/**
 * Static delivery (1.46.0): what the browser receives on a first load of the real image - the bundles come precompressed from the
 * build (Brotli for Chromium) with a year-long `immutable` cache, `index.html` is revalidated on every use (`no-cache`), and API
 * answers are untouched. No sign-in needed: the sign-in / setup screen loads the same bundles.
 */
const HASHED_SCRIPT = /^\/(main|chunk|polyfills)-[A-Z0-9]{8}\.js$/;
const HASHED_STYLE = /^\/styles-[A-Z0-9]{8}\.css$/;
const IMMUTABLE = 'public, max-age=31536000, immutable';

test('a first load gets compressed, long-cached bundles and a revalidated index.html', async ({ page }) => {
  const responses: Response[] = [];
  page.on('response', (r) => responses.push(r));

  const doc = await page.goto('/');
  expect(doc, 'the document response').not.toBeNull();
  await page.waitForLoadState('networkidle');

  const docHeaders = await doc!.allHeaders();
  expect(docHeaders['cache-control']).toBe('no-cache');
  expect(docHeaders['content-encoding']).toBe('br');

  const bundles = responses.filter((r) => {
    const path = new URL(r.url()).pathname;
    return HASHED_SCRIPT.test(path) || HASHED_STYLE.test(path);
  });
  expect(bundles.length, 'hashed bundles on the first load').toBeGreaterThan(1);
  for (const bundle of bundles) {
    const headers = await bundle.allHeaders();
    const path = new URL(bundle.url()).pathname;
    expect(bundle.status(), path).toBe(200);
    expect(headers['content-encoding'], path).toBe('br');
    expect(headers['cache-control'], path).toBe(IMMUTABLE);
    expect(headers['vary'] ?? '', path).toContain('Accept-Encoding');
  }
});

test('a deep link gets the revalidated index.html, and API answers stay as they were', async ({ page }) => {
  const doc = await page.goto('/settings');
  const docHeaders = await doc!.allHeaders();
  expect(doc!.status()).toBe(200);
  expect(docHeaders['cache-control']).toBe('no-cache');
  expect(docHeaders['content-type']).toContain('text/html');

  const api = await page.request.get('/api/v1/auth/csrf', { headers: { 'Accept-Encoding': 'br, gzip' } });
  expect(api.ok()).toBeTruthy();
  const apiHeaders = api.headers();
  expect(apiHeaders['content-encoding']).toBeUndefined();
  expect(apiHeaders['cache-control'] ?? '').not.toContain('immutable');
});
