import { test, expect, Page, APIRequestContext, Request, Route } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * The Metadata Manager admin page (metadata stage 2, lane C; renamed from "Series
 * metadata" in 1.27.0) - NEVER the real network:
 * - the account menu opens /admin/metadata (the main admin page has no tile of its own
 *   any more - Logging stays its last card), whose own summary tile sits above four
 *   tabs that render with the stage-2 endpoints implemented OR still answering 501 (the
 *   page explains that instead of breaking);
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

test('account menu opens Metadata Manager (/admin/metadata) with its own summary tile and five tabs; admin page has no tile', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  await page.goto('/admin');
  // 1.27.0: the summary tile no longer sits on the main admin page (owner decision 2) - admins
  // reach Metadata Manager from the account menu instead, which keeps its own attention badge.
  await expect(page.getByTestId('metadata-summary-tile')).toHaveCount(0);
  await expect(page.getByTestId('metadata-settings-card')).toHaveCount(0); // the card moved
  // The grid's last column ends with the Debug Logging card (a debugging tool, the last card; owner, 2026-09-26).
  const lastCard = await page.getByTestId('admin-grid').evaluate((el) => el.lastElementChild?.lastElementChild?.tagName.toLowerCase());
  expect(lastCard).toBe('app-debug-log-card');

  await page.locator('button', { has: page.getByTestId('admin-attention-badge') }).click();
  await page.getByTestId('nav-metadata').click();
  await expect(page).toHaveURL(/\/admin\/metadata$/);
  // The summary tile now lives at the TOP of the page itself, above the tabs, in place of the
  // old one-line summary.
  const tile = page.getByTestId('metadata-summary-tile');
  await expect(tile).toBeVisible();
  await expect(tile.getByTestId('tile-open')).toHaveCount(0); // in-page: no self-link
  await tile.scrollIntoViewIfNeeded();
  await shot(page, 'c-01-metadata-manager-tile');

  const tabs = page.locator('.mat-mdc-tab-header').getByRole('tab');
  // Settings, Review, Flags, Runs + Missing (1.28.0, the missing volumes / chapters report) + Completion (1.32.0; Official releases in 1.30.0).
  await expect(tabs).toHaveCount(6);
  await expect(tabs.filter({ hasText: 'Missing' })).toHaveCount(1);
  await expect(tabs.filter({ hasText: 'Completion' })).toHaveCount(1);
  await expect(page.getByTestId('metadata-settings-card')).toBeVisible();
  await shot(page, 'c-02-settings-tab', true);

  // The tile's own stats switch tabs in place (owner decision 2).
  await tile.getByTestId('tile-review').click();
  await expect(page).toHaveURL(/tab=review/);
  await page.goto('/admin/metadata');

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
  // Start from web lookups on (the current consent - read from the server, so a consent bump such as
  // 1.28.0's does not break this test) and automatic matching off.
  const versions = (await (await page.request.get('/api/v1/admin/metadata/settings')).json()) as
    { currentConsentVersion: number; currentAutoConsentVersion: number };
  await putSettings(page, { fetchEnabled: true, acceptedConsentVersion: versions.currentConsentVersion });
  await putSettings(page, { autoMatchEnabled: false });
  const usedBefore = (await settings(page)).budgetUsedToday;

  await page.goto('/admin/metadata');
  const auto = page.getByTestId('md-auto');
  await auto.scrollIntoViewIfNeeded();
  const autoSwitch = auto.getByTestId('md-auto-switch').getByRole('switch');
  // After consent the text folds behind "What is sent?".
  if (await auto.getByTestId('md-auto-consented').count()) await auto.getByTestId('md-auto-consent-toggle').click();
  await expect(auto.getByTestId('md-auto-consent-text')).toContainText('What is sent automatically:');
  await expect(auto.getByTestId('md-auto-consent-text')).toContainText('nobody reviews before it is sent');
  await expect(auto.getByTestId('md-auto-consent-text')).toContainText('checked once more under the new rules');
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
  expect((await put).postDataJSON()).toEqual({ autoMatchEnabled: true, acceptedAutoConsentVersion: versions.currentAutoConsentVersion });
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
  // 1.30.0: the first two candidates are one series family (a main story and its sequel) - shown together with their roles.
  { nodeId: 'r1', nodeKind: 'Folder', displayName: 'Synthetic Saga', libraryId: 'lib-x', libraryName: 'Sample Library',
    trail: ['Manga'], workClass: 'Series', matchLevel: 'Folder', itemCount: 24, openFlagCount: 0, reasons: ['close_second', 'series_family'],
    candidates: [{ ...cand(1, 'Synthetic Saga', 0.94, ['close_second']), familyGroup: 1, familyRole: 'main_story' },
      { ...cand(2, 'Synthetic Saga Returns', 0.91), familyGroup: 1, familyRole: 'sequel' }, cand(3, 'Synthetic Saga (Novel)', 0.9, ['type'])] },
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

/** `later`: the rows set aside ("Later", 1.33.0) as the fake server remembers them - across reloads, like the real one. */
interface Mocked { accepts: string[]; bulks: unknown[]; images: string[]; later: Map<string, string> }

// 1.33.0 "Same author": synthetic doujin-shaped works in two folders - balanced and unbalanced leading tags; the server's
// grouping (a circle and its artist are one author) is served as the hints it would compute.
const doujin = (nodeId: string, displayName: string, folder: [string, string], author: boolean, inFolder: number) => ({
  nodeId, nodeKind: 'Archive', displayName, libraryId: 'lib-x', libraryName: 'Sample Library', trail: ['Doujins', folder[1]],
  parentNodeId: folder[0], workClass: 'CollectionLeaf', matchLevel: 'Archive', itemCount: 1, openFlagCount: 0, reasons: ['one_shot'],
  candidates: [cand(1, displayName.replace(/^.*\]\s*/, ''), 0.7, ['one_shot'])],
  sameAuthor: author ? { key: 'samplecircle', label: 'Sample Circle', others: 2 } : null,
  sameFolder: { key: folder[0], label: folder[1], others: inFolder - 1 },
});
const FOLDER_ONE: [string, string] = ['fo1', 'Doujins One'];
const FOLDER_TWO: [string, string] = ['fo2', 'Doujins Two'];
const DOUJIN_ITEMS = [
  doujin('dj1', '[Sample Circle (Sample Artist)] Morning Story', FOLDER_ONE, true, 2),
  doujin('dj2', 'Sample Circle] Evening Story', FOLDER_ONE, true, 2),
  doujin('dj3', '[Sample Artist] Night Story', FOLDER_TWO, true, 2),
  doujin('dj4', 'Other Group] Lone Story', FOLDER_TWO, false, 2),
];
const AUTHORS = { items: [{ key: 'samplecircle', label: 'Sample Circle', count: 3, later: 0 }] };

async function mockStage2(page: Page, reviewItems: { nodeId: string }[] = REVIEW_ITEMS): Promise<Mocked> {
  const seen: Mocked = { accepts: [], bulks: [], images: [], later: new Map() };
  const json = (route: Route, body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/v1/admin/metadata/review/summary**', (r) => json(r, { ...SUMMARY, later: seen.later.size }));
  await page.route(/\/api\/v1\/admin\/metadata\/review\?/, (r) => {
    const query = new URL(r.request().url()).searchParams;
    const tab = query.get('tab') ?? 'NeedsReview';
    // The server's order: rows not set aside first, then the rows set aside, oldest first; `later` filters.
    const later = query.get('later');
    const author = query.get('author');
    const folder = query.get('folder');
    const items = (tab !== 'NeedsReview' ? [] : [
      ...reviewItems.filter((i) => !seen.later.has(i.nodeId) && later !== 'true'),
      ...[...seen.later].map(([id, at]) => ({ ...reviewItems.find((i) => i.nodeId === id)!, laterAt: at })).filter(() => later !== 'false'),
    ]).filter((i) => {
      const it = i as { sameAuthor?: { key: string } | null; sameFolder?: { key: string } | null };
      return (!author || it.sameAuthor?.key === author) && (!folder || it.sameFolder?.key === folder);
    });
    return json(r, { tab, items, total: items.length });
  });
  await page.route('**/api/v1/admin/metadata/review/authors**', (r) => json(r, AUTHORS));
  await page.route(/\/api\/v1\/admin\/metadata\/review\/[^/]+\/later$/, (r) => {
    const id = new URL(r.request().url()).pathname.split('/').at(-2)!;
    if (r.request().method() === 'POST') {
      if (!seen.later.has(id)) seen.later.set(id, new Date().toISOString());
    } else {
      seen.later.delete(id);
    }
    return r.fulfill({ status: 204 });
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

test('review dashboard with synthetic contract-shaped data: keyboard, deferred Undo, selected covers, other posters on expand', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  const seen = await mockStage2(page);
  await page.setViewportSize({ width: 1400, height: 1000 });
  await page.goto('/admin/metadata?tab=review');
  const rows = page.getByTestId('review-row');
  await expect(rows).toHaveCount(3);
  await expect(page.getByTestId('review-tab-AutoLinked')).toContainText('41');
  await expect(page.getByTestId('admin-attention-badge')).toContainText('5'); // 3 to review + 2 flags
  // 1.28.0 (owner): each row shows the SELECTED candidate's cover next to yours - one image per row (rank 1 here);
  // every other candidate's poster still costs nothing until its row is expanded.
  const tokens = () => [...new Set(seen.images.map((u) => u.split('/candidates/')[1].split('/')[0]))].sort();
  await expect.poll(tokens).toEqual(['tok-14-1', 'tok-17-1', 'tok-20-1']);
  // 1.30.0 (owner): a series family is shown together, flagged, each candidate with its role.
  const family = rows.first().getByTestId('review-family');
  await expect(family).toContainText('Same series family - check which one');
  await expect(family.getByTestId('review-candidate')).toHaveCount(2);
  await expect(family.getByTestId('review-family-role')).toHaveText(['Main story', 'Sequel']);
  await expect(rows.first().getByTestId('review-reason')).toHaveText(['Close second', 'Series family']);
  await shot(page, 'c-05-review-desktop', true);

  await page.keyboard.press('e'); // expand the focused row: its other candidates' posters load now
  await expect.poll(tokens).toEqual(['tok-14-1', 'tok-17-1', 'tok-20-1', 'tok-22-2', 'tok-22-3']);
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

test('review Later (1.33.0): the row moves to the end, stays there after a reload, and has its own filter', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  const seen = await mockStage2(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/admin/metadata?tab=review');
  const names = page.getByTestId('review-name');
  await expect(names).toHaveText(['Synthetic Saga', 'Example Chronicle', 'Placeholder One-Shot']);
  await expect(page.getByTestId('review-later-filter')).toHaveCount(0); // nothing set aside yet

  const sent = page.waitForResponse((r) => /\/review\/r1\/later$/.test(r.url()) && r.request().method() === 'POST');
  await page.keyboard.press('l'); // the focused (first) row
  await sent;
  await expect(names).toHaveText(['Example Chronicle', 'Placeholder One-Shot', 'Synthetic Saga']);
  await expect(page.getByTestId('review-row').last().getByTestId('review-later-tag')).toBeVisible();
  expect([...seen.later.keys()]).toEqual(['r1']);

  await page.reload(); // remembered on the server: still last after a reload
  await expect(names).toHaveText(['Example Chronicle', 'Placeholder One-Shot', 'Synthetic Saga']);
  await expect(page.getByTestId('review-later-tag')).toHaveCount(1);
  await expect(page.getByTestId('review-later-only')).toContainText('1');
  await expectFitsScreen(page, 'review tab with Later (desktop)');
  await shot(page, 'r-01-review-later-desktop', true);

  await page.getByTestId('review-later-only').click();
  await expect(names).toHaveText(['Synthetic Saga']);
  const back = page.waitForResponse((r) => /\/review\/r1\/later$/.test(r.url()) && r.request().method() === 'DELETE');
  await page.getByTestId('review-notLater').click();
  await back;
  await expect(names).toHaveCount(0);
  await page.getByTestId('review-later-all').click();
  await expect(names).toHaveText(['Synthetic Saga', 'Example Chronicle', 'Placeholder One-Shot']);
  expect(seen.later.size).toBe(0);
  expect(foreign).toEqual([]);
});

test('phone: Later from the bottom bar, and the Later filter fits the screen', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  const seen = await mockStage2(page);
  await page.goto('/admin/metadata?tab=review');
  await expect(page.getByTestId('review-row')).toHaveCount(3);
  await page.getByTestId('review-name').nth(1).click(); // focus Example Chronicle
  const bar = page.getByTestId('review-bottombar');
  await expect(bar.getByTestId('bar-name')).toHaveText('Example Chronicle');
  const sent = page.waitForResponse((r) => /\/review\/r2\/later$/.test(r.url()));
  await bar.getByTestId('bar-later').click();
  await sent;
  await expect(page.getByTestId('review-name')).toHaveText(['Synthetic Saga', 'Placeholder One-Shot', 'Example Chronicle']);
  await expect(page.getByTestId('review-later-filter')).toBeVisible();
  await expectFitsScreen(page, 'review tab with the Later filter (phone)');
  await page.getByTestId('review-name').last().click();
  await expect(bar.getByTestId('bar-notLater')).toBeVisible();
  await expectFitsScreen(page, 'review tab, a row set aside (phone)');
  await shot(page, 'r-02-review-later-phone');
  expect([...seen.later.keys()]).toEqual(['r2']);
  expect(foreign).toEqual([]);
});

test('Same author (1.33.0): a row\'s chip lists the circle\'s works together, bulk Later on them, the Authors list', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  const seen = await mockStage2(page, DOUJIN_ITEMS);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/admin/metadata?tab=review');
  const rows = page.getByTestId('review-row');
  await expect(rows).toHaveCount(4);
  await expect(rows.first().getByTestId('review-same-author')).toHaveText(/2 more by Sample Circle/);
  await expect(rows.last().getByTestId('review-same-author')).toHaveCount(0); // a name nobody else has
  await expectFitsScreen(page, 'review tab with Same author chips (desktop)');

  await rows.first().getByTestId('review-same-author').click();
  await expect(rows).toHaveCount(3);
  await expect(page.getByTestId('review-group-chip')).toContainText('By Sample Circle (3)');
  await shot(page, 'r-03-same-author-desktop', true);
  await page.getByTestId('review-select-all').click();
  await page.getByTestId('bulk-Later').click(); // the whole group set aside at once
  await expect.poll(() => seen.bulks.at(-1)).toEqual({ action: 'Later', nodeIds: ['dj1', 'dj2', 'dj3'] });
  await page.getByTestId('review-group-clear').click();
  await expect(rows).toHaveCount(4);

  await page.getByTestId('review-authors').click();
  await page.getByTestId('review-author').filter({ hasText: 'Sample Circle' }).click();
  await expect(rows).toHaveCount(3);
  await page.getByTestId('review-group-clear').click();
  await rows.last().getByTestId('review-same-folder').click();
  await expect(page.getByTestId('review-name')).toHaveText(['[Sample Artist] Night Story', 'Other Group] Lone Story']);
  await expect(page.getByTestId('review-group-chip')).toContainText('In Doujins Two (2)');
  expect(foreign).toEqual([]);
});

test('phone: Same folder and the Authors bottom sheet fit the screen', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  await mockStage2(page, DOUJIN_ITEMS);
  await page.goto('/admin/metadata?tab=review');
  const rows = page.getByTestId('review-row');
  await expect(rows).toHaveCount(4);
  await expectFitsScreen(page, 'review tab with Same author chips (phone)');
  await rows.first().getByTestId('review-same-folder').click();
  await expect(rows).toHaveCount(2);
  await expect(page.getByTestId('review-group-chip')).toContainText('In Doujins One (2)');
  await expectFitsScreen(page, 'review tab filtered by folder (phone)');
  await shot(page, 'r-04-same-folder-phone');
  await page.getByTestId('review-group-clear').click();
  await expect(rows).toHaveCount(4);

  await page.getByTestId('review-authors').click();
  const sheet = page.getByTestId('review-authors-sheet');
  await expect(sheet).toBeVisible();
  await expectFitsScreen(page, 'Authors bottom sheet (phone)');
  await shot(page, 'r-05-authors-sheet-phone');
  await sheet.getByTestId('review-author').first().click();
  await expect(sheet).toHaveCount(0);
  await expect(rows).toHaveCount(3);
  await expect(page.getByTestId('review-group-chip')).toContainText('By Sample Circle (3)');
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
  // The rows fit the screen: covers above the text, the title one line wide (1.28.0 - 1.29.1 squeezed it to one letter per line).
  await expectFitsScreen(page, 'review tab (phone)');
  // 1.30.0: the series family block on a phone card: the note and both roles visible, nothing past the screen edge.
  const family = page.getByTestId('review-row').first().getByTestId('review-family');
  await expect(family).toContainText('Same series family - check which one');
  await expect(family.getByTestId('review-family-role')).toHaveText(['Main story', 'Sequel']);
  await family.scrollIntoViewIfNeeded();
  await expectFitsScreen(page, 'review tab, series family (phone)');
  await page.getByTestId('review-name').first().click();
  await expectFitsScreen(page, 'review tab, focused row (phone)');
  // The text column has room and the name is one line (it was ~10 px wide and 200+ px tall).
  const column = await page.getByTestId('review-row').first().locator('.main').boundingBox();
  const name = await page.getByTestId('review-name').first().boundingBox();
  expect(column!.width).toBeGreaterThan(200);
  expect(name!.height).toBeLessThan(40);
  await shot(page, 'c-09-review-phone');
  await pageTab(page, 'Settings').click();
  await expect(page.getByTestId('metadata-settings-card')).toBeVisible();
  await shot(page, 'c-10-settings-phone', true);
  await pageTab(page, 'Flags').click();
  await expect(page.getByTestId('flag-row')).toHaveCount(2);
  await shot(page, 'c-11-flags-phone', true);
  expect(foreign).toEqual([]);
});
