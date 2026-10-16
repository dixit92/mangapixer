import { test, expect, Page, APIRequestContext, Request } from '@playwright/test';
import { layoutProblems } from './layout';

/**
 * Tracker sync web (1.43.0) against a real instance with NO tracker reachable: nothing in these tests leaves the test instance.
 * The browser's requests to any other host are answered or refused by the test itself (`guardOutside`), and the server is never
 * asked to do anything that contacts a tracker: allowing a tracker, saving the app registration, starting a MyAnimeList sign-in
 * (the server only BUILDS the address) and finishing one with a bogus state (refused before any request) are all local; the
 * MangaUpdates form is refused in the browser before it is sent.
 * - an admin allows MyAnimeList after its consent text and saves this server's app registration (the secret is write-only);
 * - a user accepts the user consent and Connect answers with a MyAnimeList sign-in address (asserted, never followed);
 * - the callback page strips a bogus `code` / `state` from the address bar before the app's first API request and shows the
 *   invalid-state message;
 * - the MangaUpdates form refuses an empty password without a request;
 * - the selection's "Sync to trackers" menu (the overview is mocked as connected - connecting needs the tracker) stores a switch;
 * - the tracker cards, dialog and pages with contract-shaped data fit a phone and a tablet screen.
 * Versions are read from the server, never hard-coded.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const FIXTURE_ROOT = process.env['E2E_SERIES_FIXTURE_ROOT'];
const VOLUMES_ROOT = FIXTURE_ROOT ? `${FIXTURE_ROOT}-volumes` : undefined;
const VOLUMES_LIBRARY = 'Volume Stacks';
const SIZES = [{ width: 390, height: 844 }, { width: 820, height: 1180 }];
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];

interface AdminTracker { slug: string; enabled: boolean; acceptedConsentVersion: number | null; currentConsentVersion: number; clientId: string | null; clientSecretSet: boolean; redirectUri: string | null }

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

async function adminTracker(page: Page, slug: string): Promise<AdminTracker> {
  const res = await page.request.get(`/api/v1/admin/trackers/${slug}`);
  expect(res.ok(), await res.text()).toBeTruthy();
  return res.json();
}

/** Allows a tracker through the API (with the server's own current consent version) and optionally registers the app. */
async function allow(page: Page, slug: string, extra: Record<string, unknown> = {}): Promise<void> {
  const { currentConsentVersion } = await adminTracker(page, slug);
  const res = await page.request.put(`/api/v1/admin/trackers/${slug}`, {
    headers: await csrf(page.request), data: { enabled: true, acceptedConsentVersion: currentConsentVersion, ...extra },
  });
  expect(res.ok(), await res.text()).toBeTruthy();
}

/** Turns a tracker off and clears its registration, so later specs see a fresh server. */
async function reset(page: Page, slug: string): Promise<void> {
  await page.request.put(`/api/v1/admin/trackers/${slug}`, {
    headers: await csrf(page.request), data: { enabled: false, clientId: '', clientSecret: '', redirectUri: '' },
  });
}

/** Every request the browser makes to another host is answered here (a stub page) and recorded: nothing leaves the test. */
async function guardOutside(page: Page): Promise<string[]> {
  const outside: string[] = [];
  const own = new URL(test.info().project.use.baseURL ?? process.env['E2E_BASE_URL'] ?? 'http://127.0.0.1:8091').host;
  await page.context().route((url) => url.host !== own, (route) => {
    outside.push(route.request().url());
    return route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><title>stub</title><p>stub</p>' });
  });
  return outside;
}

