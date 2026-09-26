import { test, expect, Page, APIRequestContext, Request, Route } from '@playwright/test';

/**
 * The series-metadata admin page (metadata stage 2, lane C) - NEVER the real network:
 * - the main admin page shows the summary tile (Logging stays the last card) and opens
 *   /admin/metadata, whose four tabs render with the stage-2 endpoints implemented OR
 *   still answering 501 (the page explains that instead of breaking);
 * - "Automatic matching" is consent-gated: the switch is disabled until the automatic-
 *   lookups consent is ticked, NO automatic-matching call is made before that, and
 *   turning it on is one settings PUT (no match / run request, no provider request,
 *   budget unchanged, the browser contacts no host but MangaPixer);
 * - with the stage-2 review / flags / runs routes fulfilled IN THE BROWSER by synthetic,
 *   contract-shaped data: the review dashboard's keyboard triage and deferred Undo, and
 *   candidate posters loading only when a row is expanded; phone layout.
 * Optional: E2E_SCREENSHOT_DIR saves the reviewed screenshots.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];

test.describe.configure({ mode: 'serial' });

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

interface Settings {
  fetchEnabled: boolean;
  autoMatchEnabled?: boolean;
  budgetUsedToday: number;
  libraries: { libraryId: string }[];
}

async function settings(page: Page): Promise<Settings> {
  return (await page.request.get('/api/v1/admin/metadata/settings')).json();
}

async function putSettings(page: Page, data: Record<string, unknown>): Promise<void> {
  const res = await page.request.put('/api/v1/admin/metadata/settings', { headers: await csrf(page.request), data });
  expect(res.ok(), await res.text()).toBeTruthy();
}

async function shot(page: Page, name: string, fullPage = false): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage });
}

/** A tab of the page's own tab bar (the Review list has its own tablist too). */
function pageTab(page: Page, name: string) {
  return page.locator('.mat-mdc-tab-header').getByRole('tab', { name: new RegExp(`^${name}`) });
}

/** Records every browser request to a host other than MangaPixer's. */
function watchForeignRequests(page: Page, baseURL: string): string[] {
  const own = new URL(baseURL).host;
  const foreign: string[] = [];
  page.on('request', (r) => {
    const url = new URL(r.url());
    if ((url.protocol === 'http:' || url.protocol === 'https:') && url.host !== own) foreign.push(url.host);
  });
  return foreign;
}

test('admin page: summary tile opens /admin/metadata with four tabs; Logging stays last', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  await page.goto('/admin');
  const tile = page.getByTestId('metadata-summary-tile');
  await expect(tile).toBeVisible();
  await expect(page.getByTestId('metadata-settings-card')).toHaveCount(0); // the card moved
  const lastCard = await page.locator('app-admin').evaluate((el) => el.lastElementChild?.tagName.toLowerCase());
  expect(lastCard).toBe('app-debug-log-card');
  await tile.scrollIntoViewIfNeeded();
  await shot(page, 'c-01-admin-summary-tile');

  await tile.getByTestId('tile-open').click();
  await expect(page).toHaveURL(/\/admin\/metadata$/);
  const tabs = page.locator('.mat-mdc-tab-header').getByRole('tab');
  await expect(tabs).toHaveCount(4);
  await expect(page.getByTestId('metadata-settings-card')).toBeVisible();
  await shot(page, 'c-02-settings-tab', true);

  // Review / Flags / Runs render their data, or explain that the server does not have them yet.
  for (const [name, testId, url] of [
    ['Review', 'review-dashboard', /tab=review/],
    ['Flags', 'metadata-flags', /tab=flags/],
    ['Runs', 'metadata-runs', /tab=runs/],
  ] as const) {
    await pageTab(page, name).click();
    await expect(page).toHaveURL(url);
    const panel = page.getByTestId(testId);
    await expect(panel).toBeVisible();
    await expect(panel.locator('mat-spinner')).toHaveCount(0);
  }
  expect(foreign).toEqual([]);
});

