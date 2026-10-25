import { test, expect, APIRequestContext, Browser, Page } from '@playwright/test';

import { expectFitsScreen } from './layout';

/**
 * Card size and list columns are per device (1.48.0): one user, three browser contexts (three devices). The slider values on
 * Settings > Library display are the device's own: a device without a copy starts from the user's last choice (the server
 * value) and keeps it; a change on one device is saved for the user but never moves another device that already has its own.
 * Runs with a throwaway user, so the admin account other specs share keeps its preferences.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const START_PASSWORD = 'DeviceStart123!';
const PASSWORD = 'DeviceUser123!';
const PREFS = '/api/v1/reading/library-preferences';

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get('/api/v1/auth/csrf');
  const { token } = await res.json();
  return { 'X-MangaPixer-Csrf': token };
}

/** Creates a reader account as the admin and gets it past its first forced password change. Returns the username. */
async function createUser(request: APIRequestContext, prefix: string): Promise<string> {
  const username = `${prefix}${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const login = await request.post('/api/v1/auth/login', { data: { username: ADMIN_USER, password: ADMIN_PASSWORD } });
  expect(login.ok(), await login.text()).toBeTruthy();
  const created = await request.post('/api/v1/admin/users', {
    headers: await csrf(request),
    data: { username, password: START_PASSWORD, isAdmin: false },
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

/** A new browser context = a new device (own cookies and localStorage), signed in as `username` on Settings. */
async function device(browser: Browser, username: string): Promise<Page> {
  const context = await browser.newContext({ baseURL: test.info().project.use.baseURL });
  const page = await context.newPage();
  const res = await page.request.post('/api/v1/auth/login', { data: { username, password: PASSWORD } });
  expect(res.ok(), await res.text()).toBeTruthy();
  await page.goto('/settings');
  await expect(page.getByTestId('device-display-card')).toBeVisible();
  return page;
}

const cardSlider = (page: Page) => page.getByTestId('settings-card-size');
const columnsSlider = (page: Page) => page.getByTestId('settings-list-columns');
const stored = (page: Page, key: string) => page.evaluate((k) => localStorage.getItem(k), key);

/** Sets a slider and waits for the preferences save that follows. */
async function choose(page: Page, slider: ReturnType<typeof cardSlider>, value: string): Promise<void> {
  const saved = page.waitForResponse((r) => r.url().endsWith(PREFS) && r.request().method() === 'PUT');
  await slider.fill(value);
  expect((await saved).ok()).toBeTruthy();
}

async function serverPrefs(page: Page): Promise<{ cardSize?: string; listColumns?: number }> {
  return (await page.request.get(PREFS)).json();
}

test('each device keeps its own card size and list columns; a new device starts from the last choice', async ({ browser, request }) => {
  const username = await createUser(request, 'devd');
  const a = await device(browser, username);
  let b: Page | null = null;
  let c: Page | null = null;
  try {
    // Device A chooses 130 px and one column.
    await choose(a, cardSlider(a), '130');
    await choose(a, columnsSlider(a), '1');
    expect(await stored(a, 'mangapixer-card-size')).toBe('130');
    expect(await stored(a, 'mangapixer-list-columns')).toBe('1');
    expect(await serverPrefs(a)).toMatchObject({ cardSize: '130', listColumns: 1 });

    // Device B is new: its first load starts from the last choice (A's) and stores it as its own.
    b = await device(browser, username);
    await expect(cardSlider(b)).toHaveValue('130');
    await expect(columnsSlider(b)).toHaveValue('1');
    expect(await stored(b, 'mangapixer-card-size')).toBe('130');

    // B chooses its own: 220 px, three columns. The user's saved value now follows B ...
    await choose(b, cardSlider(b), '220');
    await choose(b, columnsSlider(b), '3');
    expect(await serverPrefs(b)).toMatchObject({ cardSize: '220', listColumns: 3 });

    // ... but A keeps its own after a reload, and B keeps B's.
    await a.reload();
    await expect(cardSlider(a)).toHaveValue('130');
    await expect(columnsSlider(a)).toHaveValue('1');
    await b.reload();
    await expect(cardSlider(b)).toHaveValue('220');
    await expect(columnsSlider(b)).toHaveValue('3');

    // Reloading only reads: the saved value is still B's last choice.
    expect(await serverPrefs(a)).toMatchObject({ cardSize: '220', listColumns: 3 });

    // A brand new device starts from the last choice and keeps it.
    c = await device(browser, username);
    await expect(cardSlider(c)).toHaveValue('220');
    await expect(columnsSlider(c)).toHaveValue('3');
    expect(await stored(c, 'mangapixer-card-size')).toBe('220');
  } finally {
    await a.context().close();
    await b?.context().close();
    await c?.context().close();
  }
});

test('Settings > Library display says the sizes apply to this device only, and fits a phone', async ({ browser, request }) => {
  const username = await createUser(request, 'devh');
  const page = await device(browser, username);
  try {
    await expect(page.getByTestId('device-display-hint')).toContainText('this device only');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByTestId('device-display-card').scrollIntoViewIfNeeded();
    await expectFitsScreen(page, 'Settings > Library display');
  } finally {
    await page.context().close();
  }
});