test.describe('tracker sync web (1.43.0)', () => {
  test('an admin allows MyAnimeList after its consent text and saves this server\'s app registration', async ({ page }) => {
    test.setTimeout(120_000);
    const outside = await guardOutside(page);
    await login(page);
    await reset(page, 'mal');
    try {
      const server = await adminTracker(page, 'mal');
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.goto('/admin');
      const panel = page.getByTestId('admin-tracker-mal');
      await panel.scrollIntoViewIfNeeded();
      await expect(panel.getByTestId('admin-tracker-state-mal')).toHaveText('Off');
      const toggle = panel.getByTestId('admin-tracker-allow-mal').locator('button');
      if (server.acceptedConsentVersion !== server.currentConsentVersion) {
        // A fresh server: the text shows and the switch waits for the tick. (A CI retry finds the consent of the first attempt:
        // turning the tracker off keeps it, so the text is folded behind "What is sent?".)
        await expect(panel.getByTestId('tracker-consent-admin-mal')).toContainText('For personal servers only');
        await expect(toggle).toBeDisabled();
        await panel.getByTestId('admin-tracker-consent-mal').locator('input').check();
      }
      const allowed = page.waitForResponse((r) => r.request().method() === 'PUT' && r.url().endsWith('/api/v1/admin/trackers/mal'));
      await toggle.click();
      const allowedRes = await allowed;
      expect(allowedRes.status()).toBe(200);
      expect(allowedRes.request().postDataJSON()).toEqual({ enabled: true, acceptedConsentVersion: server.currentConsentVersion });
      await expect(panel.getByTestId('admin-tracker-state-mal')).toHaveText('Allowed - app not set up');
      await expect(panel.getByTestId('tracker-consent-admin-mal')).toHaveCount(0);

      // The redirect URL is proposed from this browser's address.
      const origin = new URL(page.url()).origin;
      await expect(panel.getByTestId('admin-tracker-redirect-mal')).toHaveValue(`${origin}/trackers/callback/mal`);
      await panel.getByTestId('admin-tracker-client-id-mal').fill('e2e-client-id');
      await panel.getByTestId('admin-tracker-client-secret-mal').fill('E2E-CLIENT-SECRET');
      const saved = page.waitForResponse((r) => r.request().method() === 'PUT' && r.url().endsWith('/api/v1/admin/trackers/mal'));
      await panel.getByTestId('admin-tracker-save-mal').click();
      const savedRes = await saved;
      expect(savedRes.status()).toBe(200);
      expect(await savedRes.text()).not.toContain('E2E-CLIENT-SECRET');
      await expect(panel.getByTestId('admin-tracker-state-mal')).toHaveText('Allowed');
      await expect(panel.getByTestId('admin-tracker-client-secret-mal')).toHaveValue('');

      const stored = await adminTracker(page, 'mal');
      expect(stored).toMatchObject({ enabled: true, clientId: 'e2e-client-id', clientSecretSet: true, redirectUri: `${origin}/trackers/callback/mal` });
      expect(JSON.stringify(stored)).not.toContain('E2E-CLIENT-SECRET');

      // After a reload the secret is only "saved", never shown.
      await page.reload();
      await panel.scrollIntoViewIfNeeded();
      await expect(panel).toContainText('A client secret is saved. It is never shown again.');
      await expect(panel.getByTestId('admin-tracker-client-secret-mal')).toHaveValue('');
      await expect(page.locator('body')).not.toContainText('E2E-CLIENT-SECRET');
      // The new controls are what a click reaches.
      // (Save is off now - nothing to save - and a disabled Material button takes no pointer events.)
      for (const id of ['admin-tracker-allow-mal', 'admin-tracker-client-id-mal', 'admin-tracker-copy-redirect-mal', 'admin-trackers-help']) {
        await page.getByTestId(id).scrollIntoViewIfNeeded();
        const box = (await page.getByTestId(id).boundingBox())!;
        const hit = await page.evaluate(([x, y, testId]) => {
          const el = document.elementFromPoint(x as number, y as number);
          return !!el?.closest(`[data-testid="${testId}"]`);
        }, [box.x + box.width / 2, box.y + box.height / 2, id] as const);
        expect(hit, `${id} is covered`).toBe(true);
      }
    } finally {
      await reset(page, 'mal');
    }
    expect(outside).toEqual([]);
  });

  test('a user accepts the MyAnimeList consent and Connect answers with the sign-in address (not followed)', async ({ page }) => {
    test.setTimeout(120_000);
    const outside = await guardOutside(page);
    await login(page);
    const origin = new URL(page.url()).origin;
    await allow(page, 'mal', { clientId: 'e2e-client-id', redirectUri: `${origin}/trackers/callback/mal` });
    try {
      await page.goto('/tracking');
      const panel = page.getByTestId('tracker-account-mal');
      await panel.scrollIntoViewIfNeeded();
      await panel.getByTestId('tracker-connect-open-mal').click();
      await expect(panel.getByTestId('tracker-consent-user-mal')).toContainText('MangaPixer never sees your password');
      const connect = panel.getByTestId('tracker-connect-mal');
      await expect(connect).toBeDisabled();
      await panel.getByTestId('tracker-consent-mal').locator('input').check();
      const { currentConsentVersion } = (await (await page.request.get('/api/v1/trackers')).json()).trackers
        .find((t: { slug: string }) => t.slug === 'mal');
      // The answer is read as it passes (the page leaves for the sign-in address right after, which drops the response body).
      let startStatus = 0;
      let startBody = '';
      let startRequest: unknown = null;
      await page.route(/\/api\/v1\/trackers\/mal\/connect\/start$/, async (route) => {
        startRequest = route.request().postDataJSON();
        const r = await route.fetch();
        startStatus = r.status();
        startBody = await r.text();
        await route.fulfill({ response: r, body: startBody });
      });
      await connect.click();
      await page.waitForURL(/^https:\/\/myanimelist\.net\//);
      expect(startRequest).toEqual({ acceptedConsentVersion: currentConsentVersion });
      expect(startStatus, startBody).toBe(200);
      const { authorizeUrl } = JSON.parse(startBody);
      const url = new URL(authorizeUrl);
      expect(`${url.origin}${url.pathname}`).toBe('https://myanimelist.net/v1/oauth2/authorize');
      expect(url.searchParams.get('response_type')).toBe('code');
      expect(url.searchParams.get('client_id')).toBe('e2e-client-id');
      expect(url.searchParams.get('redirect_uri')).toBe(`${origin}/trackers/callback/mal`);
      expect(url.searchParams.get('code_challenge_method')).toBe('plain');
      expect(url.searchParams.get('state')?.length).toBeGreaterThan(20);
      // The browser went to exactly that address - answered by the test's stub, so nothing reached MyAnimeList.
      expect(page.url()).toBe(authorizeUrl);
      await expect.poll(() => outside.filter((u) => !u.endsWith('/favicon.ico'))).toEqual([authorizeUrl]);
    } finally {
      await page.goto('/tracking');
      await reset(page, 'mal');
    }
  });

  test('the callback page strips a bogus code and state before any API request and says the sign-in is not valid', async ({ page }) => {
    test.setTimeout(120_000);
    const outside = await guardOutside(page);
    await login(page);
    const api: Request[] = [];
    page.on('request', (r) => { if (new URL(r.url()).pathname.startsWith('/api/')) api.push(r); });
    const finished = page.waitForResponse((r) => r.url().endsWith('/api/v1/trackers/mal/connect'));
    await page.goto('/trackers/callback/mal?code=BOGUS-CODE-e2e&state=BOGUS-STATE-e2e');
    const res = await finished;
    expect(res.status()).toBe(400);
    expect((await res.json()).error).toBe('invalid_state');
    expect(res.request().postDataJSON()).toEqual({ code: 'BOGUS-CODE-e2e', state: 'BOGUS-STATE-e2e' });

    await expect(page.getByTestId('tracker-callback-error')).toContainText('not valid any more');
    expect(page.url()).toMatch(/\/trackers\/callback\/mal$/);
    expect(await page.evaluate(() => location.search)).toBe('');
    // Not in the address of any API request, nor in the page address any API request was made from.
    expect(api.length).toBeGreaterThan(1);
    for (const r of api) {
      expect(r.url()).not.toContain('BOGUS');
      expect(r.headers()['referer'] ?? '').not.toContain('BOGUS');
    }
    await expect(page.locator('body')).not.toContainText('BOGUS');
    await page.getByTestId('tracker-callback-settings').click();
    await expect(page).toHaveURL(/\/tracking$/);
    expect(outside).toEqual([]);
  });

  test('the MangaUpdates form refuses an empty password without sending anything', async ({ page }) => {
    test.setTimeout(120_000);
    const outside = await guardOutside(page);
    await login(page);
    await allow(page, 'mangaupdates');
    try {
      const sent: string[] = [];
      page.on('request', (r) => { if (r.url().includes('/connect/password')) sent.push(r.url()); });
      await page.goto('/tracking');
      const panel = page.getByTestId('tracker-account-mangaupdates');
      await panel.scrollIntoViewIfNeeded();
      await panel.getByTestId('tracker-connect-open-mangaupdates').click();
      await expect(panel.getByTestId('tracker-consent-user-mangaupdates')).toContainText('username and password ONCE');
      await panel.getByTestId('tracker-consent-mangaupdates').locator('input').check();
      await panel.getByTestId('tracker-username-mangaupdates').fill('e2e-reader');
      await expect(panel.getByTestId('tracker-password-mangaupdates')).toHaveAttribute('type', 'password');
      await panel.getByTestId('tracker-connect-mangaupdates').click();
      await expect(panel.getByTestId('tracker-error-mangaupdates')).toHaveText('Enter your MangaUpdates password.');
      await page.waitForTimeout(500);
      expect(sent).toEqual([]);
    } finally {
      await reset(page, 'mangaupdates');
    }
    expect(outside).toEqual([]);
  });

  test('the selection menu stores "Sync to trackers" On and As default for a folder', async ({ page }) => {
    test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
    test.setTimeout(180_000);
    const outside = await guardOutside(page);
    await login(page);
    const [libraryId, folderId] = await volumeLibrary(page);
    // Connecting needs the tracker itself: the overview answers as connected; the switch is stored by the real server.
    await page.route(/\/api\/v1\/trackers$/, (r) => r.fulfill({ json: { trackers: [connectedMal()] } }));
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(`/libraries/${libraryId}/browse`);
    await page.locator('button.select-toggle').click();
    await page.locator('.node-wrap', { hasText: 'Stacked Saga' }).first().click();
    await page.getByTestId('tracker-sync-action').click();
    await expect(page.getByTestId('tracker-sync-note')).toContainText("A folder's setting covers everything inside it");
    await expect(page.getByTestId('tracker-sync-note')).toContainText('Private libraries and doujinshi are not synced');
    const on = page.waitForResponse((r) => r.request().method() === 'PUT' && r.url().endsWith('/api/v1/trackers/sync-overrides'));
    await page.getByTestId('tracker-sync-on').click();
    const onRes = await on;
    expect(onRes.status(), await onRes.text()).toBe(200);
    expect(onRes.request().postDataJSON()).toEqual({ nodeIds: [folderId], enabled: true });
    let stored = await (await page.request.get('/api/v1/trackers/sync-overrides')).json();
    expect(stored).toEqual([expect.objectContaining({ nodeId: folderId, enabled: true })]);

    await page.getByTestId('tracker-sync-action').click();
    const cleared = page.waitForResponse((r) => r.request().method() === 'PUT' && r.url().endsWith('/api/v1/trackers/sync-overrides'));
    await page.getByTestId('tracker-sync-default').click();
    expect((await cleared).status()).toBe(200);
    stored = await (await page.request.get('/api/v1/trackers/sync-overrides')).json();
    expect(stored).toEqual([]);
    expect(outside).toEqual([]);
  });

  test('the tracker cards, the Tracking page, the popover, the ids dialog and the callback page fit a phone and a tablet screen', async ({ page }) => {
    test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
    // Contract-shaped data with long names: MyAnimeList allowed with its registration, a connected account with entries in every
    // state, the Set form open, Select mode with its bar (1.43.2), the sync menu, the ids dialog. Nothing is changed on the server.
    test.setTimeout(240_000);
    const outside = await guardOutside(page);
    await login(page);
    const [libraryId, folderId] = await volumeLibrary(page);
    await mockTrackerData(page, folderId);
    const failures: string[] = [];
    const check = async (name: string, width: number) => {
      await page.waitForTimeout(400);
      if (SHOTS) await page.screenshot({ path: `${SHOTS}/layout-${width}-trackers-${name.replace(/\W+/g, '-')}.png`, fullPage: true });
      for (const p of await layoutProblems(page)) failures.push(`${width} px ${name}: ${p.kind}: ${p.what} - ${p.detail}`);
    };
    for (const size of SIZES) {
      await page.setViewportSize(size);
      await page.goto('/admin');
      await page.getByTestId('admin-trackers-card').scrollIntoViewIfNeeded();
      await expect(page.getByTestId('admin-tracker-account-mal-u1')).toBeVisible();
      await page.getByTestId('admin-tracker-disconnect-mal-u1').click();
      await check('admin trackers', size.width);

      // The Tracking page: both account cards, every chip, rows in every state with the from-tracker confirm, the "Set to"
      // warning form open, and the Disconnect confirm.
      await page.goto('/tracking');
      const list = page.getByTestId('tracking-list-mal');
      await expect(list.getByTestId('tracking-row-s1')).toBeVisible();
      await list.getByTestId('tracking-from-s6').click();
      await list.getByTestId('tracking-more-s5').click();
      await page.getByTestId('tracking-menu-set').click();
      await page.getByTestId('tracker-disconnect-mal').click();
      await page.getByTestId('tracker-connect-open-mangaupdates').click();
      await check('tracking page', size.width);
      // 1.43.2 Select mode: a tick per row and the bar at the bottom with every action, their counts and the reasons; then its confirm.
      await list.getByTestId('tracking-select-mal').click();
      for (const id of ['s1', 's3', 's6', 's8']) await list.getByTestId(`tracking-pick-box-${id}`).click();
      await expect(list.getByTestId('tracking-selected-count')).toHaveText('4 selected');
      await expect(list.getByTestId('tracking-batch-why')).toBeVisible();
      await check('tracking select mode', size.width);
      await list.getByTestId('tracking-batch-ToTracker').click();
      await expect(list.getByTestId('tracking-batch-confirm')).toBeVisible();
      await check('tracking select confirm', size.width);
      await list.getByTestId('tracking-batch-no').click();
      await list.getByTestId('tracking-select-done').click();
      await list.getByTestId('tracking-set-dates-s5').click();
      await expect(page.getByTestId('tracker-dates-dialog')).toBeVisible();
      // 1.43.2: "Set today" beside each date - the buttons stay on one row together (they may wrap under the field).
      await page.getByTestId('tracker-dates-start-today').click();
      await page.getByTestId('tracker-dates-finish-today').click();
      const [auto, today] = [await page.getByTestId('tracker-dates-finish-auto').boundingBox(), await page.getByTestId('tracker-dates-finish-today').boundingBox()];
      expect(Math.abs(auto!.y - today!.y), `${size.width} px: Automatic and Set today on one row`).toBeLessThan(4);
      await check('tracking dates dialog', size.width);
      await page.keyboard.press('Escape');

      // The series header's Tracking popover.
      await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
      await page.getByTestId('series-tracking-button').click();
      await expect(page.getByTestId('series-tracking-popover')).toBeVisible();
      await check('series tracking popover', size.width);
      await page.keyboard.press('Escape');

      await page.goto('/trackers/callback/mal?code=layout-code&state=layout-state');
      await expect(page.getByTestId('tracker-callback-error')).toBeVisible();
      await check('callback', size.width);

      await page.goto(`/libraries/${libraryId}/browse`);
      await page.locator('button.select-toggle').click();
      await page.locator('.node-wrap', { hasText: 'Stacked Saga' }).first().click();
      await page.getByTestId('tracker-sync-action').click();
      await expect(page.getByTestId('tracker-sync-note')).toBeVisible();
      await check('sync menu', size.width);
      await page.keyboard.press('Escape');

      await page.goto(`/series/${folderId}`);
      await page.getByTestId('series-admin-menu').first().click();
      await page.getByTestId('tracker-id-mal').click();
      await expect(page.getByTestId('tracker-ids-current')).toContainText('In use: 2');
      await page.getByTestId('tracker-ids-input').fill('https://myanimelist.net/manga/2/A_synthetic_series_title_that_is_long_enough_to_wrap');
      await check('ids dialog', size.width);
      await page.keyboard.press('Escape');
    }
    expect(failures, `tracker pages that do not fit the screen:\n${failures.join('\n')}`).toEqual([]);
    expect(outside).toEqual([]);
  });

  test('the Tracking list pages, filters and searches on the server (50 a page, chips with counts)', async ({ page }) => {
    // A connected MyAnimeList account needs MyAnimeList itself, so the overview and the list answer as contract-shaped data;
    // what is asserted is what the page asks this server for, and that nothing leaves the test.
    test.setTimeout(120_000);
    const outside = await guardOutside(page);
    await login(page);
    const all = Array.from({ length: 120 }, (_, i) => trackingRow(`r${String(i).padStart(3, '0')}`,
      i % 6 === 0 ? 'TrackerAhead' : 'InSync', { title: `Synthetic Saga ${String(i).padStart(3, '0')}` }));
    const asked: Record<string, string>[] = [];
    await page.route(/\/api\/v1\/trackers$/, (r) => r.fulfill({ json: { trackers: [connectedMal()] } }));
    await page.route(/\/api\/v1\/trackers\/attention$/, (r) => r.fulfill({ json: { total: 20, trackers: [{ tracker: 'mal', count: 20 }] } }));
    await page.route(/\/api\/v1\/trackers\/mal\/series(\?.*)?$/, (r) => {
      const url = new URL(r.request().url());
      const q = Object.fromEntries(url.searchParams.entries());
      asked.push(q);
      const pageNo = Number(q['page'] ?? '1');
      let rows = all;
      if (q['filter']) rows = rows.filter((x) => x.bucket === q['filter']);
      if (q['q']) rows = rows.filter((x) => x.title.toLowerCase().includes(q['q'].toLowerCase()));
      return r.fulfill({ json: {
        items: rows.slice((pageNo - 1) * 50, pageNo * 50), page: pageNo, pageSize: 50, totalCount: rows.length,
        counts: { all: 120, inSync: 100, mangaPixerAhead: 0, trackerAhead: 20, leftAsIs: 0, held: 0, noId: 0, notSynced: 0, problem: 0, unchecked: 0, attention: 20 },
      } });
    });
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/tracking');
    const list = page.getByTestId('tracking-list-mal');
    await expect(list.locator('[data-testid^="tracking-row-"]')).toHaveCount(50);
    await expect(list.getByTestId('tracking-shown-mal')).toContainText('Showing 1–50 of 120');
    await expect(list.getByTestId('tracking-chip-All')).toHaveText('All · 120');
    await expect(list.getByTestId('tracking-chip-TrackerAhead')).toHaveText('MyAnimeList ahead · 20');
    expect(asked[0]).toEqual({ page: '1' });
    // Load more is what a click reaches while there is more (a disabled Material button takes no pointer events).
    await list.getByTestId('tracking-load-more-mal').scrollIntoViewIfNeeded();
    const more = (await list.getByTestId('tracking-load-more-mal').boundingBox())!;
    expect(await page.evaluate(([x, y]) => !!document.elementFromPoint(x, y)?.closest('[data-testid="tracking-load-more-mal"]'),
      [more.x + more.width / 2, more.y + more.height / 2])).toBe(true);

    await list.getByTestId('tracking-load-more-mal').click();
    await expect(list.locator('[data-testid^="tracking-row-"]')).toHaveCount(100);
    expect(asked.at(-1)).toEqual({ page: '2' });

    await list.getByTestId('tracking-chip-TrackerAhead').click();
    await expect(list.locator('[data-testid^="tracking-row-"]')).toHaveCount(20);
    await expect(list.getByTestId('tracking-chip-TrackerAhead')).toHaveAttribute('aria-pressed', 'true');
    expect(asked.at(-1)).toEqual({ page: '1', filter: 'TrackerAhead' });
    await expect(list.getByTestId('tracking-load-more-mal')).toBeDisabled();

    await list.getByTestId('tracking-search').fill('Saga 006');
    await expect(list.locator('[data-testid^="tracking-row-"]')).toHaveCount(1);
    expect(asked.at(-1)).toEqual({ page: '1', filter: 'TrackerAhead', q: 'Saga 006' });

    // The new controls are what a click reaches.
    for (const id of ['tracking-tab-mal', 'tracking-chip-All', 'tracking-search', 'tracker-preview-mal', 'tracking-select-mal',
      'tracking-from-r006', 'tracking-to-r006', 'tracking-more-r006', 'tracking-set-dates-r006', 'tracker-disconnect-mal',
      'tracker-add-started-mal', 'tracking-help']) {
      await page.getByTestId(id).scrollIntoViewIfNeeded();
      const box = (await page.getByTestId(id).boundingBox())!;
      const hit = await page.evaluate(([x, y, testId]) => !!document.elementFromPoint(x as number, y as number)?.closest(`[data-testid="${testId}"]`),
        [box.x + box.width / 2, box.y + box.height / 2, id] as const);
      expect(hit, `${id} is covered`).toBe(true);
    }
    expect(outside).toEqual([]);
  });

  test('the two "Set progress" buttons: from MyAnimeList asks first and shows the counts; to MyAnimeList sends; the blocked one says why', async ({ page }) => {
    test.setTimeout(120_000);
    const outside = await guardOutside(page);
    await login(page);
    const ahead = trackingRow('s1', 'TrackerAhead', { allChapters: 45, chapters: 45, remoteChapters: 51, remoteVolumes: null, totalChapters: 51,
      fromTracker: { possible: true, block: 'None' }, toTracker: { possible: false, block: 'NotAhead' } });
    const behind = trackingRow('s2', 'MangaPixerAhead', { allChapters: 60, remoteChapters: 40,
      fromTracker: { possible: false, block: 'NotAhead' }, toTracker: { possible: true, block: 'None' } });
    let rows = [ahead, behind];
    const posts: string[] = [];
    await page.route(/\/api\/v1\/trackers$/, (r) => r.fulfill({ json: { trackers: [connectedMal()] } }));
    await page.route(/\/api\/v1\/trackers\/attention$/, (r) => r.fulfill({ json: { total: 1, trackers: [{ tracker: 'mal', count: 1 }] } }));
    await page.route(/\/api\/v1\/trackers\/mal\/series(\?.*)?$/, (r) => r.fulfill({ json: {
      items: rows, page: 1, pageSize: 50, totalCount: rows.length,
      counts: { all: 2, inSync: 0, mangaPixerAhead: 1, trackerAhead: 1, leftAsIs: 0, held: 0, noId: 0, notSynced: 0, problem: 0, unchecked: 0, attention: 1 },
    } }));
    await page.route(/\/api\/v1\/trackers\/mal\/entries\/s1\/from-tracker$/, (r) => {
      posts.push(`${r.request().method()} from-tracker s1`);
      const done = { ...ahead, bucket: 'InSync', state: 'Ok', allChapters: 51, chapters: 51, fromTracker: { possible: false, block: 'NotAhead' } };
      rows = [done, behind];
      return r.fulfill({ json: { marked: 6, alreadyRead: 45, remoteChapters: 51, remoteVolumes: null, entry: done } });
    });
    await page.route(/\/api\/v1\/trackers\/mal\/entries\/s2\/send$/, (r) => {
      posts.push(`${r.request().method()} send s2`);
      rows = [rows[0], { ...behind, bucket: 'InSync', state: 'Ok', remoteChapters: 60, toTracker: { possible: false, block: 'NotAhead' } }];
      return r.fulfill({ json: { seriesId: 's2', title: behind.title, nodeId: null, tracker: 'mal', remoteId: '2', state: 'Ok', dontSync: false,
        localChapters: 60, remoteChapters: 60, remoteMissing: false, updatedAt: '2026-10-13T10:00:00Z' } });
    });
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/tracking');
    const list = page.getByTestId('tracking-list-mal');
    const from = list.getByTestId('tracking-from-s1');
    const toBlocked = list.getByTestId('tracking-to-s1');
    await expect(from).toHaveText(/Set progress from MyAnimeList to MangaPixer/);
    await expect(toBlocked).toHaveAttribute('aria-disabled', 'true');
    await toBlocked.hover();
    await expect(page.locator('.mat-mdc-tooltip')).toContainText('MangaPixer is not ahead of MyAnimeList - nothing to send');

    await from.click();
    await expect(list.getByTestId('tracking-from-confirm-s1')).toContainText('Mark chapters 1-51 read in MangaPixer?');
    expect(posts).toEqual([]);
    await list.getByTestId('tracking-from-yes-s1').click();
    await expect(list.getByTestId('tracking-notice-mal')).toContainText('marked 6 files read in MangaPixer (45 files already read)');
    await expect(list.getByTestId('tracking-state-s1')).toHaveText('In sync');

    await list.getByTestId('tracking-to-s2').click();
    await expect(list.getByTestId('tracking-notice-mal')).toContainText('up to date');
    await expect(list.getByTestId('tracking-state-s2')).toHaveText('In sync');
    expect(posts).toEqual(['POST from-tracker s1', 'POST send s2']);
    expect(outside).toEqual([]);
  });

  test('Select mode (1.43.2): select all in a chip, apply once with the counts, the server gets it in chunks, one summary, the list again', async ({ page }) => {
    // Contract-shaped answers (a connected MyAnimeList account needs MyAnimeList itself); asserted is what the page asks THIS server
    // for, and that nothing leaves the test.
    test.setTimeout(120_000);
    const outside = await guardOutside(page);
    await login(page);
    const ahead = Array.from({ length: 60 }, (_, i) => trackingRow(`t${String(i).padStart(2, '0')}`, 'TrackerAhead', {
      title: `Synthetic Saga ${String(i).padStart(2, '0')}`, remoteChapters: 200, fromTracker: { possible: true, block: 'None' } }));
    const others = Array.from({ length: 30 }, (_, i) => trackingRow(`o${String(i).padStart(2, '0')}`, 'InSync', { title: `Other Story ${i}` }));
    let applied = false;
    const counts = () => ({ all: 90, inSync: applied ? 90 : 30, mangaPixerAhead: 0, trackerAhead: applied ? 0 : 60, leftAsIs: 0, held: 0, noId: 0,
      notSynced: 0, problem: 0, unchecked: 0, attention: applied ? 0 : 60 });
    const asked: string[] = [];
    const batches: { action: string; seriesIds: string[] }[] = [];
    await page.route(/\/api\/v1\/trackers$/, (r) => r.fulfill({ json: { trackers: [connectedMal()] } }));
    await page.route(/\/api\/v1\/trackers\/attention$/, (r) => r.fulfill({ json: { total: applied ? 0 : 60, trackers: [{ tracker: 'mal', count: applied ? 0 : 60 }] } }));
    await page.route(/\/api\/v1\/trackers\/mal\/series(\?.*)?$/, (r) => {
      const q = Object.fromEntries(new URL(r.request().url()).searchParams.entries());
      asked.push(`series ${q['filter'] ?? 'All'}`);
      const pageNo = Number(q['page'] ?? '1');
      const rows = q['filter'] === 'TrackerAhead' ? (applied ? [] : ahead) : [...ahead, ...others];
      return r.fulfill({ json: { items: rows.slice((pageNo - 1) * 50, pageNo * 50), page: pageNo, pageSize: 50, totalCount: rows.length, counts: counts() } });
    });
    await page.route(/\/api\/v1\/trackers\/mal\/series\/ids(\?.*)?$/, (r) => {
      const q = Object.fromEntries(new URL(r.request().url()).searchParams.entries());
      asked.push(`ids ${q['filter'] ?? 'All'}`);
      return r.fulfill({ json: { items: ahead.map((x) => ({ seriesId: x.seriesId, fromTracker: x.fromTracker, toTracker: x.toTracker, canSync: true, dontSync: false })),
        totalCount: 60, truncated: false } });
    });
    await page.route(/\/api\/v1\/trackers\/mal\/entries\/batch$/, async (r) => {
      const body = r.request().postDataJSON() as { action: string; seriesIds: string[] };
      batches.push(body);
      await new Promise((resolve) => setTimeout(resolve, 150));
      if (batches.reduce((n, b) => n + b.seriesIds.length, 0) >= 60) applied = true;
      const left = body.seriesIds.filter((id) => id === 't07').length;
      return r.fulfill({ json: { done: body.seriesIds.length - left, skipped: left, failed: 0, capReached: false, notReached: 0,
        marked: (body.seriesIds.length - left) * 8, alreadyRead: 0, stoppedCode: null, skips: left ? [{ reason: 'LeftAsIs', count: left }] : [], rows: [] } });
    });

    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/tracking');
    const list = page.getByTestId('tracking-list-mal');
    await list.getByTestId('tracking-chip-TrackerAhead').click();
    await expect(list.locator('[data-testid^="tracking-row-"]')).toHaveCount(50);
    await list.getByTestId('tracking-select-mal').click();
    await list.getByTestId('tracking-select-all').click();
    await expect(list.getByTestId('tracking-selected-count')).toHaveText('60 selected');
    expect(asked).toContain('ids TrackerAhead');
    // The chip's natural action comes first, with how many of the selected it applies to.
    const first = list.locator('.sel-act').first();
    await expect(first).toHaveAttribute('data-testid', 'tracking-batch-FromTracker');
    await expect(first).toHaveAttribute('aria-label', 'Set progress from MyAnimeList to MangaPixer (60)');
    // The bar sits on the screen while the list is long, and its controls are what a click reaches.
    for (const id of ['tracking-batch-FromTracker', 'tracking-select-clear', 'tracking-select-done', 'tracking-pick-t00']) {
      const box = (await list.getByTestId(id).boundingBox())!;
      expect(box.y + box.height, `${id} on the screen`).toBeLessThanOrEqual(900);
      const hit = await page.evaluate(([x, y, testId]) => !!document.elementFromPoint(x as number, y as number)?.closest(`[data-testid="${testId}"]`),
        [box.x + box.width / 2, box.y + box.height / 2, id] as const);
      expect(hit, `${id} is covered`).toBe(true);
    }

    await first.click();
    await expect(list.getByTestId('tracking-batch-confirm')).toContainText('Mark read in MangaPixer what MyAnimeList counts, for 60 series? Nothing is unmarked.');
    expect(batches).toEqual([]);
    await list.getByTestId('tracking-batch-yes').click();
    await expect(list.getByTestId('tracking-batch-progress')).toBeVisible();
    await expect(list.getByTestId('tracking-notice-mal')).toHaveText(
      'Set progress from MyAnimeList for 60 series: 472 files marked, 0 already read, 1 skipped (left as is).', { timeout: 30_000 });
    expect(batches.map((b) => b.seriesIds.length)).toEqual([10, 10, 10, 10, 10, 10]);
    expect(batches.every((b) => b.action === 'FromTracker')).toBe(true);
    expect(batches.flatMap((b) => b.seriesIds)).toEqual(ahead.map((x) => x.seriesId));
    // The list again: the chip is empty now, its count follows, and the selection is cleared.
    await expect(list.getByTestId('tracking-chip-TrackerAhead')).toHaveText('MyAnimeList ahead · 0');
    await expect(list.getByTestId('tracking-selected-count')).toHaveText('0 selected');
    expect(outside).toEqual([]);
  });

  test('the tracker\'s numbers open the series on MyAnimeList in a new tab, without a referrer (1.43.2)', async ({ page }) => {
    test.setTimeout(120_000);
    const outside = await guardOutside(page);
    const referers: (string | undefined)[] = [];
    await page.context().route(/^https:\/\/myanimelist\.net\//, (route) => {
      referers.push(route.request().headers()['referer']);
      outside.push(route.request().url());
      return route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><title>stub</title><p>stub</p>' });
    });
    await login(page);
    const rows = [trackingRow('s1', 'InSync', { remoteId: '2' }), trackingRow('s2', 'NoId', { remoteId: null })];
    await page.route(/\/api\/v1\/trackers$/, (r) => r.fulfill({ json: { trackers: [connectedMal()] } }));
    await page.route(/\/api\/v1\/trackers\/attention$/, (r) => r.fulfill({ json: { total: 0, trackers: [] } }));
    await page.route(/\/api\/v1\/trackers\/mal\/series(\?.*)?$/, (r) => r.fulfill({ json: {
      items: rows, page: 1, pageSize: 50, totalCount: 2,
      counts: { all: 2, inSync: 1, mangaPixerAhead: 0, trackerAhead: 0, leftAsIs: 0, held: 0, noId: 1, notSynced: 0, problem: 0, unchecked: 0, attention: 0 },
    } }));
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/tracking');
    const list = page.getByTestId('tracking-list-mal');
    const link = list.getByTestId('tracking-remote-link-s1');
    await expect(link).toHaveAttribute('href', 'https://myanimelist.net/manga/2');
    await expect(link).toHaveAttribute('target', '_blank');
    await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    await expect(link).toHaveAttribute('aria-label', 'Open on MyAnimeList: 120 ch · 12 vol');
    await expect(list.getByTestId('tracking-remote-link-s2')).toHaveCount(0);
    await list.getByTestId('tracking-more-s1').click();
    await expect(page.getByTestId('tracking-menu-open-tracker')).toHaveAttribute('href', 'https://myanimelist.net/manga/2');
    await page.keyboard.press('Escape');

    // A click opens a new tab; the request carries no referrer (this server's address never reaches the tracker) and is answered here.
    const [popup] = await Promise.all([page.waitForEvent('popup'), link.click()]);
    await popup.waitForLoadState();
    expect(popup.url()).toBe('https://myanimelist.net/manga/2');
    expect(await popup.evaluate(() => document.referrer)).toBe('');
    expect(await popup.evaluate(() => window.opener)).toBeNull();
    await popup.close();
    expect(referers).toEqual([undefined]);
    expect(outside).toEqual(['https://myanimelist.net/manga/2']);
  });

  test('the card mark: a gold border around the covers the tracker counts as read, none on the others; the menu leads to Tracking', async ({ page }) => {
    test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
    test.setTimeout(180_000);
    const outside = await guardOutside(page);
    await login(page);
    const [libraryId, folderId] = await volumeLibrary(page);
    await page.route(/\/api\/v1\/trackers$/, (r) => r.fulfill({ json: { trackers: [connectedMal()] } }));
    await page.route(/\/api\/v1\/trackers\/attention$/, (r) => r.fulfill({ json: { total: 2, trackers: [{ tracker: 'mal', count: 2 }] } }));
    await page.route(/\/api\/v1\/trackers\/nodes\/[^/]+\/summary$/, (r) => r.fulfill({ json: { seriesId: null, rows: [] } }));
    // The server sets `trackerRead` only for a series the viewer tracks on a connected tracker; here the browse answer gets it for
    // the first card only, as the server would for one volume read on MyAnimeList.
    let marked = '';
    await page.route(new RegExp(`/api/v1/libraries/${libraryId}/browse\\?.*parentId=${folderId}`), async (r) => {
      const res = await r.fetch();
      const body = await res.json();
      const items = body.items as { id: string; kind: string; trackerRead?: string[] }[];
      const first = (items as { id: string; kind: string; trackerRead?: string[]; volumeStack?: { missing?: boolean } }[])
        .find((n) => n.kind === 'Archive' || (n.kind === 'VolumeStack' && !n.volumeStack?.missing));
      if (first) { first.trackerRead = ['mal']; marked = first.id; }
      await r.fulfill({ response: res, json: body });
    });
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    const marks = page.getByTestId('tracker-read-mark');
    await expect(marks).toHaveCount(1);
    expect(marked).not.toBe('');
    // One card carries it; every other card of the folder has none.
    expect(await page.locator('.nodes .node-wrap').count()).toBeGreaterThan(1);
    await expect(page.locator('.nodes .node-wrap').filter({ has: page.getByTestId('tracker-read-mark') })).toHaveCount(1);
    const mark = marks.first();
    await expect(mark).toHaveAttribute('aria-label', 'Read on MyAnimeList');
    // The owner's V2 (2026-10-15): a 2 px gold ring inside the whole cover with the 1 px contrast edge inside it - a border that
    // lets every click through to the card (it covers the cover box, so it must never take the pointer).
    const look = await mark.evaluate((el) => {
      const s = getComputedStyle(el);
      // The host is `display: contents`: measure against the cover box itself.
      const coverEl = el.closest('.cover')!;
      const cover = coverEl.getBoundingClientRect();
      const box = el.getBoundingClientRect();
      const probe = document.createElement('span');
      probe.style.color = 'var(--mp-warn-strong)';
      document.body.appendChild(probe);
      const gold = getComputedStyle(probe).color;
      probe.remove();
      const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
      const same = (a: number, b: number) => Math.abs(a - b) < 1;
      return { shadow: s.boxShadow, pointer: s.pointerEvents, gold, radius: s.borderTopLeftRadius,
        coverRadius: getComputedStyle(coverEl).borderTopLeftRadius,
        fillsCover: same(box.top, cover.top) && same(box.left, cover.left) && same(box.width, cover.width) && same(box.height, cover.height),
        hit: hit === el, hitInCover: !!hit && coverEl.contains(hit) };
    });
    expect(look).toMatchObject({ pointer: 'none', fillsCover: true, hit: false, hitInCover: true });
    expect(look.radius).toBe(look.coverRadius);
    expect(look.shadow).toBe(`${look.gold} 0px 0px 0px 2px inset, rgba(0, 0, 0, 0.6) 0px 0px 0px 3px inset`);

    // The account menu: Tracking between Settings and Administration, with the count; it opens the page.
    await page.getByRole('button', { name: /^Account menu/ }).click();
    const entry = page.getByTestId('nav-tracking');
    await expect(entry).toBeVisible();
    await expect(page.getByTestId('nav-tracking-count')).toHaveText('2');
    const labels = await page.locator('.mat-mdc-menu-panel [mat-menu-item]').allTextContents();
    const at = labels.findIndex((l) => l.includes('Tracking'));
    expect(labels[at - 1]).toContain('Settings');
    expect(labels[at + 1]).toContain('Administration');
    await entry.click();
    await expect(page).toHaveURL(/\/tracking$/);
    expect(outside).toEqual([]);
  });

  test('the series header\'s Tracking popover opens the series\' row on the Tracking page', async ({ page }) => {
    test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
    test.setTimeout(180_000);
    const outside = await guardOutside(page);
    await login(page);
    const [libraryId, folderId] = await volumeLibrary(page);
    const row = trackingRow('s7', 'LeftAsIs', { nodeId: folderId, remoteStatus: 'Completed', allChapters: 45, remoteChapters: 51,
      remoteVolumes: 10, toTracker: { possible: false, block: 'LeftAsIs' }, fromTracker: { possible: false, block: 'LeftAsIs' } });
    const asked: string[] = [];
    await page.route(/\/api\/v1\/trackers$/, (r) => r.fulfill({ json: { trackers: [connectedMal()] } }));
    await page.route(/\/api\/v1\/trackers\/attention$/, (r) => r.fulfill({ json: { total: 0, trackers: [] } }));
    await page.route(new RegExp(`/api/v1/trackers/nodes/${folderId}/summary$`), (r) => r.fulfill({ json: { seriesId: 's7', rows: [row] } }));
    await page.route(/\/api\/v1\/trackers\/mal\/series(\?.*)?$/, (r) => {
      asked.push(new URL(r.request().url()).searchParams.get('q') ?? '');
      return r.fulfill({ json: { items: [row], page: 1, pageSize: 50, totalCount: 1,
        counts: { all: 1, inSync: 0, mangaPixerAhead: 0, trackerAhead: 0, leftAsIs: 1, held: 0, noId: 0, notSynced: 0, problem: 0, unchecked: 0, attention: 0 } } });
    });
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    const button = page.getByTestId('series-tracking-button');
    await expect(button).toBeVisible();
    const box = (await button.boundingBox())!;
    expect(await page.evaluate(([x, y]) => !!document.elementFromPoint(x, y)?.closest('[data-testid="series-tracking-button"]'),
      [box.x + box.width / 2, box.y + box.height / 2])).toBe(true);
    await button.click();
    const pop = page.getByTestId('series-tracking-popover');
    await expect(pop.getByTestId('series-tracking-state-mal')).toHaveText('Left as is · Completed on MyAnimeList');
    await expect(pop.getByTestId('series-tracking-here-mal')).toContainText('45 of 300 ch');
    await expect(pop.getByTestId('series-tracking-remote-mal')).toContainText('51 ch · 10 vol · Completed');
    await pop.getByTestId('series-tracking-open-mal').click();
    await expect(page).toHaveURL(/\/tracking\?tracker=mal&series=s7$/);
    await expect(page.getByTestId('tracking-row-s7')).toHaveClass(/focused/);
    // The popover never follows to another page (owner review on an iPad: it stayed open over the Tracking page), and going back
    // to the series finds its button closed.
    await expect(page.getByTestId('series-tracking-popover')).toHaveCount(0);
    await page.goBack();
    await expect(page.getByTestId('series-tracking-button')).toBeVisible();
    await expect(page.getByTestId('series-tracking-button')).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('series-tracking-popover')).toHaveCount(0);
    // A click-less navigation away while it is open (an iPad back / forward swipe - no outside click to close it first): the browse
    // page is kept and detached, so the popover must be closed before it goes (UX review UX0).
    await page.getByTestId('series-tracking-button').click();
    await expect(page.getByTestId('series-tracking-popover')).toBeVisible();
    await page.goForward();
    await expect(page).toHaveURL(/\/tracking\?tracker=mal&series=s7$/);
    await expect(page.getByTestId('tracking-row-s7')).toBeVisible();
    await expect(page.getByTestId('series-tracking-popover')).toHaveCount(0);
    await page.goBack();
    await expect(page.getByTestId('series-tracking-button')).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('series-tracking-popover')).toHaveCount(0);
    // The title travels in the navigation state and seeds the search; it is never in the address.
    expect(asked.at(-1)).toBe('Synthetic Saga s7');
    expect(page.url()).not.toContain('Synthetic');
    expect(outside).toEqual([]);
  });
});