test('Automatic matching is consent-gated: no automatic-matching call before consent', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  const autoCalls: string[] = [];
  const settingsPuts: Record<string, unknown>[] = [];
  page.on('request', (r: Request) => {
    const path = new URL(r.url()).pathname;
    if (r.method() === 'POST' && /\/admin\/metadata\/(libraries\/[^/]+\/match|review\/bulk|review\/[^/]+\/accept)$/.test(path)) {
      autoCalls.push(path);
    }
    if (r.method() === 'PUT' && path === '/api/v1/admin/metadata/settings') settingsPuts.push(r.postDataJSON());
  });
  await login(page);
  // Start from web lookups on (v1 consent) and automatic matching off.
  await putSettings(page, { fetchEnabled: true, acceptedConsentVersion: 1 });
  await putSettings(page, { autoMatchEnabled: false });
  const usedBefore = (await settings(page)).budgetUsedToday;

  await page.goto('/admin/metadata');
  const auto = page.getByTestId('md-auto');
  await auto.scrollIntoViewIfNeeded();
  await expect(auto.getByTestId('md-auto-consent-text')).toContainText('What is sent automatically:');
  await expect(auto.getByTestId('md-auto-consent-text')).toContainText('nobody reviews before it is sent');
  const autoSwitch = auto.getByTestId('md-auto-switch').getByRole('switch');
  if (!(await auto.getByTestId('md-auto-consented').count())) {
    await expect(autoSwitch).toBeDisabled();
    await shot(page, 'c-03-auto-consent-required');
    expect(settingsPuts.filter((b) => b['autoMatchEnabled'] === true)).toEqual([]);
    expect(autoCalls).toEqual([]);
    await auto.getByTestId('md-auto-consent').locator('input[type="checkbox"]').check();
  }
  await expect(autoSwitch).toBeEnabled();
  const put = page.waitForRequest((r) => r.method() === 'PUT' && r.url().endsWith('/api/v1/admin/metadata/settings'));
  await autoSwitch.click();
  expect((await put).postDataJSON()).toEqual({ autoMatchEnabled: true, acceptedAutoConsentVersion: 1 });
  await page.waitForTimeout(800);
  await shot(page, 'c-04-auto-after-switch');

  // The switch itself matched nothing: no run, no review call, no provider request.
  expect(autoCalls).toEqual([]);
  expect((await settings(page)).budgetUsedToday).toBe(usedBefore);
  expect(foreign).toEqual([]);

  await putSettings(page, { autoMatchEnabled: false });
  await putSettings(page, { fetchEnabled: false });
});

// --- Synthetic, contract-shaped stage-2 data served IN THE BROWSER (no server, no network) ---

const PNG_1PX = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');

const cand = (rank: number, title: string, score: number, reasons: string[] = []) => ({
  rank, provider: 'mangaupdates', externalId: String(1000 + rank), title, providerType: rank === 3 ? 'Novel' : 'Manga',
  year: 2010 + rank, volumes: 10 - rank, titleScore: score + 0.02, adjustedScore: score, reasons, imageToken: `tok-${title.length}-${rank}`,
});

const REVIEW_ITEMS = [
  { nodeId: 'r1', nodeKind: 'Folder', displayName: 'Synthetic Saga', libraryId: 'lib-x', libraryName: 'Sample Library',
    trail: ['Manga'], workClass: 'Series', matchLevel: 'Folder', itemCount: 24, openFlagCount: 0, reasons: ['close_second'],
    candidates: [cand(1, 'Synthetic Saga', 0.94, ['close_second']), cand(2, 'Synthetic Saga Returns', 0.91), cand(3, 'Synthetic Saga (Novel)', 0.9, ['type'])] },
  { nodeId: 'r2', nodeKind: 'Folder', displayName: 'Example Chronicle', libraryId: 'lib-x', libraryName: 'Sample Library',
    trail: ['Manga', 'E'], workClass: 'SeriesWithUnits', matchLevel: 'Folder', itemCount: 11, openFlagCount: 1, reasons: ['count', 'year'],
    candidates: [cand(1, 'Example Chronicle', 0.88, ['count']), cand(2, 'Example Chronicles Zero', 0.8, ['year'])] },
  { nodeId: 'r3', nodeKind: 'Archive', displayName: 'Placeholder One-Shot', libraryId: 'lib-x', libraryName: 'Sample Library',
    trail: ['Artists', 'Sample Circle'], workClass: 'ArtistCollection', matchLevel: 'Archive', itemCount: 1, openFlagCount: 0,
    memberNodeIds: ['r3b', 'r3c'], reasons: ['one_shot', 'review_only'], candidates: [cand(1, 'Placeholder One-Shot', 0.83, ['one_shot'])] },
];

