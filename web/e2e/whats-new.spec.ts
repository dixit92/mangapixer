import { test, expect, APIRequestContext, Browser, Page, Route } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * What's new (1.53.0 lane W): after an update an admin's first page shows the release notes once; "Got it" / Escape tell the server they
 * were seen; the account menu's "What's new" reopens the running version's notes; never over the reader; the dialog fits 360 / 390 px
 * and a desktop in dark and light.
 *
 * Independent of the real CHANGELOG: the notes the dialog shows are synthetic (`page.route` answers GET /admin/whats-new with them until
 * the real `seen` POST went through); the real server is asked too - a new admin's answer has no sections (every account starts as having
 * seen the running version) and `seen` answers 204. Runs with throwaway admins (the shared admin is never re-themed or marked).
 * Optional: E2E_SCREENSHOT_DIR saves the dialog at each width and theme.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];
const START_PASSWORD = 'WhatsNewStart123!';
const PASSWORD = 'WhatsNewUser123!';
const API = '/api/v1/admin/whats-new';
const APPEARANCE = '/api/v1/reading/preferences/appearance';

const NOTES = {
  current: '9.1.0',
  sections: [
    {
      version: '9.1.0', date: '2031-02-01',
      groups: [
        {
          name: 'Added',
          items: [
            { parts: [{ text: 'Shared lanterns', bold: true }, { text: ' are off until you turn them on in ' },
              { text: 'Administration', link: '/admin' }, { text: '. Each user can turn theirs off in Settings.' }] },
            { parts: [{ text: 'Search: ' }, { text: 'lantern:4', code: true },
              { text: ' finds what you lit four lanterns or more, alone or with other words, on every library you can open.' }] },
            { parts: [{ text: 'A long bullet that wraps over several lines on a phone so the dialog body has to scroll a little: '
              + 'synthetic text, synthetic text, synthetic text, synthetic text, synthetic text, synthetic text.' }] },
          ],
        },
        { name: 'Changed', items: [{ parts: [{ text: 'The lantern list keeps its place when you come back to it.' }] }] },
        { name: 'Fixed', items: [{ parts: [{ text: 'A lantern lit twice at the same moment no longer counts twice.' }] }] },
      ],
    },
    {
      version: '9.0.2', date: '2031-01-20',
      groups: [{ name: 'Fixed', items: [
        { parts: [{ text: 'The 9.0.2 fix, a synthetic line.' }] },
        { parts: [{ text: 'Another synthetic fix with a ' }, { text: '--flag', code: true }, { text: ' in it.' }] },
      ] }],
    },
  ],
};

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get('/api/v1/auth/csrf');
  const { token } = await res.json();
  return { 'X-MangaPixer-Csrf': token };
}