/**
 * The UX review of 1.43.0 (lane UX): what a touch screen, a keyboard and an address change do with the Tracking page and the series
 * header's popover. Contract-shaped answers, nothing leaves the test instance.
 */
test.describe('tracking UX review fixes (1.43.0)', () => {
  /** The Tracking list for MyAnimeList (and MangaUpdates when `mu` rows are given): one row per bucket that matters here. */
  async function mockList(page: Page, mal: Record<string, unknown>[], mu: Record<string, unknown>[] | null = null): Promise<void> {
    const muSummary = { slug: 'mangaupdates', name: 'MangaUpdates', allowed: true, networkDisabledByConfig: false, connectKind: 'Password',
      currentConsentVersion: 1, supportsPrivateEntries: false,
      account: mu ? { ...connectedMal().account, tracker: 'mangaupdates', displayName: 'reader' } : null };
    await page.route(/\/api\/v1\/trackers$/, (r) => r.fulfill({ json: { trackers: [connectedMal(), muSummary] } }));
    await page.route(/\/api\/v1\/trackers\/attention$/, (r) => r.fulfill({ json: { total: 0, trackers: [] } }));
    const answer = (rows: Record<string, unknown>[]) => ({ items: rows, page: 1, pageSize: 50, totalCount: rows.length,
      counts: { all: rows.length, inSync: rows.length, mangaPixerAhead: 0, trackerAhead: 0, leftAsIs: 0, held: 0, noId: 0, notSynced: 0, problem: 0,
        unchecked: 0, attention: 0 } });
    await page.route(/\/api\/v1\/trackers\/mal\/series(\?.*)?$/, (r) => r.fulfill({ json: answer(mal) }));
    if (mu) await page.route(/\/api\/v1\/trackers\/mangaupdates\/series(\?.*)?$/, (r) => r.fulfill({ json: answer(mu) }));
  }

  const hits = (page: Page, box: { x: number; y: number; width: number; height: number }, testId: string) =>
    page.evaluate(([x, y, t]) => !!document.elementFromPoint(x as number, y as number)?.closest(`[data-testid="${t}"]`),
      [box.x + box.width / 2, box.y + box.height / 2, testId] as const);

  test.describe('on a touch screen', () => {
    test.use({ hasTouch: true, isMobile: true, viewport: { width: 390, height: 844 } });

    test('UX1: a disabled "Set progress" says why in the row, and no tooltip opens or stays on a touch screen', async ({ page }) => {
      test.setTimeout(120_000);
      const outside = await guardOutside(page);
      await login(page);
      await mockList(page, [trackingRow('s5', 'InSync'), trackingRow('s1', 'TrackerAhead', { remoteChapters: 300, fromTracker: { possible: true, block: 'None' } })]);
      await page.goto('/tracking');
      const list = page.getByTestId('tracking-list-mal');
      const blocked = list.getByTestId('tracking-to-s5');
      await expect(blocked).toHaveAttribute('aria-disabled', 'true');
      // The reason is written in the row - no hover or long press needed - and it is what a tap there reaches.
      const why = list.getByTestId('tracking-why-s5');
      await expect(why).toBeVisible();
      await expect(why).toHaveText('Neither side is ahead - nothing to bring in or send');
      await expect(list.getByTestId('tracking-why-s1')).toHaveText('MangaPixer is not ahead of MyAnimeList - nothing to send');
      await why.scrollIntoViewIfNeeded();
      expect(await hits(page, (await why.boundingBox())!, 'tracking-why-s5')).toBe(true);

      // A tap, the compatibility mouseenter an iPad sends with a tap, and a long press: no tooltip, now or later.
      await blocked.tap({ force: true });
      await blocked.dispatchEvent('mouseenter');
      const box = (await blocked.boundingBox())!;
      const cdp = await page.context().newCDPSession(page);
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: box.x + box.width / 2, y: box.y + box.height / 2 }] });
      await page.waitForTimeout(900);
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
      await cdp.detach();
      await page.waitForTimeout(600);
      await expect(page.locator('.mat-mdc-tooltip')).toHaveCount(0);
      expect(outside).toEqual([]);
    });

    test('UX10: a tap beside the open popover only closes it - the card under the finger does not open; the button still toggles', async ({ page }) => {
      test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
      test.setTimeout(180_000);
      const outside = await guardOutside(page);
      await login(page);
      const [libraryId, folderId] = await volumeLibrary(page);
      await mockList(page, []);
      const row = trackingRow('s7', 'InSync', { nodeId: folderId });
      await page.route(new RegExp(`/api/v1/trackers/nodes/${folderId}/summary$`), (r) => r.fulfill({ json: { seriesId: 's7', rows: [row] } }));
      await page.setViewportSize({ width: 820, height: 1180 });
      await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
      const button = page.getByTestId('series-tracking-button');
      await button.tap();
      const pop = page.getByTestId('series-tracking-popover');
      await expect(pop).toBeVisible();
      const popBox = (await pop.boundingBox())!;
      // A card whose centre is beside the popover.
      let target: { x: number; y: number } | null = null;
      for (const card of await page.locator('.nodes .node-wrap').all()) {
        const b = await card.boundingBox();
        if (!b) continue;
        const x = b.x + b.width / 2; const y = b.y + b.height / 2;
        const inPop = x >= popBox.x && x <= popBox.x + popBox.width && y >= popBox.y && y <= popBox.y + popBox.height;
        if (!inPop && y > 0 && y < 1170) { target = { x, y }; break; }
      }
      expect(target, 'a card beside the popover').not.toBeNull();
      const before = page.url();
      await page.touchscreen.tap(target!.x, target!.y);
      await expect(pop).toHaveCount(0);
      await page.waitForTimeout(500);
      expect(page.url()).toBe(before);
      await expect(button).toBeFocused();

      // The button opens it, and a tap on the button while open closes it (the backdrop above the button takes that tap).
      await button.tap();
      await expect(pop).toBeVisible();
      const bb = (await button.boundingBox())!;
      await page.touchscreen.tap(bb.x + bb.width / 2, bb.y + bb.height / 2);
      await expect(pop).toHaveCount(0);
      await button.tap();
      await expect(pop).toBeVisible();
      expect(outside).toEqual([]);
    });
  });

  test('UX5: the popover takes the focus when it opens, keeps Tab inside, and Escape gives it back to the button', async ({ page }) => {
    test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
    test.setTimeout(180_000);
    const outside = await guardOutside(page);
    await login(page);
    const [libraryId, folderId] = await volumeLibrary(page);
    await mockList(page, []);
    const row = trackingRow('s7', 'InSync', { nodeId: folderId });
    await page.route(new RegExp(`/api/v1/trackers/nodes/${folderId}/summary$`), (r) => r.fulfill({ json: { seriesId: 's7', rows: [row] } }));
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(`/libraries/${libraryId}/browse/${folderId}`);
    const button = page.getByTestId('series-tracking-button');
    await button.focus();
    await page.keyboard.press('Enter');
    const pop = page.getByTestId('series-tracking-popover');
    await expect(pop).toBeVisible();
    await expect(pop).toHaveAttribute('role', 'dialog');
    await expect(pop).toHaveAccessibleName('Tracking for this series');
    await expect(pop.getByTestId('series-tracking-open-mal')).toBeFocused();
    const inside = () => page.evaluate(() => !!document.activeElement?.closest('[data-testid="series-tracking-popover"]'));
    for (const key of ['Tab', 'Tab', 'Shift+Tab', 'Shift+Tab', 'Shift+Tab']) {
      await page.keyboard.press(key);
      expect(await inside(), `focus inside after ${key}`).toBe(true);
    }
    await page.keyboard.press('Escape');
    await expect(pop).toHaveCount(0);
    await expect(button).toBeFocused();
    expect(outside).toEqual([]);
  });

  test('UX8: another ?tracker= / ?series= while the Tracking page is shown switches the tab and highlights that series', async ({ page }) => {
    test.setTimeout(120_000);
    const outside = await guardOutside(page);
    await login(page);
    await mockList(page, [trackingRow('s1', 'InSync')], [trackingRow('m1', 'InSync', { tracker: 'mangaupdates' }), trackingRow('m2', 'InSync', { tracker: 'mangaupdates' })]);
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/tracking?tracker=mal');
    await expect(page.getByTestId('tracking-row-s1')).toBeVisible();
    await expect(page.getByTestId('tracking-tab-mal')).toHaveAttribute('aria-selected', 'true');
    // An in-app change of the address (here: back to an earlier entry of this same page) - the route is reused, not re-created.
    await page.evaluate(() => {
      history.pushState(null, '', '/tracking?tracker=mangaupdates&series=m2');
      history.pushState(null, '', '/tracking?tracker=mal');
      history.back();
    });
    await expect(page).toHaveURL(/\/tracking\?tracker=mangaupdates&series=m2$/);
    await expect(page.getByTestId('tracking-tab-mangaupdates')).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('tracking-row-m2')).toHaveClass(/focused/);
    await expect(page.getByTestId('tracking-row-m1')).not.toHaveClass(/focused/);
    expect(outside).toEqual([]);
  });
});

