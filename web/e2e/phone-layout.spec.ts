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
    ['metadata official releases', '/admin/metadata?tab=official'],
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
      // Select mode (1.30.0): a whole volume selected in the Volumes view, a chapter selected on the stack page.
      await page.goto(`${browse}/${folderId}`);
      await page.locator('button.select-toggle').click();
      await page.locator('.node-wrap a.node-card').first().click();
      await check('series - Volumes view, stack selected', size.width);
      await page.goto(`${browse}/${folderId}/volume/1`);
      await page.getByTestId('stack-select').click();
      await page.getByTestId('stack-item').first().click();
      await check('volume stack - chapter selected', size.width);
      // The same browse pages in list view and the series' Folders view.
      await setPreferences(page, { viewMode: 'list', seriesViewMode: 'Folders' });
      await page.goto(browse);
      await check('library root - list', size.width);
      await page.goto(`${browse}/${folderId}`);
      await check('series - Folders view, list', size.width);
      // 1.30.0: the Volumes view and the stack page in list view, with a row selected.
      await setPreferences(page, { viewMode: 'list', seriesViewMode: null });
      await page.goto(`${browse}/${folderId}`);
      await page.locator('.node-wrap .row-select').first().click();
      await check('series - Volumes view, list, stack selected', size.width);
      await page.goto(`${browse}/${folderId}/volume/1`);
      await check('volume stack - list', size.width);
      await page.getByTestId('stack-row').first().locator('.row-select').click();
      await check('volume stack - list, chapter selected', size.width);
      await setPreferences(page, { viewMode: saved['viewMode'], seriesViewMode: null });
    }
  } finally {
    await setPreferences(page, saved);
  }

  expect(failures, `pages that do not fit the screen:\n${failures.join('\n')}`).toEqual([]);
});

test('the declared-facts dialog and its type list fit a phone and a tablet screen', async ({ page }) => {
  // 1.30.0: each type names its country of origin ("Manhwa (Korea)") and the dialog explains the type is a hint.
  test.setTimeout(120_000);
  const failures: string[] = [];
  await login(page);
  const [, folderId] = await ensureLibrary(page);
  for (const size of SIZES) {
    await page.setViewportSize(size);
    await page.goto(`/series/${folderId}`);
    await page.getByTestId('series-admin-menu').click();
    await page.getByTestId('declared-facts').click();
    await expect(page.getByTestId('declared-type-hint')).toBeVisible();
    await settle(page);
    if (SHOTS) await page.screenshot({ path: `${SHOTS}/layout-${size.width}-declared-facts.png` });
    for (const p of await layoutProblems(page)) failures.push(`${size.width} px declared facts: ${p.kind}: ${p.what} - ${p.detail}`);

    // Open the type list with the keyboard (a closed mat-select is driven by keys, 1.29.0 CI flake).
    await page.getByTestId('declared-type-select').focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('option', { name: 'Manhwa (Korea)' })).toBeVisible();
    await settle(page);
    if (SHOTS) await page.screenshot({ path: `${SHOTS}/layout-${size.width}-declared-type-list.png` });
    for (const p of await layoutProblems(page)) failures.push(`${size.width} px declared type list: ${p.kind}: ${p.what} - ${p.detail}`);
    await page.keyboard.press('Escape');
    await page.keyboard.press('Escape');
  }
  expect(failures, `declared-facts dialog does not fit the screen:\n${failures.join('\n')}`).toEqual([]);
});

test('the admin trash card with held libraries and a confirm step fits a phone and a tablet screen', async ({ page }) => {
  // 1.31.0: Empty trash + Clean bundles. Contract-shaped data (the synthetic library has no removed items): a held library
  // with a long name, the counts line, and the confirm step that names the hold. Nothing is posted.
  test.setTimeout(120_000);
  const overview = {
    settings: { automaticCleaning: false, retentionDays: 30, allowedRetentionDays: [1, 7, 30, 90, 365], automaticHour: 4 },
    windowStart: '2026-09-01T12:00:00Z',
    libraries: [
      {
        libraryId: 'trash-held', name: 'A synthetic library with a rather long name that has to wrap on a phone',
        eligible: { nodes: 1234, archives: 1200, folders: 34, userStateRows: 5678, files: 3600, bytes: 987654321 },
        waiting: 12, libraryNodes: 2000, hold: 'burst', holdReleasable: true,
      },
      {
        libraryId: 'trash-ok', name: 'Synthetic',
        eligible: { nodes: 3, archives: 3, folders: 0, userStateRows: 4, files: 9, bytes: 2048 },
        waiting: 0, libraryNodes: 100, hold: null, holdReleasable: false,
      },
    ],
    total: { nodes: 3, archives: 3, folders: 0, userStateRows: 4, files: 9, bytes: 2048 },
    bundles: { files: 2, bytes: 4096 },
    lastEmpty: { at: '2026-09-30T04:00:00Z', automatic: true, count: 17, bytes: 123456, heldLibraries: 1 },
    lastBundleClean: null,
  };
  await page.route(/\/api\/v1\/admin\/trash$/, (r) =>
    r.request().method() === 'GET'
      ? r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(overview) })
      : r.abort());
  await login(page);
  const failures: string[] = [];
  for (const size of SIZES) {
    await page.setViewportSize(size);
    await page.goto('/admin');
    const card = page.getByTestId('trash-card');
    await card.scrollIntoViewIfNeeded();
    await expect(card.getByTestId('trash-lib-trash-held')).toContainText('Held');
    await settle(page);
    if (SHOTS) await card.screenshot({ path: `${SHOTS}/layout-${size.width}-trash-card.png` });
    for (const p of await layoutProblems(page)) failures.push(`${size.width} px trash card: ${p.kind}: ${p.what} - ${p.detail}`);

    await card.getByTestId('trash-empty-lib-trash-held').click();
    await expect(card.getByTestId('trash-confirm')).toContainText('Empty it anyway?');
    await settle(page);
    if (SHOTS) await card.screenshot({ path: `${SHOTS}/layout-${size.width}-trash-confirm.png` });
    for (const p of await layoutProblems(page)) failures.push(`${size.width} px trash confirm: ${p.kind}: ${p.what} - ${p.detail}`);
  }
  expect(failures, `the trash card does not fit the screen:\n${failures.join('\n')}`).toEqual([]);
});
