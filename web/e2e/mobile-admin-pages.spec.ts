import { test, expect, Locator, Page, APIRequestContext } from '@playwright/test';
import { login, readerPrefs, routeFakeItem } from './reader-fixture';

/**
 * Touch targets and names on the admin pages, the Metadata Manager, the series page, Tracking and the reader options sheet
 * (1.51.0, the mobile sweep's M6 and M15-M19). On a touch screen (`pointer: coarse`: Playwright's `hasTouch`) every control
 * the sweep found below 40 px is a 40 x 40 px target on its own, and the Backups file picker has an accessible name.
 * A mouse-driven window is not touched by these rules (the last test pins the desktop look of two of them).
 * Contract-shaped answers for the Tracking page (the instance has no tracker); everything else is the real app.
 */
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const VOLUMES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-volumes` : undefined;
const LIBRARY_NAME = 'Volume Stacks';
const MIN = 40;

interface Node { id: string; displayName: string }

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get('/api/v1/auth/csrf');
  const { token } = await res.json();
  return { 'X-MangaPixer-Csrf': token };
}

/** The synthetic volume library (registered by whichever spec runs first) and its series folder: [libraryId, folderId]. */
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
    return folder;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).not.toBe('');
  return [lib!.id, folder];
}

/** Every locator's own box is at least 40 x 40 (a link, button, field or tab the sweep measured below that). */
async function expectTouchSize(page: Page, targets: Record<string, Locator>): Promise<void> {
  const small: string[] = [];
  for (const [name, locator] of Object.entries(targets)) {
    await expect(locator.first(), `${name} is shown`).toBeVisible();
    for (const one of await locator.all()) {
      await one.scrollIntoViewIfNeeded();
      const box = (await one.boundingBox())!;
      if (box.height < MIN - 0.5 || box.width < MIN - 0.5) small.push(`${name}: ${Math.round(box.width)} x ${Math.round(box.height)} px`);
    }
  }
  expect(small, 'targets below 40 px').toEqual([]);
}

function connectedMal() {
  return {
    slug: 'mal', name: 'MyAnimeList', allowed: true, networkDisabledByConfig: false, connectKind: 'OAuthPkce', currentConsentVersion: 2,
    supportsPrivateEntries: false,
    account: {
      tracker: 'mal', state: 'Ok', displayName: 'a-reader', connectedAt: '2026-10-13T10:00:00Z', lastSuccessAt: '2026-10-13T11:00:00Z',
      consentRenewalNeeded: false, addStartedSeries: true, privateEntries: true, syncScores: false, requestsToday: 12, dailyRequestCap: 2000,
    },
  };
}

function trackingRow(seriesId: string) {
  return {
    seriesId, title: `Synthetic Saga ${seriesId}`, nodeId: `node${seriesId}`, tracker: 'mal', remoteId: '2', remoteIdSource: 'MangaDex', state: 'Ok',
    bucket: 'InSync', errorCode: null, dontSync: false, chapters: 120, volumes: 12, allChapters: 120, allVolumes: 12, heldChapters: null,
    heldVolumes: null, totalChapters: 300, totalVolumes: 30, remoteChapters: 120, remoteVolumes: 12, remoteStatus: 'Reading',
    remoteMissing: false,
    dates: { supported: true, startedOn: '2026-10-01', startedOnOverridden: false, finishedOn: null, finishedOnOverridden: false,
      remoteStartedOn: '2026-10-01', remoteFinishedOn: null, pending: false },
    fromTracker: { possible: false, block: 'NotAhead' }, toTracker: { possible: false, block: 'NotAhead' },
    lastSyncAt: '2026-10-13T10:00:00Z', lastSentAt: '2026-10-13T10:00:00Z', updatedAt: '2026-10-13T10:00:00Z',
  };
}

test.describe('touch screen', () => {
  test.use({ hasTouch: true, isMobile: true, viewport: { width: 390, height: 844 } });

  test('Administration: the Backups file picker is named, and the cards\' controls are 40 px targets (M6, M15, M18)', async ({ page }) => {
    test.setTimeout(180_000);
    await login(page);
    if (VOLUMES_ROOT) await ensureLibrary(page);
    await page.goto('/admin');
    const picker = page.getByLabel('Backup file to restore (.db or .db.gz)');
    await expect(picker).toBeVisible();
    await expectTouchSize(page, {
      'restore file picker': picker,
      'backup frequency': page.locator('#bk-interval'),
      'keep the newest': page.locator('#bk-retention'),
      'API token name': page.getByTestId('api-token-name'),
      'API token expiry': page.getByTestId('api-token-expiry'),
      'API token scope': page.getByTestId('api-token-scope-read').locator('xpath=ancestor::label'),
      'trash retention': page.getByTestId('trash-retention'),
      'trash: change in Scheduled jobs': page.getByTestId('trash-auto-link'),
      'analytics sort headers': page.locator('.sort-btn'),
      ...(VOLUMES_ROOT ? { 'scan: change in Scheduled jobs': page.getByTestId('scan-schedule-link') } : {}),
    });
  });

  test('Administration in landscape: the Scheduled jobs selects are 40 px targets (M18)', async ({ page }) => {
    test.setTimeout(120_000);
    await login(page);
    await page.setViewportSize({ width: 844, height: 390 });
    await page.goto('/admin');
    await expectTouchSize(page, {
      'refresh hour': page.getByTestId('job-hour-metadata-refresh'),
      'backup hour': page.getByTestId('job-hour-backup'),
      'cache hour': page.getByTestId('job-hour-cache-eviction'),
      'ongoing cadence': page.getByTestId('job-cadence-ongoing'),
    });
  });

  test('Metadata Manager: the back link, Show progress, the review tabs and Authors are 40 px targets (M15)', async ({ page }) => {
    test.setTimeout(120_000);
    await login(page);
    await page.goto('/admin/metadata');
    await expectTouchSize(page, {
      'back to Administration': page.getByTestId('admin-metadata').locator('.back a'),
      'show progress': page.getByTestId('md-volume-covers-progress'),
    });
    await page.goto('/admin/metadata?tab=review');
    await expectTouchSize(page, {
      'Needs review tab': page.getByTestId('review-tab-NeedsReview'),
      'Confirmed tab': page.getByTestId('review-tab-Confirmed'),
    });
    await expect(page.getByTestId('review-authors')).toBeVisible();
    await expectTouchSize(page, { Authors: page.getByTestId('review-authors') });
  });

  test('series page: Back to folder (portrait) and the library links (landscape) are 40 px targets (M16)', async ({ page }) => {
    test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
    test.setTimeout(180_000);
    await login(page);
    const [, folderId] = await ensureLibrary(page);
    // The synthetic folder has no ComicInfo, so the page would have no items table or source links: a contract-shaped answer has them.
    await page.route(`**/api/v1/nodes/${folderId}/series-info*`, (r) => r.fulfill({ json: {
      nodeId: folderId, nodeKind: 'Folder', anchorNodeId: folderId, anchorKind: 'Folder', anchorDisplayName: 'Synthetic Saga', libraryId: 'e2elibrary',
      state: 'ComicInfo', title: 'Synthetic Saga', precedence: 'WebFirst', precedenceSource: 'Default',
      comicInfo: { itemsWithComicInfo: 2, itemsTotal: 2, webLinks: ['https://www.mangaupdates.com/series/abc123/synthetic-saga'] },
      items: [
        { nodeId: 'e2eitem1', displayName: 'Part 1', number: '1', volume: 1, title: 'Part 1', year: 2020 },
        { nodeId: 'e2eitem2', displayName: 'Part 2', number: '2', volume: 1, title: 'Part 2', year: 2021 },
      ],
    } }));
    await page.goto(`/series/${folderId}`);
    await expectTouchSize(page, { 'Back to folder': page.locator('.page .back a') });
    await page.setViewportSize({ width: 844, height: 390 });
    await page.goto(`/series/${folderId}`);
    await expect(page.getByTestId('series-items')).toBeVisible();
    await expectTouchSize(page, {
      'Back to folder': page.locator('.page .back a'),
      'library items': page.getByTestId('series-items').locator('a'),
      'source link': page.locator('.page .section p a'),
    });
  });

  test('Tracking: the series link, Open on the tracker and Set dates are 40 px targets (M17)', async ({ page }) => {
    test.setTimeout(120_000);
    await login(page);
    await page.route(/\/api\/v1\/trackers$/, (r) => r.fulfill({ json: { trackers: [connectedMal()] } }));
    await page.route(/\/api\/v1\/trackers\/attention$/, (r) => r.fulfill({ json: { total: 0, trackers: [] } }));
    await page.route(/\/api\/v1\/trackers\/mal\/series(\?.*)?$/, (r) => r.fulfill({ json: {
      items: [trackingRow('a1'), trackingRow('a2')], page: 1, pageSize: 50, totalCount: 2,
      counts: { all: 2, inSync: 2, mangaPixerAhead: 0, trackerAhead: 0, leftAsIs: 0, held: 0, noId: 0, notSynced: 0, problem: 0, unchecked: 0, attention: 0 },
    } }));
    await page.goto('/tracking');
    await expect(page.getByTestId('tracking-row-a1')).toBeVisible();
    await expectTouchSize(page, {
      'series link': page.getByTestId('tracking-row-a1').locator('a.title'),
      'open on MyAnimeList': page.getByTestId('tracking-remote-link-a1'),
      'set dates': page.getByTestId('tracking-set-dates-a1'),
      'tracker tab': page.getByTestId('tracking-tab-mal'),
      'filter chip': page.getByTestId('tracking-chip-All'),
    });
  });

  test('reader options sheet: the GPU status row is a 40 px target (M19)', async ({ page }) => {
    test.setTimeout(120_000);
    await readerPrefs(page, { upscaler: 'smooth', animation: 'none' });
    await routeFakeItem(page, { id: 'e2emobileadmin', pageCount: 4, pageWidth: 300, pageHeight: 420, readerMode: 'PagedLtr' });
    await login(page);
    await page.goto('/reader/e2emobileadmin');
    await expect(page.locator('.reader-toolbar .page-info')).toHaveText('1 / 4');
    await page.getByRole('button', { name: 'Reader options' }).click();
    const sheet = page.locator('app-reader-options-sheet');
    await expect(sheet).toBeVisible();
    await expectTouchSize(page, { 'GPU status row': sheet.locator('.hint-tap') });
  });
});

test.describe('mouse', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test('a mouse-driven window keeps the compact admin look (the touch rules do not apply)', async ({ page }) => {
    test.setTimeout(120_000);
    await login(page);
    await page.goto('/admin');
    const frequency = await page.locator('#bk-interval').boundingBox();
    expect(frequency!.height, 'Backups frequency select stays compact').toBeLessThan(MIN);
    const picker = await page.getByLabel('Backup file to restore (.db or .db.gz)').boundingBox();
    expect(picker!.height, 'file picker stays compact').toBeLessThan(MIN);
  });
});
