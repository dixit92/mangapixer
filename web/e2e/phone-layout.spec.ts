import { test, expect, Page, APIRequestContext } from '@playwright/test';
import { expectFitsScreen, layoutProblems } from './layout';

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
    ['metadata completion', '/admin/metadata?tab=completion'],
    ['debug log', '/admin/logging'],
    ['move conflicts', '/admin/move-conflicts'],
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

test('the identify dialog with its site switch fits a phone and a tablet screen', async ({ page }) => {
  // 1.32.0: "Search on: MangaUpdates | Grand Comics Database", the GCD pace note and the start-year option. Contract-shaped
  // context (a comics folder with both sites allowed); nothing is searched, so nothing is sent.
  test.setTimeout(120_000);
  const failures: string[] = [];
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  await page.route(new RegExp(`/api/v1/admin/metadata/nodes/${folderId}/identify$`), (r) => r.fulfill({
    json: {
      nodeId: folderId, nodeKind: 'Folder', displayName: 'Stacked Saga (1991)', libraryId, provider: 'gcd',
      providerName: 'Grand Comics Database', fetchAvailable: true, comicsSignalled: true,
      sites: [
        { id: 'mangaupdates', name: 'MangaUpdates', available: true },
        { id: 'gcd', name: 'Grand Comics Database', available: true, note: 'Comics and graphic novels. Answers about 25 requests an hour.' },
      ],
      suggestions: ['Stacked Saga', 'A synthetic comics title that is long enough to wrap on a phone'],
      budgetUsedToday: 0, dailyBudget: 5000,
      local: { displayName: 'Stacked Saga (1991)', itemCount: 7, yearHint: 1991 },
    },
  }));
  for (const size of SIZES) {
    await page.setViewportSize(size);
    await page.goto(`/libraries/${libraryId}/browse`);
    await page.locator('.select-toggle').click();
    await page.locator('.node-wrap', { hasText: 'Stacked Saga' }).first().click();
    await page.getByTestId('series-selection-menu').click();
    await page.getByTestId('bulk-identify').click();
    await expect(page.getByTestId('identify-site')).toBeVisible();
    await expect(page.getByTestId('identify-start-year')).toContainText('1991');
    await settle(page);
    if (SHOTS) await page.screenshot({ path: `${SHOTS}/layout-${size.width}-identify-sites.png` });
    for (const p of await layoutProblems(page)) failures.push(`${size.width} px identify dialog: ${p.kind}: ${p.what} - ${p.detail}`);
    await page.getByTestId('identify-close').click();
  }
  expect(failures, `identify dialog does not fit the screen:\n${failures.join('\n')}`).toEqual([]);
});

test('the Scheduled jobs section fits a phone and a tablet screen, and saves an hour', async ({ page }) => {
  // 1.32.0: every job with its last / next run in server time; the library rows carry the scan time of day.
  test.setTimeout(180_000);
  await login(page);
  await ensureLibrary(page);
  const failures: string[] = [];
  for (const size of SIZES) {
    await page.setViewportSize(size);
    await page.goto('/admin');
    const card = page.getByTestId('scheduled-jobs');
    await card.scrollIntoViewIfNeeded();
    await expect(card.getByTestId('jobs-clock')).toContainText('Times are server time');
    await expect(card.getByTestId('job-metadata-refresh')).toContainText('Series information refresh');
    await settle(page);
    if (SHOTS) await card.screenshot({ path: `${SHOTS}/layout-${size.width}-scheduled-jobs.png` });
    for (const p of await layoutProblems(page)) failures.push(`${size.width} px scheduled jobs: ${p.kind}: ${p.what} - ${p.detail}`);
  }
  expect(failures, `the Scheduled jobs section does not fit the screen:\n${failures.join('\n')}`).toEqual([]);

  // Save the cache clean-up's hour, reload, see it kept; then put the default back.
  const card = page.getByTestId('scheduled-jobs');
  const select = card.getByTestId('job-hour-cache-eviction');
  const saved = page.waitForResponse((r) => r.request().method() === 'PUT' && r.url().endsWith('/api/v1/admin/jobs/cache-eviction'));
  await select.selectOption('2');
  expect((await saved).ok()).toBeTruthy();
  await page.reload();
  await expect(page.getByTestId('scheduled-jobs').getByTestId('job-hour-cache-eviction')).toHaveValue('2');
  const restored = page.waitForResponse((r) => r.request().method() === 'PUT' && r.url().endsWith('/api/v1/admin/jobs/cache-eviction'));
  await page.getByTestId('scheduled-jobs').getByTestId('job-hour-cache-eviction').selectOption('5');
  expect((await restored).ok()).toBeTruthy();
});