function connectedMal(extra: Record<string, unknown> = {}) {
  return {
    slug: 'mal', name: 'MyAnimeList', allowed: true, networkDisabledByConfig: false, connectKind: 'OAuthPkce', currentConsentVersion: 1,
    supportsPrivateEntries: false,
    account: {
      tracker: 'mal', state: 'Ok', displayName: 'a-reader-with-a-rather-long-myanimelist-name', connectedAt: '2026-10-13T10:00:00Z',
      lastSuccessAt: '2026-10-13T11:00:00Z', consentRenewalNeeded: false, addStartedSeries: true, privateEntries: true,
      requestsToday: 12, dailyRequestCap: 2000,
    },
    ...extra,
  };
}

/** The synthetic volume library (registered here when missing, as phone-layout.spec.ts does): [libraryId, folderId]. */
async function volumeLibrary(page: Page): Promise<[string, string]> {
  const headers = await csrf(page.request);
  const libs: { id: string; name: string }[] = await (await page.request.get('/api/v1/libraries')).json();
  let lib = libs.find((l) => l.name === VOLUMES_LIBRARY);
  if (!lib) {
    const created = await page.request.post('/api/v1/admin/libraries', { headers, data: { displayName: VOLUMES_LIBRARY, rootPath: VOLUMES_ROOT } });
    expect(created.ok(), await created.text()).toBeTruthy();
    lib = await created.json();
    await page.request.post(`/api/v1/admin/libraries/${lib!.id}/scan`, { headers });
  }
  let folder = '';
  await expect.poll(async () => {
    const root = await (await page.request.get(`/api/v1/libraries/${lib!.id}/browse?pageSize=50`)).json();
    folder = ((root.items as { id: string; displayName: string }[]).find((n) => n.displayName === 'Stacked Saga')?.id) ?? '';
    return folder;
  }, { timeout: 120_000, intervals: [1000, 2000, 5000] }).not.toBe('');
  return [lib!.id, folder];
}

