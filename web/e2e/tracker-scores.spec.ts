import { test, expect, Page, Route } from '@playwright/test';
import { login } from './reader-fixture';
import { expectFitsScreen } from './layout';

/**
 * 1.53.0 (lane T): the user's own tracker score on the Tracking page's account card - the switch "Keep my score in step with my
 * MangaPixer rating" (saved per user, then the import is offered), "Take my scores from <tracker>" (the summary; the kept, differing
 * ratings listed) and the per-row "Use <tracker>'s". Desktop, and a phone at 390 and 360 px (fits the screen, 40 px touch targets).
 * The E2E instance has no tracker, so the tracker routes answer contract-shaped data (`page.route`); no request leaves the instance.
 * Optional: E2E_SCREENSHOT_DIR saves the account card and the import result per width.
 */
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const MIN = 40;

function account(syncScores: boolean) {
  return {
    tracker: 'mal', state: 'Ok', displayName: 'a-reader', connectedAt: '2026-10-13T10:00:00Z', lastSuccessAt: '2026-11-10T11:00:00Z',
    consentRenewalNeeded: false, addStartedSeries: true, privateEntries: true, syncScores, requestsToday: 12, dailyRequestCap: 2000,
  };
}

function overview(syncScores: boolean) {
  return {
    trackers: [{
      slug: 'mal', name: 'MyAnimeList', allowed: true, networkDisabledByConfig: false, connectKind: 'OAuthPkce', currentConsentVersion: 2,
      supportsPrivateEntries: false, account: account(syncScores),
    }],
  };
}

const IMPORT = {
  scored: 14, matched: 9, filled: 5, same: 2, requests: 1, capReached: false,
  differ: [
    { seriesId: 'sa1', title: 'Synthetic Saga With A Rather Long Title For A Narrow Screen', nodeId: 'nodesa1', myScore: 4, trackerScore: 8 },
    { seriesId: 'sa2', title: 'Another Saga', nodeId: 'nodesa2', myScore: 9, trackerScore: 7 },
  ],
};

interface Seen { settings: unknown[]; imports: number; uses: { url: string; body: unknown }[] }

async function mockTrackers(page: Page): Promise<Seen> {
  const seen: Seen = { settings: [], imports: 0, uses: [] };
  let syncScores = false;
  await page.route(/\/api\/v1\/trackers$/, (r) => r.fulfill({ json: overview(syncScores) }));
  await page.route(/\/api\/v1\/trackers\/attention$/, (r) => r.fulfill({ json: { total: 0, trackers: [] } }));
  await page.route(/\/api\/v1\/trackers\/mal\/series(\?.*)?$/, (r) => r.fulfill({ json: {
    items: [], page: 1, pageSize: 50, totalCount: 0,
    counts: { all: 0, inSync: 0, mangaPixerAhead: 0, trackerAhead: 0, leftAsIs: 0, held: 0, noId: 0, notSynced: 0, problem: 0, unchecked: 0, attention: 0 },
  } }));
  await page.route(/\/api\/v1\/trackers\/mal\/settings$/, async (r: Route) => {
    const body = r.request().postDataJSON() as { syncScores?: boolean };
    seen.settings.push(body);
    if (typeof body.syncScores === 'boolean') syncScores = body.syncScores;
    await r.fulfill({ json: account(syncScores) });
  });
  await page.route(/\/api\/v1\/trackers\/mal\/scores\/import$/, async (r: Route) => {
    seen.imports++;
    await r.fulfill({ json: IMPORT });
  });
  await page.route(/\/api\/v1\/trackers\/mal\/scores\/[^/]+$/, async (r: Route) => {
    seen.uses.push({ url: r.request().url(), body: r.request().postDataJSON() });
    await r.fulfill({ status: 204, body: '' });
  });
  return seen;
}

async function runFlow(page: Page, seen: Seen, label: string): Promise<void> {
  await page.goto('/tracking');
  const card = page.getByTestId('tracker-account-mal');
  const toggle = page.getByTestId('tracker-sync-scores-mal').getByRole('switch');
  await expect(toggle).toBeVisible();
  await expect(toggle).toHaveAttribute('aria-checked', 'false');
  await expect(page.getByTestId('tracker-sync-scores-mal')).toContainText('Keep my score in step with my MangaPixer rating');
  if (SHOTS) await card.screenshot({ path: `${SHOTS}/tracker-scores-card-${label}.png` });

  const saved = page.waitForResponse((r) => r.url().endsWith('/api/v1/trackers/mal/settings') && r.request().method() === 'PUT');
  await toggle.click();
  await saved;
  expect(seen.settings).toEqual([{ syncScores: true }]);
  await expect(page.getByTestId('tracker-score-offer-mal')).toContainText('your MyAnimeList scores too');
  await expect(toggle).toHaveAttribute('aria-checked', 'true');

  await page.getByTestId('tracker-import-scores-mal').click();
  await expect(page.getByTestId('tracker-score-summary-mal'))
    .toContainText('5 unrated series now have your MyAnimeList score; 2 already the same; 2 differ - kept your MangaPixer ratings');
  expect(seen.imports).toBe(1);
  const row = page.getByTestId('tracker-score-row-sa1');
  await expect(row).toContainText('Yours 2 ★ · MyAnimeList 4 ★');
  await expect(page.getByTestId('tracker-score-use-sa2')).toBeVisible();
  if (SHOTS) await card.screenshot({ path: `${SHOTS}/tracker-scores-import-${label}.png` });

  const used = page.waitForResponse((r) => /\/scores\/sa1$/.test(r.url()));
  await page.getByTestId('tracker-score-use-sa1').click();
  await used;
  expect(seen.uses).toEqual([{ url: expect.stringMatching(/\/api\/v1\/trackers\/mal\/scores\/sa1$/), body: { score: 8 } }]);
  await expect(row).toContainText('Now 4 ★ (from MyAnimeList)');
  await expect(page.getByTestId('tracker-score-use-sa1')).toHaveCount(0);
  await expect(page.getByTestId('tracker-score-use-sa2')).toBeVisible();
}

test.describe('tracker scores - desktop', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test('the switch, Take my scores and Use MyAnimeList\'s on the account card', async ({ page }) => {
    test.setTimeout(120_000);
    await login(page);
    const seen = await mockTrackers(page);
    await runFlow(page, seen, '1280');
    await expectFitsScreen(page, 'Tracking, scores imported (1280)');
  });
});

test.describe('tracker scores - phone', () => {
  test.use({ hasTouch: true, isMobile: true, viewport: { width: 390, height: 844 } });

  for (const width of [390, 360]) {
    test(`fits a ${width} px phone, with 40 px targets`, async ({ page }) => {
      test.setTimeout(120_000);
      await page.setViewportSize({ width, height: 844 });
      await login(page);
      const seen = await mockTrackers(page);
      await runFlow(page, seen, String(width));
      await expectFitsScreen(page, `Tracking, scores imported (${width})`);
      const small: string[] = [];
      for (const [name, id] of [['Take my scores', 'tracker-import-scores-mal'], ['Use MyAnimeList\'s', 'tracker-score-use-sa2']]) {
        const box = (await page.getByTestId(id).boundingBox())!;
        if (box.height < MIN - 0.5 || box.width < MIN - 0.5) small.push(`${name}: ${Math.round(box.width)} x ${Math.round(box.height)}`);
      }
      const link = (await page.getByTestId('tracker-score-row-sa2').locator('a').boundingBox())!;
      if (link.height < MIN - 0.5) small.push(`series link: ${Math.round(link.height)} px high`);
      expect(small, 'targets below 40 px').toEqual([]);
    });
  }
});
