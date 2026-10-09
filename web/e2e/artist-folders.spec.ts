import { test, expect, Page, Route } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * "Artist folder" (1.37.0): a folder of one artist's works - NEVER the real network (the review API is answered IN THE BROWSER with
 * synthetic, contract-shaped data):
 * - Metadata Manager > Review: the key r on a waiting FOLDER opens the "Artist folder" dialog (the folder's name, "Story & art"); the
 *   folder is marked only when the Undo window closes, with the artist the dialog returned; an archive row never offers it;
 * - the Collections tab lists the artist folder with its artist and removes the mark;
 * - desktop and phone widths fit the screen, the dialog included; the phone bottom bar offers "Artist".
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

async function shot(page: Page, name: string, fullPage = false): Promise<void> {
  if (!SHOTS) return;
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage });
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

const json = (route: Route, body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

const cand = (rank: number, title: string, score: number) => ({
  rank, provider: 'mangaupdates', externalId: String(7000 + rank), title, providerType: 'Manga', format: 'Comic', year: 2010 + rank,
  volumes: 3, titleScore: score, adjustedScore: score, reasons: ['review_only'], imageToken: `atok-${rank}`,
});

// A waiting folder of one artist's works (review only as one folder), and a waiting archive.
const WAITING = [
  { nodeId: 'aw1', nodeKind: 'Folder', displayName: 'Beta Painter', libraryId: 'lib-x', libraryName: 'Sample Library',
    trail: ['Artists'], workClass: 'Ambiguous', matchLevel: 'ReviewOnly', itemCount: 6, openFlagCount: 0, reasons: ['review_only'],
    candidates: [cand(1, 'Qzv Harbor Tale', 0.7)] },
  { nodeId: 'aw2', nodeKind: 'Archive', displayName: 'Qzv Quiet Orchard.cbz', libraryId: 'lib-x', libraryName: 'Sample Library',
    trail: ['Loose'], workClass: 'CollectionLeaf', matchLevel: 'Archive', itemCount: 1, openFlagCount: 0, reasons: ['close_second'],
    candidates: [cand(1, 'Qzv Quiet Orchard', 0.86)] },
];
const MARKED = [
  { nodeId: 'am1', nodeKind: 'Folder', displayName: 'Gamma Inker', libraryId: 'lib-x', libraryName: 'Sample Library',
    trail: ['Artists'], itemCount: 9, openFlagCount: 0, reasons: [], candidates: [], artist: { name: 'Gamma Inker', role: 'author' },
    link: { state: 'ArtistFolder', updatedAt: '2026-10-08T10:00:00Z' } },
];
const SUMMARY = { needsReview: 2, later: 0, autoLinked: 0, unmatched: 0, openFlags: 0, dontMatch: 0, confirmed: 0, missingFolders: 0,
  pending: 0, recheckPending: 0, collections: 0, artistFolders: 1 };

interface Seen { accepts: { url: string; body: unknown }[]; clears: string[] }

async function mockReview(page: Page): Promise<Seen> {
  const seen: Seen = { accepts: [], clears: [] };
  await page.route('**/api/v1/admin/metadata/review/summary**', (r) => json(r, SUMMARY));
  await page.route(/\/api\/v1\/admin\/metadata\/review\?/, (r) => {
    const tab = new URL(r.request().url()).searchParams.get('tab') ?? 'NeedsReview';
    const items = tab === 'NeedsReview' ? WAITING.filter((i) => !seen.accepts.some((a) => a.url.includes(`/${i.nodeId}/`)))
      : tab === 'Collections' ? MARKED : [];
    return json(r, { tab, items, total: items.length });
  });
  await page.route('**/api/v1/admin/metadata/review/authors**', (r) => json(r, { items: [] }));
  await page.route(/\/api\/v1\/admin\/metadata\/review\/[^/]+\/accept-artist$/, (r) => {
    seen.accepts.push({ url: r.request().url(), body: r.request().postDataJSON() });
    return json(r, { change: { nodeId: 'aw1', link: { nodeId: 'aw1', state: 'ArtistFolder', updatedAt: 'x' } },
      artist: { name: 'Beta Painter', role: 'author' }, creatorAdded: true, queued: 6 });
  });
  await page.route(/\/api\/v1\/admin\/metadata\/nodes\/[^/]+\/artist-folder$/, (r) => {
    if (r.request().method() === 'DELETE') seen.clears.push(r.request().url());
    return json(r, { nodeId: 'am1' });
  });
  await page.route('**/api/v1/admin/metadata/candidates/*/image', (r) => r.fulfill({ status: 404, body: '' }));
  await page.route(/\/api\/v1\/admin\/metadata\/flags(\?|$)/, (r) => json(r, { items: [], total: 0 }));
  await page.route(/\/api\/v1\/admin\/metadata\/runs(\?|$)/, (r) => json(r, { status: { enabled: false, active: false, pending: 0 }, items: [] }));
  return seen;
}

test('review: the key r marks a waiting folder an artist folder after the Undo window; the Collections tab removes the mark', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await login(page);
  const seen = await mockReview(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/admin/metadata?tab=review');
  const rows = page.getByTestId('review-row');
  await expect(rows).toHaveCount(2);
  await expect(rows.first().getByTestId('review-artistFolder')).toBeVisible();
  await expect(rows.nth(1).getByTestId('review-artistFolder')).toHaveCount(0); // an archive is never an artist folder

  await page.keyboard.press('r'); // the first row (the folder) is focused
  const name = page.getByTestId('artist-folder-name');
  await expect(name).toHaveValue('Beta Painter');
  await expect(page.getByTestId('artist-folder-role')).toContainText('Story & art');
  await expect(page.getByTestId('artist-folder-explain')).toContainText('never sent');
  await expectFitsScreen(page, 'artist folder dialog (desktop)');
  await shot(page, 'a-01-artist-folder-dialog-desktop');
  await page.getByTestId('artist-folder-save').click();

  await expect(rows).toHaveCount(1);
  expect(seen.accepts).toEqual([]); // nothing sent inside the Undo window
  await expect.poll(() => seen.accepts.length, { timeout: 12_000 }).toBe(1);
  expect(seen.accepts[0].url).toMatch(/\/review\/aw1\/accept-artist$/);
  expect(seen.accepts[0].body).toEqual({ name: 'Beta Painter', role: 'author' });

  await page.getByTestId('review-tab-Collections').click();
  await expect(rows).toHaveCount(1);
  await expect(rows.first().getByTestId('review-link')).toContainText('Artist folder: Gamma Inker');
  await expectFitsScreen(page, 'Collections tab with an artist folder (desktop)');
  await shot(page, 'a-02-collections-artist-desktop', true);
  await rows.first().getByTestId('review-clearArtistFolder').click();
  await expect.poll(() => seen.clears.length, { timeout: 12_000 }).toBe(1);
  expect(seen.clears[0]).toMatch(/\/nodes\/am1\/artist-folder$/);
  expect(foreign).toEqual([]);
});

test('phone: the bottom bar offers "Artist" for a waiting folder, and the dialog fits the screen', async ({ page, baseURL }) => {
  const foreign = watchForeignRequests(page, baseURL!);
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  await mockReview(page);
  await page.goto('/admin/metadata?tab=review');
  await expect(page.getByTestId('review-row')).toHaveCount(2);
  await page.getByTestId('review-name').first().click();
  const bar = page.getByTestId('review-bottombar');
  await expect(bar.getByTestId('bar-artistFolder')).toContainText('Artist');
  await bar.getByTestId('bar-artistFolder').click();
  await expect(page.getByTestId('artist-folder-name')).toHaveValue('Beta Painter');
  await expectFitsScreen(page, 'artist folder dialog (phone)');
  await shot(page, 'a-03-artist-folder-dialog-phone');
  expect(foreign).toEqual([]);
});