/** Contract-shaped tracker answers for the layout check (lane E's and lane G's DTOs), with long names. */
async function mockTrackerData(page: Page, folderId: string): Promise<void> {
  const long = 'A synthetic series title that is long enough to wrap on a phone screen';
  const row = (seriesId: string, bucket: string, extra: Record<string, unknown> = {}) => trackingRow(seriesId, bucket, {
    title: `${long} ${seriesId}`, nodeId: folderId, ...extra,
  });
  const adminMal = {
    slug: 'mal', name: 'MyAnimeList', enabled: true, acceptedConsentVersion: 1, currentConsentVersion: 1, consentAt: '2026-10-13T10:00:00Z',
    consentRenewalNeeded: false, networkDisabledByConfig: false, connectKind: 'OAuthPkce', needsAppRegistration: true,
    clientId: 'a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5', clientSecretSet: true,
    redirectUri: 'https://a-rather-long-host-name.example.test:8443/trackers/callback/mal', connectedUsers: 2,
  };
  const adminMu = { ...adminMal, slug: 'mangaupdates', name: 'MangaUpdates', enabled: false, acceptedConsentVersion: null, consentAt: null,
    connectKind: 'Password', needsAppRegistration: false, clientId: null, clientSecretSet: false, redirectUri: null, connectedUsers: 0 };
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route(/\/api\/v1\/admin\/trackers$/, (r) => r.fulfill(json([adminMal, adminMu])));
  await page.route(/\/api\/v1\/admin\/trackers\/accounts$/, (r) => r.fulfill(json([
    { userId: 'u1', username: 'a-user-with-a-rather-long-user-name', tracker: 'mal', state: 'Ok', connectedAt: '2026-10-13T10:00:00Z', lastSuccessAt: '2026-10-13T11:00:00Z' },
    { userId: 'u2', username: 'another-user', tracker: 'mal', state: 'NeedsReconnect', connectedAt: '2026-10-12T10:00:00Z' },
  ])));
  await page.route(/\/api\/v1\/trackers$/, (r) => r.fulfill(json({ trackers: [connectedMal(), {
    slug: 'mangaupdates', name: 'MangaUpdates', allowed: true, networkDisabledByConfig: false, connectKind: 'Password', currentConsentVersion: 1,
    supportsPrivateEntries: false, account: null,
  }] })));
  const rows = [
    row('s1', 'Held', { heldChapters: 240, heldVolumes: 24, toTracker: { possible: true, block: 'None' } }),
    row('s2', 'Problem', { errorCode: 'unavailable' }),
    row('s3', 'LeftAsIs', { remoteStatus: 'OnHold', toTracker: { possible: false, block: 'LeftAsIs' }, fromTracker: { possible: false, block: 'LeftAsIs' } }),
    row('s4', 'NoId', { remoteId: null, toTracker: { possible: false, block: 'NoId' }, fromTracker: { possible: false, block: 'NoId' } }),
    row('s5', 'InSync'),
    row('s6', 'TrackerAhead', { remoteChapters: 300, fromTracker: { possible: true, block: 'None' } }),
    row('s7', 'MangaPixerAhead', { remoteChapters: 100, toTracker: { possible: true, block: 'None' } }),
    row('s8', 'NotSynced', { dontSync: true, toTracker: { possible: false, block: 'DontSync' }, fromTracker: { possible: false, block: 'DontSync' } }),
    row('s9', 'Unchecked', { remoteChapters: null, remoteVolumes: null, remoteStatus: null, lastSyncAt: null, toTracker: { possible: false, block: 'NotChecked' }, fromTracker: { possible: false, block: 'NotChecked' } }),
  ];
  await page.route(/\/api\/v1\/trackers\/mal\/series(\?.*)?$/, (r) => r.fulfill(json({
    items: rows, page: 1, pageSize: 50, totalCount: rows.length,
    counts: { all: 9, inSync: 1, mangaPixerAhead: 1, trackerAhead: 1, leftAsIs: 1, held: 1, noId: 1, notSynced: 1, problem: 1, unchecked: 1, attention: 2 },
  })));
  await page.route(/\/api\/v1\/trackers\/attention$/, (r) => r.fulfill(json({ total: 2, trackers: [{ tracker: 'mal', count: 2 }] })));
  await page.route(/\/api\/v1\/trackers\/nodes\/[^/]+\/summary$/, (r) => r.fulfill(json({ seriesId: 's6', rows: [rows[5]] })));
  await page.route(/\/api\/v1\/admin\/trackers\/nodes\/[^/]+\/ids$/, (r) => r.fulfill(json({
    seriesId: 'r1', ids: [
      { tracker: 'mal', remoteId: '2', source: 'MangaDex', overrideId: null },
      { tracker: 'mangaupdates', remoteId: '51239621230', source: 'Link', overrideId: null },
    ],
  })));
  // The series page's Admin menu shows the tracker ids only for a node with a web link: the series info answers as linked.
  await page.route(new RegExp(`/api/v1/nodes/${folderId}/series-info`), async (r) => {
    const res = await r.fetch();
    const body = await res.json();
    body.web ??= { provider: 'mangaupdates', providerName: 'MangaUpdates', siteUrl: null, fetchedAt: '2026-10-13T10:00:00Z' };
    await r.fulfill({ response: res, json: body });
  });
}