/** Creates an admin account as the shared admin and gets it past its first forced password change. Returns the username. */
async function createAdmin(request: APIRequestContext): Promise<string> {
  const username = `wn${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const login = await request.post('/api/v1/auth/login', { data: { username: ADMIN_USER, password: ADMIN_PASSWORD } });
  expect(login.ok(), await login.text()).toBeTruthy();
  const created = await request.post('/api/v1/admin/users', {
    headers: await csrf(request),
    data: { username, password: START_PASSWORD, isAdmin: true },
  });
  expect(created.ok(), await created.text()).toBeTruthy();
  await request.post('/api/v1/auth/logout', { headers: await csrf(request) });

  expect((await request.post('/api/v1/auth/login', { data: { username, password: START_PASSWORD } })).ok()).toBeTruthy();
  const changed = await request.post('/api/v1/auth/change-password', {
    headers: await csrf(request),
    data: { currentPassword: START_PASSWORD, newPassword: PASSWORD },
  });
  expect(changed.ok(), await changed.text()).toBeTruthy();
  await request.post('/api/v1/auth/logout', { headers: await csrf(request) });
  return username;
}

async function newPage(browser: Browser, viewport = { width: 1280, height: 900 }): Promise<Page> {
  const context = await browser.newContext({ baseURL: test.info().project.use.baseURL, viewport });
  return context.newPage();
}

/** Signs the page's context in as `username` (cookie session); does not open a page. */
async function signIn(page: Page, username: string): Promise<void> {
  const res = await page.request.post('/api/v1/auth/login', { data: { username, password: PASSWORD } });
  expect(res.ok(), await res.text()).toBeTruthy();
}

interface Traffic { gets: string[]; seen: number[] }

/**
 * Answers the admin's notes request with NOTES until a `seen` POST reached the real server (then the real answer); the menu's
 * `?current=true` gets the newest section. Records every request.
 */
async function serveNotes(page: Page): Promise<Traffic> {
  const traffic: Traffic = { gets: [], seen: [] };
  await page.route(`**${API}**`, async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (request.method() === 'POST' && url.pathname === `${API}/seen`) {
      const response = await route.fetch();
      traffic.seen.push(response.status());
      await route.fulfill({ response });
      return;
    }
    if (request.method() === 'GET' && url.pathname === API) {
      traffic.gets.push(url.search);
      if (url.searchParams.get('current') === 'true') {
        await route.fulfill({ json: { current: NOTES.current, sections: [NOTES.sections[0]] } });
      } else if (traffic.seen.length === 0) {
        await route.fulfill({ json: NOTES });
      } else {
        await route.continue();
      }
      return;
    }
    await route.continue();
  });
  return traffic;
}

const dialog = (page: Page) => page.getByRole('dialog');

test.describe('what\'s new', () => {
  test('a new admin has nothing new on the real server; no dialog shows', async ({ browser, request }) => {
    const username = await createAdmin(request);
    const page = await newPage(browser);
    await signIn(page, username);

    const real = await page.request.get(API);
    expect(real.status()).toBe(200);
    const body = await real.json();
    expect(body.current).toMatch(/^\d+\.\d+\.\d+$/);
    expect(body.sections).toEqual([]);
    expect((await page.request.get(`${API}?current=true`)).status()).toBe(200);

    const asked = page.waitForResponse((r) => new URL(r.url()).pathname === API);
    await page.goto('/');
    expect((await asked).status()).toBe(200);
    await page.waitForTimeout(1000);
    await expect(dialog(page)).toHaveCount(0);
    await page.context().close();
  });

  test('shows once after sign-in, newest first; "Got it" posts seen and the next visit shows nothing', async ({ browser, request }) => {
    const username = await createAdmin(request);
    const page = await newPage(browser);
    await signIn(page, username);
    const traffic = await serveNotes(page);

    await page.goto('/');
    await expect(dialog(page)).toBeVisible();
    await expect(page.getByTestId('whats-new-title')).toHaveText("What's new in MangaPixer 9.1.0");
    await expect(page.getByTestId('whats-new-section')).toHaveCount(2);
    await expect(page.getByTestId('whats-new-section').first()).toHaveAttribute('data-version', '9.1.0');
    await expect(dialog(page).locator('strong', { hasText: 'Shared lanterns' })).toBeVisible();
    await expect(dialog(page).locator('code', { hasText: 'lantern:4' })).toBeVisible();
    await expect(page.getByTestId('whats-new-got-it')).toBeFocused();

    await page.getByTestId('whats-new-got-it').click();
    await expect(dialog(page)).toHaveCount(0);
    await expect.poll(() => traffic.seen).toEqual([204]);

    // Another page in the app asks nothing; a new visit asks the real server, which now has nothing new.
    await page.getByRole('button', { name: 'Libraries', exact: true }).first().click();
    await page.waitForTimeout(500);
    expect(traffic.gets).toEqual(['']);
    await page.goto('/');
    await expect.poll(() => traffic.gets.length).toBe(2);
    await page.waitForTimeout(1000);
    await expect(dialog(page)).toHaveCount(0);
    await page.context().close();
  });

  test('Escape marks the notes seen; a link closes the dialog and opens the page in the app', async ({ browser, request }) => {
    const username = await createAdmin(request);
    const page = await newPage(browser);
    await signIn(page, username);
    const traffic = await serveNotes(page);

    await page.goto('/');
    await expect(dialog(page)).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(dialog(page)).toHaveCount(0);
    await expect.poll(() => traffic.seen).toEqual([204]);

    // The menu reopens the running version's notes; its in-app link opens Administration.
    await page.getByRole('button', { name: /^Account menu/ }).click();
    await page.getByTestId('nav-whats-new').click();
    await expect(dialog(page)).toBeVisible();
    await expect(page.getByTestId('whats-new-section')).toHaveCount(1);
    await dialog(page).getByRole('link', { name: 'Administration' }).click();
    await expect(dialog(page)).toHaveCount(0);
    await expect(page).toHaveURL(/\/admin$/);
    expect(traffic.gets).toEqual(['', '?current=true']);
    expect(traffic.seen).toEqual([204]);   // closing the reopened notes posts nothing
    await page.context().close();
  });

  test('never over the reader: a first page in the reader waits for the next page', async ({ browser, request }) => {
    const username = await createAdmin(request);
    const page = await newPage(browser);
    await signIn(page, username);
    await page.addInitScript(() => localStorage.setItem('mangapixer-reader-help-seen', '1'));
    const traffic = await serveNotes(page);

    await page.goto('/reader/whatsnewnoitem');
    await page.waitForTimeout(2000);
    await expect(page).toHaveURL(/\/reader\//);
    expect(traffic.gets).toEqual([]);
    await expect(page.getByTestId('whats-new-title')).toHaveCount(0);

    await page.goto('/');
    await expect(page.getByTestId('whats-new-title')).toBeVisible();
    expect(traffic.gets).toEqual(['']);
    await page.getByTestId('whats-new-got-it').click();
    await expect.poll(() => traffic.seen).toEqual([204]);
    await page.context().close();
  });

  test('the dialog fits 360 / 390 px and a desktop, in dark and light', async ({ browser, request }) => {
    test.setTimeout(120_000);
    const username = await createAdmin(request);
    const failures: string[] = [];
    for (const theme of ['dark', 'light']) {
      for (const size of [{ width: 1280, height: 900 }, { width: 390, height: 844 }, { width: 360, height: 780 }]) {
        const page = await newPage(browser, size);
        await signIn(page, username);
        const put = await page.request.put(APPEARANCE, { headers: await csrf(page.request), data: { theme, accent: 'violet' } });
        expect(put.ok(), await put.text()).toBeTruthy();
        await page.route(`**${API}**`, (route) => route.request().method() === 'POST'
          ? route.fulfill({ status: 204 })
          : route.fulfill({ json: NOTES }));

        await page.goto('/');
        await expect(dialog(page)).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
        await page.waitForTimeout(400);
        if (SHOTS) await page.screenshot({ path: `${SHOTS}/whats-new-${theme}-${size.width}.png` });

        const box = (await page.locator('mat-dialog-container').boundingBox())!;
        const button = (await page.getByTestId('whats-new-got-it').boundingBox())!;
        if (size.width < 600) {
          if (box.x < 15.5 || size.width - (box.x + box.width) < 15.5) failures.push(`${theme} ${size.width}: margins ${box.x} / ${size.width - box.x - box.width}`);
        } else if (box.width > 561) {
          failures.push(`${theme} ${size.width}: dialog ${box.width} px wide`);
        }
        if (button.height < 40) failures.push(`${theme} ${size.width}: "Got it" ${button.height} px tall`);
        try {
          await expectFitsScreen(page, `what's new ${theme}`);
        } catch (e) {
          failures.push(`${theme} ${size.width}: ${(e as Error).message.split('\n').slice(0, 6).join(' | ')}`);
        }
        await page.context().close();
      }
    }
    expect(failures, failures.join('\n')).toEqual([]);
  });
});
