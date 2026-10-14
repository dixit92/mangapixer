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

interface AdminTracker { slug: string; enabled: boolean; currentConsentVersion: number; clientId: string | null; clientSecretSet: boolean; redirectUri: string | null }

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
      await expect(panel.getByTestId('tracker-consent-admin-mal')).toContainText('For personal servers only');
      await expect(panel.getByTestId('admin-tracker-state-mal')).toHaveText('Off');

      // The switch waits for the tick.
      const toggle = panel.getByTestId('admin-tracker-allow-mal').locator('button');
      await expect(toggle).toBeDisabled();
      await panel.getByTestId('admin-tracker-consent-mal').locator('input').check();
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
      for (const id of ['admin-tracker-allow-mal', 'admin-tracker-save-mal', 'admin-tracker-copy-redirect-mal', 'admin-trackers-help']) {
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
      await page.goto('/settings');
      const panel = page.getByTestId('tracker-account-mal');
      await panel.scrollIntoViewIfNeeded();
      await expect(panel.getByTestId('tracker-consent-user-mal')).toContainText('MangaPixer never sees your password');
      const connect = panel.getByTestId('tracker-connect-mal');
      await expect(connect).toBeDisabled();
      await panel.getByTestId('tracker-consent-mal').locator('input').check();
      const { currentConsentVersion } = (await (await page.request.get('/api/v1/trackers')).json()).trackers
        .find((t: { slug: string }) => t.slug === 'mal');
      const started = page.waitForResponse((r) => r.url().endsWith('/api/v1/trackers/mal/connect/start'));
      const left = page.waitForRequest((r) => r.url().startsWith('https://myanimelist.net/'));
      await connect.click();
      const res = await started;
      expect(res.request().postDataJSON()).toEqual({ acceptedConsentVersion: currentConsentVersion });
      expect(res.status(), await res.text()).toBe(200);
      const { authorizeUrl } = await res.json();
      const url = new URL(authorizeUrl);
      expect(`${url.origin}${url.pathname}`).toBe('https://myanimelist.net/v1/oauth2/authorize');
      expect(url.searchParams.get('response_type')).toBe('code');
      expect(url.searchParams.get('client_id')).toBe('e2e-client-id');
      expect(url.searchParams.get('redirect_uri')).toBe(`${origin}/trackers/callback/mal`);
      expect(url.searchParams.get('code_challenge_method')).toBe('plain');
      expect(url.searchParams.get('state')?.length).toBeGreaterThan(20);
      // The browser went to exactly that address - answered by the test's stub, so nothing reached MyAnimeList.
      expect((await left).url()).toBe(authorizeUrl);
      expect(outside.filter((u) => !u.endsWith('/favicon.ico'))).toEqual([authorizeUrl]);
    } finally {
      await page.goto('/settings');
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
    await expect(page).toHaveURL(/\/settings$/);
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
      await page.goto('/settings');
      const panel = page.getByTestId('tracker-account-mangaupdates');
      await panel.scrollIntoViewIfNeeded();
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

  test('the tracker cards, the ids dialog and the callback page fit a phone and a tablet screen', async ({ page }) => {
    test.skip(!VOLUMES_ROOT, 'E2E_SERIES_FIXTURE_ROOT not set: no synthetic library available');
    // Contract-shaped data with long names: MyAnimeList allowed with its registration, a connected account with entries in every
    // state, the Set form and the Send all confirm open, the sync menu, the ids dialog. Nothing is changed on the server.
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

      await page.goto('/settings');
      const entries = page.getByTestId('tracker-entries-mal');
      await entries.scrollIntoViewIfNeeded();
      await expect(entries.getByTestId('tracker-entry-s1')).toBeVisible();
      await entries.getByTestId('tracker-set-s1').click();
      await entries.getByTestId('tracker-send-all-mal').click();
      await page.getByTestId('tracker-disconnect-mal').click();
      await check('connected accounts', size.width);

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
});

function connectedMal(extra: Record<string, unknown> = {}) {
  return {
    slug: 'mal', name: 'MyAnimeList', allowed: true, networkDisabledByConfig: false, connectKind: 'OAuthPkce', currentConsentVersion: 1,
    supportsPrivateEntries: false,
    account: {
      tracker: 'mal', state: 'Ok', displayName: 'a-reader-with-a-rather-long-myanimelist-name', connectedAt: '2026-10-13T10:00:00Z',
      lastSuccessAt: '2026-10-13T11:00:00Z', consentRenewalNeeded: false, addStartedSeries: true, privateEntries: true,
      requestsToday: 12, dailyRequestCap: 300,
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

/** Contract-shaped tracker answers for the layout check (lane E's DTOs), with long names. */
async function mockTrackerData(page: Page, folderId: string): Promise<void> {
  const long = 'A synthetic series title that is long enough to wrap on a phone screen';
  const entry = (seriesId: string, state: string, extra: Record<string, unknown> = {}) => ({
    seriesId, title: `${long} ${seriesId}`, nodeId: folderId, tracker: 'mal', remoteId: '2', remoteIdSource: 'MangaDex', state,
    errorCode: null, dontSync: false, localChapters: 120, localVolumes: 12, heldChapters: null, heldVolumes: null,
    lastSentChapters: 118, lastSentVolumes: 12, lastSentAt: '2026-10-13T10:00:00Z', remoteChapters: 118, remoteVolumes: 12,
    remoteStatus: 'Reading', remoteMissing: false, remoteSeenAt: '2026-10-13T10:00:00Z', updatedAt: '2026-10-13T10:00:00Z', ...extra,
  });
  const entries = [
    entry('s1', 'Held', { heldChapters: 240, heldVolumes: 24 }),
    entry('s2', 'Error', { errorCode: 'unavailable' }),
    entry('s3', 'LeftAsIs', { remoteStatus: 'OnHold' }),
    entry('s4', 'NoId', { remoteId: null }),
    entry('s5', 'Ok'),
    entry('s6', 'TrackerAhead', { remoteChapters: 300 }),
  ];
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
  await page.route(/\/api\/v1\/trackers\/mal\/entries$/, (r) => r.fulfill(json(entries)));
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