test('the phone header shows Libraries and Search as icons; wider screens keep the text', async ({ page }) => {
  // 1.32.0: on a phone the two text links touched each other; they are icon buttons (named for screen readers) there.
  test.setTimeout(120_000);
  await login(page);
  await ensureLibrary(page);
  const header = page.locator('mat-toolbar').first();
  const box = async (name: string) => (await header.getByRole('button', { name, exact: true }).boundingBox())!;

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await settle(page);
  await expect(header.locator('button.nav-icon')).toHaveCount(2);
  await expect(header.getByRole('button', { name: 'Libraries', exact: true })).toHaveText('library_books');
  const [libraries, search] = [await box('Libraries'), await box('Search')];
  expect(libraries.x + libraries.width, 'the two icons must not touch').toBeLessThanOrEqual(search.x);
  expect(libraries.width).toBeGreaterThanOrEqual(40); // a touch target, not a squeezed label
  await expectFitsScreen(page, 'the phone header on Home');
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/header-390.png` });

  await header.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(page).toHaveURL(/\/search/);
  await expect(header.getByRole('button', { name: 'Search', exact: true })).toHaveAttribute('aria-current', 'page');

  await page.setViewportSize({ width: 820, height: 1180 });
  await page.goto('/');
  await settle(page);
  await expect(header.locator('button.nav-icon')).toHaveCount(0);
  await expect(header.getByRole('button', { name: 'Libraries', exact: true })).toHaveText('Libraries');
  await expectFitsScreen(page, 'the tablet header on Home');
});

test('the reader shows the archive name: a row under the bar on phones and tablets, in the bar on desktop', async ({ page }) => {
  // 1.32.0: the name is text (with a title for the full name), one line, and never covers more of the page than the bar.
  test.setTimeout(180_000);
  await login(page);
  const [libraryId, folderId] = await ensureLibrary(page);
  const flat = await (await page.request.get(`/api/v1/libraries/${libraryId}/browse?parentId=${folderId}&pageSize=50&group=flat`)).json();
  const archive = (flat.items as Node[]).find((n) => n.kind === 'Archive')!;
  const failures: string[] = [];

  for (const size of [...SIZES, { width: 1024, height: 768 }, { width: 1280, height: 900 }]) {
    await page.setViewportSize(size);
    await page.goto(`/reader/${archive.id}`);
    const name = page.getByTestId('reader-archive-name');
    await expect(name).toHaveText(archive.displayName);
    await expect(name).toHaveAttribute('title', archive.displayName);
    await settle(page);
    if (SHOTS) await page.screenshot({ path: `${SHOTS}/reader-name-${size.width}.png` });
    const bar = (await page.locator('.reader-toolbar').boundingBox())!;
    const n = (await name.boundingBox())!;
    expect(n.height, `${size.width} px: the name stays on one line`).toBeLessThanOrEqual(32);
    expect(n.x).toBeGreaterThanOrEqual(0);
    expect(n.x + n.width).toBeLessThanOrEqual(size.width);

    if (size.width >= 1000) {
      // In the bar: between the page counter and the first action icon, centered in that gap.
      expect(n.y).toBeGreaterThanOrEqual(bar.y);
      expect(n.y + n.height).toBeLessThanOrEqual(bar.y + bar.height);
      const counter = (await page.locator('.reader-toolbar .page-info').boundingBox())!;
      const firstIcon = (await page.locator('.reader-toolbar button.chapter-arrow').first().boundingBox())!;
      expect(n.x, 'the name starts after the page counter').toBeGreaterThanOrEqual(counter.x + counter.width);
      expect(n.x + n.width, 'the name ends before the action icons').toBeLessThanOrEqual(firstIcon.x);
      const gapCenter = (counter.x + counter.width + firstIcon.x) / 2;
      expect(Math.abs(n.x + n.width / 2 - gapCenter), 'centered in the free space of the bar').toBeLessThanOrEqual(2);
    } else {
      // Under the bar: directly below it, the width of the screen.
      expect(Math.abs(n.y - (bar.y + bar.height)), `${size.width} px: the row sits right below the bar`).toBeLessThanOrEqual(1);
      expect(n.width).toBeGreaterThanOrEqual(size.width - 2);
    }
    for (const p of await layoutProblems(page)) failures.push(`${size.width} px reader: ${p.kind}: ${p.what} - ${p.detail}`);
  }
  expect(failures, `the reader with its archive name does not fit the screen:\n${failures.join('\n')}`).toEqual([]);
});