const SUMMARY = { needsReview: 3, autoLinked: 41, unmatched: 5, openFlags: 2, dontMatch: 7, confirmed: 120, missingFolders: 1, pending: 12 };

const FLAGS = [
  { flagId: 'fl1', nodeId: 'r4', nodeKind: 'Folder', nodeDisplayName: 'Sample Tales', libraryId: 'lib-x', reason: 'WrongSeries',
    note: 'The cover and the description belong to the sequel.', state: 'Open', reporterDisplayName: 'Reader One',
    createdAt: '2026-09-25T08:00:00Z', currentLink: { state: 'Auto', title: 'Sample Tales II', matchMethod: 'Auto', matchScore: 0.95,
      updatedAt: '2026-09-24T00:00:00Z' } },
  { flagId: 'fl2', nodeId: 'r5', nodeKind: 'Folder', nodeDisplayName: 'Mixed Shorts', libraryId: 'lib-x', reason: 'NotOneSeries',
    note: null, state: 'Open', reporterDisplayName: 'Reader Two', createdAt: '2026-09-25T09:30:00Z',
    currentLink: { state: 'Confirmed', title: 'Mixed Shorts', updatedAt: '2026-09-01T00:00:00Z' } },
];

const RUNS = {
  status: { enabled: true, active: true, waitingCode: null, waitingUntil: null, pending: 12 },
  items: [
    { runId: 'run2', libraryId: 'lib-x', libraryName: 'Sample Library', trigger: 'Bulk', status: 'Running', reviewFirst: false,
      startedAt: '2026-09-26T10:00:00Z', candidates: 60, queued: 60, processed: 21, autoLinked: 15, needsReview: 4, unmatched: 2,
      skipped: 0, failed: 0, requestsUsed: 63, autoChangedByAdmin: 0, reviewAcceptedTop: 0, reviewAcceptedOther: 0, reviewDontMatch: 0 },
    { runId: 'run1', libraryId: 'lib-x', libraryName: 'Sample Library', trigger: 'Scan', status: 'Completed', reviewFirst: false,
      startedAt: '2026-09-20T10:00:00Z', completedAt: '2026-09-20T10:40:00Z', candidates: 40, queued: 40, processed: 40,
      autoLinked: 30, needsReview: 8, unmatched: 2, skipped: 0, failed: 0, requestsUsed: 110, autoChangedByAdmin: 2,
      reviewAcceptedTop: 5, reviewAcceptedOther: 1, reviewDontMatch: 1 },
  ],
};

interface Mocked { accepts: string[]; bulks: unknown[]; images: string[] }

