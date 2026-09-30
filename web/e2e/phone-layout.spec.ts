import { test, expect, Page, APIRequestContext } from '@playwright/test';
import { layoutProblems } from './layout';

/**
 * Every main page fits a phone (390 px) and a tablet (820 px) screen (1.29.2): no sideways page scroll, nothing past the
 * screen edge, no text squeezed into a one-letter column (see `layout.ts`). Real data from the synthetic volume library
 * (`e2e/fixtures/make-series-fixtures.mjs`, the same library as volume-stacks.spec.ts); pages with metadata lists show
 * their empty states here - the review rows with covers are checked with contract-shaped data in admin-metadata.spec.ts.
 * Optional: E2E_SCREENSHOT_DIR saves one screenshot per page and width.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const VOLUMES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-volumes` : undefined;
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const LIBRARY_NAME = 'Volume Stacks';
const SIZES = [{ width: 390, height: 844 }, { width: 820, height: 1180 }];

test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic volume library available');

interface Node { id: string; displayName: string; kind: string }

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get('/api/v1/auth/csrf');
  const { token } = await res.json();
  return { 'X-MangaPixer-Csrf': token };
}

/** The volume library (registered by whichever spec runs first) and its series folder: [libraryId, folderId]. */
async function ensureLibrary(page: Page): Promise<[string, string]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === LIBRARY_NAME);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: LIBRARY_NAME, rootPath: VOLUMES_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folder = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = ((root.items as Node[]).find((n) => n.displayName === 'Stacked Saga')?.id) ?? '';
    if (!folder) return 0;
    const inside = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?parentId=${folder}&pageSize=50&group=flat`)).json();
    return (inside.items as Node[]).length;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).toBe(7);
  return [lib!.id, folder];
}

/** Writes the viewer's browse preferences (list / card, Volumes / Folders) and returns the previous ones. */
async function setPreferences(page: Page, change: Record<string, unknown>): Promise<Record<string, unknown>> {
  const headers = await csrf(page.request);
  const current = await (await page.request.get('/api/v1/reading/library-preferences')).json();
  const res = await page.request.put('/api/v1/reading/library-preferences', { headers, data: { ...current, ...change } });
  expect(res.ok(), await res.text()).toBeTruthy();
  return current;
}

async function settle(page: Page): Promise<void> {
  await page.waitForLoadState('networkidle', { timeout: 5000 }).catch(() => { /* a page that keeps polling */ });
  await page.waitForTimeout(500);
}

test('every main page fits a phone and a tablet screen', async ({ page }) => {
  test.setTimeout(240_000);
  const failures: string[] = [];
  const check = async (name: string, width: number) => {
    await settle(page);
    if (SHOTS) await page.screenshot({ path: `${SHOTS}/layout-${width}-${name.replace(/\W+/g, '-')}.png` });
    for (const p of await layoutProblems(page)) failures.push(`${width} px ${name}: ${p.kind}: ${p.what} - ${p.detail}`);
  };

  for (const size of SIZES) {
    await page.setViewportSize(size);
    await page.context().clearCookies();
    await page.goto('/login');
    await check('login', size.width);
  }

  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  const flat = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?parentId=${folderId}&pageSize=50&group=flat`)).json();
  const archiveId = (flat.items as Node[]).find((n) => n.kind === 'Archive')!.id;
  const browse = `/libraries/${libraryId}/browse`;
  const pages: [string, string][] = [
    ['home', '/'],
    ['libraries', '/libraries'],
    ['library root', browse],
    ['series - Volumes view', `${browse}/${folderId}`],
    ['volume stack', `${browse}/${folderId}/volume/1`],
    ['search', '/search?q=Stacked'],
    ['favorites', '/favorites'],
    ['library menu', '/library-nav'],
    ['settings', '/settings'],
    ['admin', '/admin'],
    ['metadata settings', '/admin/metadata?tab=settings'],
    ['metadata review', '/admin/metadata?tab=review'],
    ['metadata flags', '/admin/metadata?tab=flags'],
    ['metadata runs', '/admin/metadata?tab=runs'],
    ['metadata missing', '/admin/metadata?tab=missing'],
    ['debug log', '/admin/logging'],
    ['reader', `/reader/${archiveId}`],
  ];

  const saved = await setPreferences(page, { seriesViewMode: null });
  try {
    for (const size of SIZES) {
      await page.setViewportSize(size);
      for (const [name, url] of pages) {
        await page.goto(url);
        await check(name, size.width);
      }
      // The same browse pages in list view and the series' Folders view.
      await setPreferences(page, { viewMode: 'list', seriesViewMode: 'Folders' });
      await page.goto(browse);
      await check('library root - list', size.width);
      await page.goto(`${browse}/${folderId}`);
      await check('series - Folders view, list', size.width);
      await setPreferences(page, { viewMode: saved['viewMode'], seriesViewMode: null });
    }
  } finally {
    await setPreferences(page, saved);
  }

  expect(failures, `pages that do not fit the screen:\n${failures.join('\n')}`).toEqual([]);
});