/** One row of the Tracking list (lane G's `TrackerSeriesRowDto`), in sync at 120 ch / 12 vol unless told otherwise. */
function trackingRow(seriesId: string, bucket: string, extra: Record<string, unknown> = {}) {
  const state = ({ InSync: 'Ok', MangaPixerAhead: 'Ok', TrackerAhead: 'TrackerAhead', LeftAsIs: 'LeftAsIs', Held: 'Held', NoId: 'NoId',
    NotSynced: 'DontSync', Problem: 'Error', Unchecked: 'Pending' } as Record<string, string>)[bucket] ?? 'Ok';
  return {
    seriesId, title: `Synthetic Saga ${seriesId}`, nodeId: null, tracker: 'mal', remoteId: '2', remoteIdSource: 'MangaDex', state, bucket,
    errorCode: null, dontSync: false, chapters: 120, volumes: 12, allChapters: 120, allVolumes: 12, heldChapters: null, heldVolumes: null,
    totalChapters: 300, totalVolumes: 30, remoteChapters: 120, remoteVolumes: 12, remoteStatus: 'Reading', remoteMissing: false,
    dates: { supported: true, startedOn: '2026-10-01', startedOnOverridden: false, finishedOn: null, finishedOnOverridden: false,
      remoteStartedOn: '2026-10-01', remoteFinishedOn: null, pending: false },
    fromTracker: { possible: false, block: 'NotAhead' }, toTracker: { possible: false, block: 'NotAhead' },
    lastSyncAt: '2026-10-13T10:00:00Z', lastSentAt: '2026-10-13T10:00:00Z', updatedAt: '2026-10-13T10:00:00Z', ...extra,
  };
}