async function mockStage2(page: Page): Promise<Mocked> {
  const seen: Mocked = { accepts: [], bulks: [], images: [] };
  const json = (route: Route, body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/v1/admin/metadata/review/summary**', (r) => json(r, SUMMARY));
  await page.route(/\/api\/v1\/admin\/metadata\/review\?/, (r) => {
    const tab = new URL(r.request().url()).searchParams.get('tab') ?? 'NeedsReview';
    const items = tab === 'NeedsReview' ? REVIEW_ITEMS : [];
    return json(r, { tab, items, total: items.length });
  });
  await page.route(/\/api\/v1\/admin\/metadata\/review\/[^/]+\/accept$/, (r) => {
    seen.accepts.push(r.request().url());
    return json(r, { nodeId: 'x' });
  });
  await page.route('**/api/v1/admin/metadata/review/bulk', (r) => {
    seen.bulks.push(r.request().postDataJSON());
    const body = r.request().postDataJSON() as { action: string; nodeIds: string[] };
    return json(r, { action: body.action, succeeded: body.nodeIds.length, failed: 0, results: body.nodeIds.map((nodeId) => ({ nodeId, code: 'ok' })) });
  });
  await page.route('**/api/v1/admin/metadata/candidates/*/image', (r) => {
    seen.images.push(r.request().url());
    return r.fulfill({ status: 200, contentType: 'image/png', body: PNG_1PX });
  });
  await page.route(/\/api\/v1\/admin\/metadata\/flags(\?|$)/, (r) => json(r, { items: FLAGS, total: FLAGS.length }));
  await page.route(/\/api\/v1\/admin\/metadata\/runs(\?|$)/, (r) => json(r, RUNS));
  return seen;
}

test('review dashboard with synthetic contract-shaped data: keyboard, deferred Undo, posters on expand', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  const seen = await mockStage2(page);
  await page.setViewportSize({ width: 1400, height: 1000 });
  await page.goto('/admin/metadata?tab=review');
  const rows = page.getByTestId('review-row');
  await expect(rows).toHaveCount(3);
  await expect(page.getByTestId('review-tab-AutoLinked')).toContainText('41');
  await expect(page.getByTestId('admin-attention-badge')).toContainText('5'); // 3 to review + 2 flags
  expect(seen.images).toEqual([]); // candidate posters cost requests: none before expanding
  await shot(page, 'c-05-review-desktop', true);

  await page.keyboard.press('e'); // expand the focused row: its posters load now
  await expect.poll(() => seen.images.length).toBe(3);
  await shot(page, 'c-06-review-expanded');

  await page.keyboard.press('j');
  await page.keyboard.press('a'); // accept row 2's top candidate: hidden now, sent only after the Undo window
  await expect(rows).toHaveCount(2);
  expect(seen.accepts).toEqual([]);
  await page.getByRole('button', { name: 'Undo' }).click();
  await expect(rows).toHaveCount(3);
  expect(seen.accepts).toEqual([]);

  await page.keyboard.press('d'); // Don't match the focused row, then let the window close
  await expect(rows).toHaveCount(2);
  await expect.poll(() => seen.bulks.length, { timeout: 12_000 }).toBe(1);
  expect(seen.bulks[0]).toEqual({ action: 'DontMatch', nodeIds: ['r2'] });

  await pageTab(page, 'Flags').click();
  await expect(page.getByTestId('flag-row')).toHaveCount(2);
  await shot(page, 'c-07-flags-desktop', true);
  await pageTab(page, 'Runs').click();
  await expect(page.getByTestId('run-live')).toBeVisible();
  await shot(page, 'c-08-runs-desktop', true);
  expect(foreign).toEqual([]);
});

test('phone: review cards with a bottom action bar, no inline actions', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  await mockStage2(page);
  await page.goto('/admin/metadata?tab=review');
  await expect(page.getByTestId('review-row')).toHaveCount(3);
  const bar = page.getByTestId('review-bottombar');
  await expect(bar).toBeVisible();
  await expect(bar.getByTestId('bar-accept')).toBeVisible();
  await expect(page.getByTestId('review-accept')).toHaveCount(0); // no inline actions on phone
  await shot(page, 'c-09-review-phone');
  await pageTab(page, 'Settings').click();
  await expect(page.getByTestId('metadata-settings-card')).toBeVisible();
  await shot(page, 'c-10-settings-phone', true);
  await pageTab(page, 'Flags').click();
  await expect(page.getByTestId('flag-row')).toHaveCount(2);
  await shot(page, 'c-11-flags-phone', true);
  expect(foreign).toEqual([]);
});
